using System.Diagnostics;
using System.Text.RegularExpressions;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Continuous gameplay recording with ffmpeg, split into back-to-back segments (5/10/15/20/30 minutes) with no gap
/// between files. Files are numbered automatically: Recording_0001.mp4, Recording_0002.mp4, ... (continuing after
/// any recordings already in the folder).
/// </summary>
public sealed class RecordingService
{
    public const string WindowPrefix = "Window: ";
    public const string DevicePrefix = "Capture device: ";
    public const string NoAudio = "No audio";

    private Process? _ffmpeg;
    public bool IsRecording => _ffmpeg is { HasExited: false };
    public event Action<string>? Status;

    public static string? FindFfmpeg()
    {
        var configured = AppSettings.Current.FfmpegPath;
        if (File.Exists(configured))
            return configured;
        if (OsInfo.Which("ffmpeg") is { } onPath)
            return onPath;
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (OsInfo.IsWindows && Directory.Exists(winget))
        {
            var hit = Directory.EnumerateDirectories(winget, "Gyan.FFmpeg*")
                .SelectMany(d => Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories)).FirstOrDefault();
            if (hit != null) return hit;
        }
        var local = Path.Combine(AppPaths.ToolsDir, "ffmpeg", OsInfo.Exe("ffmpeg"));
        return File.Exists(local) ? local : AppPaths.FindInBackupTools(OsInfo.Exe("ffmpeg"));
    }

    /// <summary>
    /// Video and audio capture devices reported by ffmpeg: DirectShow on Windows, AVFoundation on macOS (capture cards,
    /// cameras and "Capture screen N"), V4L2 devices + the X11 screen and PulseAudio sources on Linux.
    /// </summary>
    public static (List<string> Video, List<string> Audio) ListDevices()
    {
        var video = new List<string>();
        var audio = new List<string>();
        var ff = FindFfmpeg();
        if (OsInfo.IsLinux)
        {
            try { video.AddRange(Directory.EnumerateFiles("/dev", "video*").Order().Select(d => $"{d} ({V4lName(d)})")); } catch { }
            if (Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } display) video.Add($"{LinuxScreen} {display}");
            audio.Add("default");
            try
            {
                using var pa = Process.Start(new ProcessStartInfo("pactl", "list short sources") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
                foreach (var line in pa.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    if (line.Split('\t') is { Length: > 1 } cols) audio.Add(cols[1]);
                pa.WaitForExit(5000);
            }
            catch { /* no PulseAudio / PipeWire tools */ }
            return (video, audio);
        }
        if (ff == null)
            return (video, audio);
        var psi = new ProcessStartInfo(ff, OsInfo.IsMac ? "-hide_banner -f avfoundation -list_devices true -i \"\"" : "-hide_banner -list_devices true -f dshow -i dummy")
        { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var text = p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        if (OsInfo.IsMac)
        {
            // [AVFoundation indev @ 0x...] AVFoundation video devices:  /  [0] FaceTime HD Camera  /  ... audio devices:
            var list = video;
            foreach (var line in text.Split('\n'))
            {
                if (line.Contains("video devices")) list = video;
                else if (line.Contains("audio devices")) list = audio;
                else if (Regex.Match(line, @"\]\s\[(\d+)\]\s(.+)$") is { Success: true } m) list.Add(m.Groups[2].Value.Trim());
            }
            return (video, audio);
        }
        foreach (Match m in Regex.Matches(text, "\"([^\"]+)\" \\((video|audio)\\)"))
            (m.Groups[2].Value == "video" ? video : audio).Add(m.Groups[1].Value);
        return (video, audio);
    }

    /// <summary>Linux: the X11 screen as a "device" (ffmpeg x11grab).</summary>
    public const string LinuxScreen = "Screen (X11)";

    private static string V4lName(string dev)
    {
        try { return File.ReadAllText($"/sys/class/video4linux/{Path.GetFileName(dev)}/name").Trim(); } catch { return "video device"; }
    }

    /// <summary>
    /// ffmpeg input arguments for a capture source and audio device on this OS, and the -map options that go with them.
    /// <paramref name="bigBuffers"/>: the longer DirectShow buffers used for full recordings on Windows.
    /// </summary>
    public static (string Input, string Maps, bool HasAudio) BuildInput(string source, string audio, bool bigBuffers)
    {
        var hasAudio = !string.IsNullOrWhiteSpace(audio) && audio != NoAudio;
        var dev = source.StartsWith(DevicePrefix) ? source[DevicePrefix.Length..] : source;
        if (OsInfo.IsMac)
        {
            // AVFoundation takes video and audio as one input: "video name:audio name".
            var input = $"-f avfoundation -framerate 30 -capture_cursor 0 -i \"{dev}:{(hasAudio ? audio : "none")}\"";
            return (input, hasAudio ? "-map 0:v -map 0:a" : "-map 0:v", hasAudio);
        }
        if (OsInfo.IsLinux)
        {
            var video = dev.StartsWith(LinuxScreen)
                ? $"-f x11grab -framerate 30 -draw_mouse 0 -i {dev[LinuxScreen.Length..].Trim()}"
                : $"-f v4l2 -framerate 30 -i {dev.Split(' ')[0]}";
            return (video + (hasAudio ? $" -f pulse -i \"{audio}\"" : ""), hasAudio ? "-map 0:v -map 1:a" : "-map 0:v", hasAudio);
        }
        string win = source.StartsWith(WindowPrefix)
            ? $"-f gdigrab -framerate 30 -draw_mouse 0 -i title=\"{source[WindowPrefix.Length..]}\""
            : $"-f dshow -rtbufsize {(bigBuffers ? "512M" : "256M")} -i video=\"{dev}\"";
        if (hasAudio) win += $" -f dshow -rtbufsize {(bigBuffers ? "256M" : "128M")} -i audio=\"{audio}\"";
        return (win, hasAudio ? "-map 0:v -map 1:a" : "-map 0:v", hasAudio);
    }

    public static int NextNumber(string folder)
    {
        if (!Directory.Exists(folder))
            return 1;
        int max = 0;
        foreach (var f in Directory.EnumerateFiles(folder, "Recording_*.mp4"))
        {
            var m = Regex.Match(Path.GetFileNameWithoutExtension(f), @"Recording_(\d+)");
            if (m.Success) max = Math.Max(max, int.Parse(m.Groups[1].Value));
        }
        return max + 1;
    }

    public void Start(string source, string audio, string folder, int minutes) => Start(source, audio, folder, minutes, minutes * 60);

    public void Start(string source, string audio, string folder, int minutes, int segmentSeconds)
    {
        if (IsRecording)
            throw new InvalidOperationException("Already recording.");
        var ff = FindFfmpeg() ?? throw new FileNotFoundException("ffmpeg is not installed.");
        Directory.CreateDirectory(folder);
        int start = NextNumber(folder);

        var (input, maps, hasAudio) = BuildInput(source, audio, bigBuffers: true);
        var output = Path.Combine(folder, "Recording_%04d.mp4");
        var args = $"-hide_banner -y {input} {maps} " +
                   "-c:v libx264 -preset veryfast -crf 21 -pix_fmt yuv420p -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" " +
                   (hasAudio ? "-c:a aac -b:a 192k " : "") +
                   $"-f segment -segment_time {segmentSeconds} -reset_timestamps 1 -segment_start_number {start} " +
                   $"-segment_format mp4 -strftime 0 \"{output}\"";

        AppPaths.Log("ffmpeg " + args);
        var psi = new ProcessStartInfo(ff, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg didn't start.");
        _ffmpeg.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            if (e.Data.Contains("Opening '"))
                Status?.Invoke("Writing " + Path.GetFileName(Regex.Match(e.Data, "'([^']+)'").Groups[1].Value));
            else if (e.Data.Contains("Error", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("Could not", StringComparison.OrdinalIgnoreCase))
                Status?.Invoke(e.Data.Trim());
            AppPaths.Log("ffmpeg: " + e.Data);
        };
        _ffmpeg.BeginErrorReadLine();
        _last = (source, audio, folder, minutes, segmentSeconds);
        _wanted = true;
        _lastSize = -1;
        _lastGrowth = DateTime.Now;
        Status?.Invoke($"Recording - {minutes}-minute segments starting at Recording_{start:0000}.mp4");
    }

    // ------------------------------------------------------------------ health check (used by HealthMonitor)
    private (string Source, string Audio, string Folder, int Minutes, int Seconds)? _last;
    private bool _wanted;          // the user wants a recording running (Start called, Stop not)
    private int _restarts;
    private long _lastSize = -1;
    private DateTime _lastGrowth = DateTime.Now;
    /// <summary>All recorders of this run (one per open game page).</summary>
    public static readonly List<RecordingService> Instances = new();

    public RecordingService() { lock (Instances) Instances.Add(this); }

    /// <summary>
    /// Checks a running recording: ffmpeg must still be alive and the current file must keep growing.
    /// If ffmpeg died unexpectedly it is restarted automatically (numbering continues). Null = nothing to report.
    /// </summary>
    public (bool Error, string Message)? CheckHealth()
    {
        if (!_wanted || _last is not { } l) return null;
        if (!IsRecording)
        {
            if (_restarts >= 5) return (true, "Recording stopped unexpectedly and couldn't be restarted - check the capture device / disk.");
            _restarts++;
            try
            {
                _wanted = false;
                Start(l.Source, l.Audio, l.Folder, l.Minutes, l.Seconds);
                return (true, $"Recording had stopped unexpectedly (ffmpeg exited) - restarted automatically (attempt {_restarts}).");
            }
            catch (Exception e) { return (true, "Recording stopped unexpectedly and restarting failed: " + e.Message); }
        }
        var newest = new DirectoryInfo(l.Folder).EnumerateFiles("Recording_*.mp4").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        var size = newest?.Length ?? 0;
        if (size != _lastSize) { _lastSize = size; _lastGrowth = DateTime.Now; return null; }
        if ((DateTime.Now - _lastGrowth).TotalSeconds > 120)
            return (false, $"The recording file {newest?.Name ?? "(none)"} hasn't grown for {(int)(DateTime.Now - _lastGrowth).TotalMinutes} min - is the capture source still sending video?");
        return null;
    }

    /// <summary>Stops gracefully ("q") so the last segment is finalized and playable.</summary>
    public void Stop()
    {
        _wanted = false;
        if (!IsRecording)
            return;
        try
        {
            _ffmpeg!.StandardInput.WriteLine("q");
            if (!_ffmpeg.WaitForExit(15000))
                _ffmpeg.Kill();
        }
        catch { try { _ffmpeg?.Kill(); } catch { } }
        Status?.Invoke("Recording stopped.");
    }
}
