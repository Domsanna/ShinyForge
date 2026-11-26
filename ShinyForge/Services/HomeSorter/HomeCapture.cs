using System.Diagnostics;
using ShinyForge.Core;

namespace ShinyForge.Services.HomeSorter;

/// <summary>
/// The screen of the console running Pokémon HOME, from its capture card: one FFmpeg reads the card at full 1920x1080
/// (small text like the box counter and OT ID needs it) as JPEG pictures, 10 a second. One reader per card is shared by
/// everything in ShinyForge that wants the HOME screen (the live view on the HOME page and in the sorter, and the sorter
/// itself): cheap "USB Video" cards can only be opened once. Each <see cref="Start"/> must be matched by a Dispose; the
/// reader stops when the last one lets go.
/// </summary>
public sealed class HomeCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly Process _ffmpeg;
    private byte[]? _frame;
    private DateTime _frameAt;
    public string Device { get; }
    /// <summary>
    /// FFmpeg said the card is used by another program. Cheap USB cards (MS2109 "USB Video") can only be open in one program;
    /// Elgato cards can be shared.
    /// </summary>
    public bool InUse { get; private set; }

    /// <summary>Why there's no picture, for the user.</summary>
    public string NoPictureReason() => InUse
        ? $"The capture card \"{Device}\" is in use by another program (Streamlabs / OBS / a camera app) - this kind of card can only be open " +
          "in one program. Close that program (or remove the card from its scene) and try again."
        : $"The capture card \"{Device}\" shows nothing. Is the console on and its dock connected?";

    private HomeCapture(Process p, string device)
    {
        _ffmpeg = p;
        Device = device;
        new Thread(ReadFrames) { IsBackground = true, Name = "HOME capture" }.Start();
    }

    /// <summary>
    /// The card to watch: the one picked in the HOME sorter, else a Switch 2 card ("USB Video" / MS2109 - HOME usually runs
    /// there), else the Switch 1 card ShinyForge uses for screenshots.
    /// </summary>
    public static string? DefaultDevice()
    {
        var present = Equipment.VideoSources();
        var set = AppSettings.Current.HomeSorter.Device;
        if (present.Contains(set)) return set;
        return present.FirstOrDefault(d => d.Contains("USB Video", StringComparison.OrdinalIgnoreCase) || d.Contains("MS2109", StringComparison.OrdinalIgnoreCase))
               ?? SwitchScreen.Device() ?? present.FirstOrDefault();
    }

    private static readonly Dictionary<string, HomeCapture> Open = new();
    private static readonly object OpenGate = new();
    private int _users;

    /// <summary>The card's shared reader: the running one, or a new one. Dispose it when done.</summary>
    public static HomeCapture Start(string device)
    {
        lock (OpenGate)
        {
            if (Open.TryGetValue(device, out var running) && !running._ffmpeg.HasExited)
            {
                running._users++;
                return running;
            }
            var cap = Launch(device);
            cap._users = 1;
            Open[device] = cap;
            return cap;
        }
    }

    private static HomeCapture Launch(string device)
    {
        if (RecordingService.FindFfmpeg() is not { } ff) throw new FileNotFoundException("FFmpeg isn't installed - add it in Tools & Updates.");
        var psi = new ProcessStartInfo(ff)
        { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error" }.Concat(SwitchScreen.InputArgs(device))
                     .Concat(new[] { "-an", "-vf", "fps=10,scale=1920:1080", "-pix_fmt", "yuvj420p", "-c:v", "mjpeg", "-q:v", "2", "-f", "image2pipe", "pipe:1" }))
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var cap = new HomeCapture(p, device);
        var lastLogged = DateTime.MinValue;
        var skipped = 0;
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            // a half-sent MJPEG frame makes FFmpeg print the same decode error many times a second, which filled the app log
            // (GitHub issue #3): log those at most once a minute, with how many were left out
            var decodeNoise = e.Data.Contains("No JPEG data", StringComparison.OrdinalIgnoreCase) ||
                              e.Data.Contains("Error submitting packet to decoder", StringComparison.OrdinalIgnoreCase) ||
                              e.Data.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase);
            if (decodeNoise && (DateTime.UtcNow - lastLogged).TotalSeconds < 60) { skipped++; return; }
            if (decodeNoise) { lastLogged = DateTime.UtcNow; if (skipped > 0) AppPaths.Log($"HOME capture FFmpeg: ({skipped} more frame decode errors in the last minute - a damaged frame is skipped)"); skipped = 0; }
            AppPaths.Log("HOME capture FFmpeg: " + e.Data);
            if (e.Data.Contains("already in use", StringComparison.OrdinalIgnoreCase)) cap.InUse = true;
        };
        p.BeginErrorReadLine();
        return cap;
    }

    private void ReadFrames()
    {
        var s = new BufferedStream(_ffmpeg.StandardOutput.BaseStream, 1 << 17);
        byte Next() => s.ReadByte() is var b and >= 0 ? (byte)b : throw new EndOfStreamException();
        try
        {
            while (true)
            {
                var jpg = Jpeg.Read(Next);
                if (jpg == null) continue;
                lock (_gate) { _frame = jpg; _frameAt = DateTime.UtcNow; }
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException) { }
    }

    /// <summary>
    /// A picture that left the card at least <paramref name="delayMs"/> after this call (the capture-card delay: what a
    /// button press just did is on it). Null when the card shows nothing.
    /// </summary>
    public RgbImage? Grab(int delayMs, int timeoutMs = 5000)
    {
        var wanted = DateTime.UtcNow.AddMilliseconds(delayMs);
        var until = DateTime.UtcNow.AddMilliseconds(delayMs + timeoutMs);
        while (DateTime.UtcNow < until)
        {
            byte[]? jpg = null;
            lock (_gate) if (_frame != null && _frameAt >= wanted) jpg = _frame;
            if (jpg != null) return ConsoleFtpLauncher.Decode?.Invoke(jpg);
            if (_ffmpeg.HasExited) break;
            Thread.Sleep(15);
        }
        return null;
    }

    /// <summary>The last picture as JPEG bytes (for error screenshots).</summary>
    public byte[]? LastJpeg() { lock (_gate) return _frame; }

    /// <summary>When the picture was taken (UTC), to tell a new picture from the last one.</summary>
    public DateTime LastAt { get { lock (_gate) return _frameAt; } }

    /// <summary>FFmpeg stopped (the card is busy or gone).</summary>
    public bool Stopped => _ffmpeg.HasExited;

    public void Dispose()
    {
        lock (OpenGate)
        {
            if (--_users > 0) return;
            if (Open.TryGetValue(Device, out var c) && c == this) Open.Remove(Device);
        }
        try { if (!_ffmpeg.HasExited) { _ffmpeg.StandardInput.Write('q'); if (!_ffmpeg.WaitForExit(1500)) _ffmpeg.Kill(); } } catch { }
        _ffmpeg.Dispose();
    }
}
