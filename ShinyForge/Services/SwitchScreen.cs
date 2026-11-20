using System.Diagnostics;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// The Switch's screen from its capture card, for when sys-botbase can't be asked for it. sys-botbase-cpp (3.x) has a fixed
/// 3 MB of memory: once a game or the Homebrew Menu runs, a screenshot (pixelPeek, a 512 KB buffer) fails with
/// std::bad_alloc, its command thread dies and it answers nothing until the Switch restarts (its log.txt on the SD card).
/// One FFmpeg reads the card (5 pictures a second, 1280x720 like sys-botbase's screenshots) while screenshots are wanted
/// and stops <see cref="IdleStop"/> after the last one. Capture cards can be shared: Streamlabs and the recording / shiny-clip
/// FFmpeg keep working.
/// </summary>
public static class SwitchScreen
{
    private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(30);
    private static readonly object Gate = new();
    private static Process? _ffmpeg;
    private static string? _device;
    private static byte[]? _frame;
    private static DateTime _frameAt, _lastWanted;
    private static System.Threading.Timer? _idle;
    private static (List<string> List, DateTime At)? _devices;

    static SwitchScreen() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop(); // never leave the FFmpeg behind

    /// <summary>
    /// The Switch 1's capture device: the one set in Settings, else the device a Switch game records from (not the Switch 2's
    /// "USB Video" card), else an Elgato / "Game Capture" card. Null when none is plugged in.
    /// </summary>
    public static string? Device()
    {
        var present = Devices();
        if (present.Count == 0) return null;
        var s = AppSettings.Current;
        if (present.Contains(s.SwitchScreenDevice)) return s.SwitchScreenDevice;
        var fromGames = GameCatalog.ForSwitch
            .Select(g => s.Games.TryGetValue(g.Id, out var gs) ? gs.VideoSource : "")
            .Where(v => v.StartsWith(RecordingService.DevicePrefix))
            .Select(v => v[RecordingService.DevicePrefix.Length..])
            .Where(d => present.Contains(d) && !IsSwitch2Card(d))
            .GroupBy(d => d).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        return fromGames ?? StreamlabsService.GuessDevices(present).Switch1;
    }

    private static bool IsSwitch2Card(string d) =>
        d.Contains("USB Video", StringComparison.OrdinalIgnoreCase) || d.Contains("MS2109", StringComparison.OrdinalIgnoreCase);

    /// <summary>FFmpeg's video devices, looked up at most every 2 minutes (listing them starts FFmpeg).</summary>
    private static List<string> Devices()
    {
        lock (Gate)
        {
            if (_devices is { } d && DateTime.Now - d.At < TimeSpan.FromMinutes(2)) return d.List;
            var list = Equipment.VideoSources();
            _devices = (list, DateTime.Now);
            return list;
        }
    }

