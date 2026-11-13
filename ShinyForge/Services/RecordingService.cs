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
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            var hit = Directory.EnumerateDirectories(winget, "Gyan.FFmpeg*")
                .SelectMany(d => Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories)).FirstOrDefault();
            if (hit != null) return hit;
        }
        var local = Path.Combine(AppPaths.ToolsDir, "ffmpeg", "ffmpeg.exe");
        return File.Exists(local) ? local : AppPaths.FindInBackupTools("ffmpeg.exe");
    }

    /// <summary>Video and audio capture devices (DirectShow) reported by ffmpeg.</summary>
    public static (List<string> Video, List<string> Audio) ListDevices()
    {
        var video = new List<string>();
        var audio = new List<string>();
        var ff = FindFfmpeg();
        if (ff == null)
            return (video, audio);
        var psi = new ProcessStartInfo(ff, "-hide_banner -list_devices true -f dshow -i dummy")
        { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var text = p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        foreach (Match m in Regex.Matches(text, "\"([^\"]+)\" \\((video|audio)\\)"))
            (m.Groups[2].Value == "video" ? video : audio).Add(m.Groups[1].Value);
        return (video, audio);
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

        string input;
        if (source.StartsWith(WindowPrefix))
        {
            var title = source[WindowPrefix.Length..];
            input = $"-f gdigrab -framerate 30 -draw_mouse 0 -i title=\"{title}\"";
        }
        else
        {
            var dev = source.StartsWith(DevicePrefix) ? source[DevicePrefix.Length..] : source;
            input = $"-f dshow -rtbufsize 512M -i video=\"{dev}\"";
        }
        var hasAudio = !string.IsNullOrWhiteSpace(audio) && audio != NoAudio;
        if (hasAudio)
            input += $" -f dshow -rtbufsize 256M -i audio=\"{audio}\"";

        var output = Path.Combine(folder, "Recording_%04d.mp4");
        var args = $"-hide_banner -y {input} -map 0:v{(hasAudio ? " -map 1:a" : "")} " +
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