    /// <summary>
    /// A picture of the Switch's screen taken AFTER this call (so it shows what a button press just did), as a 1280x720 JPEG,
    /// or null when there's no capture card or it shows nothing.
    /// </summary>
    public static byte[]? Grab(int timeoutMs = 6000)
    {
        var device = Device();
        if (device == null || RecordingService.FindFfmpeg() is not { } ff) return null;
        var asked = DateTime.UtcNow;
        lock (Gate)
        {
            _lastWanted = DateTime.UtcNow;
            if (_ffmpeg is not { HasExited: false } || _device != device) Start(ff, device);
        }
        // a picture that left the card after the request (FFmpeg + the card add ~0.1-0.2 s)
        var wanted = asked.AddMilliseconds(250);
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            lock (Gate)
            {
                if (_frame != null && _frameAt >= wanted) return _frame;
                if (_ffmpeg is not { HasExited: false }) break;
            }
            Thread.Sleep(40);
        }
        AppPaths.Log($"No picture from the capture card \"{device}\" for the Switch screenshot.");
        return null;
    }

    /// <summary>FFmpeg's input arguments for a capture device on this OS (also handed to the Python hunter in shinyforge.json).</summary>
    public static string[] InputArgs(string device) =>
        OsInfo.IsMac ? new[] { "-f", "avfoundation", "-framerate", "30", "-i", device + ":none" }
        : OsInfo.IsLinux ? new[] { "-f", "v4l2", "-framerate", "30", "-i", device.Split(' ')[0] }
        : new[] { "-f", "dshow", "-rtbufsize", "64M", "-i", "video=" + device };

    /// <summary>The rest of the reader's FFmpeg command: 5 JPEG pictures a second, 1280x720, to standard output.</summary>
    public static readonly string[] OutputArgs =
        { "-an", "-vf", "fps=5,scale=1280:720", "-pix_fmt", "yuvj420p", "-c:v", "mjpeg", "-q:v", "3", "-f", "image2pipe", "pipe:1" };

    private static void Start(string ff, string device)
    {
        StopLocked();
        var psi = new ProcessStartInfo(ff)
        { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error" }.Concat(InputArgs(device)).Concat(OutputArgs)) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _ffmpeg = p;
        _device = device;
        _frame = null;
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppPaths.Log("Switch screen FFmpeg: " + e.Data); };
        p.BeginErrorReadLine();
        new Thread(() => ReadFrames(p)) { IsBackground = true, Name = "Switch screen" }.Start();
        _idle ??= new System.Threading.Timer(_ => StopWhenIdle(), null, 5000, 5000);
    }

    private static void ReadFrames(Process p)
    {
        var s = new BufferedStream(p.StandardOutput.BaseStream, 1 << 16);
        byte Next() => s.ReadByte() is var b and >= 0 ? (byte)b : throw new EndOfStreamException();
        try
        {
            while (true)
            {
                var jpg = Jpeg.Read(Next);
                if (jpg == null) continue;
                lock (Gate)
                {
                    if (_ffmpeg != p) return;
                    _frame = jpg;
                    _frameAt = DateTime.UtcNow;
                }
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException) { } // FFmpeg stopped
    }

    private static void StopWhenIdle()
    {
        lock (Gate)
            if (_ffmpeg != null && DateTime.UtcNow - _lastWanted > IdleStop) StopLocked();
    }

    /// <summary>Stops the capture-card reader (ShinyForge closing; it also stops by itself when not used).</summary>
    public static void Stop() { lock (Gate) StopLocked(); }

    private static void StopLocked()
    {
        if (_ffmpeg is { } p)
        {
            try { if (!p.HasExited) { p.StandardInput.Write('q'); if (!p.WaitForExit(1500)) p.Kill(); } } catch { }
            p.Dispose();
        }
        _ffmpeg = null;
        _frame = null;
    }
}

/// <summary>Reads one JPEG from a byte stream by its own structure (no length is sent, and it may hold any byte value).</summary>
internal static class Jpeg
{
    private const int MaxSize = 0x400000;

    /// <summary>
    /// The next JPEG (which must start right here with FF D8), marker segments up to end-of-image, with the compressed data after
    /// each start-of-scan copied until the next real marker (FF 00 is a stuffed FF, FF D0-D7 are restart markers). Null when
    /// the bytes aren't a JPEG.
    /// </summary>
    public static byte[]? Read(Func<byte> next)
    {
        var jpg = new MemoryStream(0x40000);
        if (next() != 0xFF || next() != 0xD8) return null;
        jpg.WriteByte(0xFF); jpg.WriteByte(0xD8);
        var marker = Marker(next, jpg);
        while (marker != 0xD9)
        {
            if (jpg.Length > MaxSize) throw new InvalidDataException("Not a screenshot (too big).");
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7) { marker = Marker(next, jpg); continue; } // no length
            int hi = next(), lo = next(), len = hi << 8 | lo;
            if (len < 2) throw new InvalidDataException("Broken JPEG segment.");
            jpg.WriteByte((byte)hi); jpg.WriteByte((byte)lo);
            for (var i = 2; i < len; i++) jpg.WriteByte(next());
            marker = marker == 0xDA ? Scan(next, jpg) : Marker(next, jpg);
        }
        return jpg.ToArray();
    }

    private static int Marker(Func<byte> next, MemoryStream jpg)
    {
        if (next() != 0xFF) throw new InvalidDataException("Broken JPEG (no marker).");
        int m;
        do m = next(); while (m == 0xFF); // fill bytes
        jpg.WriteByte(0xFF); jpg.WriteByte((byte)m);
        return m;
    }

    private static int Scan(Func<byte> next, MemoryStream jpg)
    {
        while (true)
        {
            var b = next();
            jpg.WriteByte(b);
            if (b != 0xFF) continue;
            int m;
            do m = next(); while (m == 0xFF);
            jpg.WriteByte((byte)m);
            if (m != 0x00 && m is not (>= 0xD0 and <= 0xD7)) return m;
            if (jpg.Length > MaxSize) throw new InvalidDataException("Not a screenshot (too big).");
        }
    }
}
