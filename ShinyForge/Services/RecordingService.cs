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

    /// <summary>Windows: the system path of a DirectShow video device (FFmpeg's "Alternative name" without "@device_pnp_").</summary>
    public static string? DevicePath(string device)
    {
        var ff = FindFfmpeg();
        if (ff == null || !OsInfo.IsWindows) return null;
        using var p = Process.Start(new ProcessStartInfo(ff, "-hide_banner -list_devices true -f dshow -i dummy") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        var text = p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        var m = Regex.Match(text, "\"" + Regex.Escape(device) + "\" \\(video\\)\\s*\\r?\\n[^\\n]*Alternative name \"@device_pnp_([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
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

    // ------------------------------------------------------------------ screens, sound and console frames
    public const string Top3DS = WindowPrefix + "3DS Capture - Top";
    public const string Bottom3DS = WindowPrefix + "3DS Capture - Bottom";
    /// <summary>Both 3DS Capture windows, stacked like a real DS / 3DS (games from Gen 4 to 7 on DS and 3DS).</summary>
    public const string Both3DS = WindowPrefix + "3DS Capture - Top + Bottom (stacked)";
    /// <summary>The sound 3DS Capture plays (it receives the 3DS audio over the USB cable and plays it on the speakers).</summary>
    public const string AppAudio3DS = "3DS Capture sound (from the USB cable)";
    public const string Capture3DSProcess = "3ds_capture";

    /// <summary>Everything that decides what a recording looks and sounds like.</summary>
    public sealed record CaptureSpec(string Source, string Audio, Platform Platform = Platform.Switch, string Overlay = "", bool CropToGame = true);

    /// <summary>The FFmpeg inputs + filter graph for a capture, and the 3DS Capture sound grabber when that is used (start it before FFmpeg).</summary>
    public sealed record CaptureCommand(string Args, bool HasAudio, ProcessAudioCapture? AppAudio, string Description);

    /// <summary>Games on DS and 3DS (Gen 4-7) have two screens; Game Boy games and Switch games one.</summary>
    public static bool HasTwoScreens(Platform p) => p is Platform.NDS or Platform.N3DS;

    /// <summary>
    /// The black border around older games inside the 3DS top screen, in pixels of the 400x240 screen (left, top, right, bottom).
    /// Used by recordings (FFmpeg crop) and by the Streamlabs scenes (source crop).
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom) TopScreenBorder(Platform p) => p switch
    {
        Platform.NDS => (40, 0, 40, 0),                    // DS mode: 320x240 in the middle (measured on Pokémon Pearl: x 40-359; bottom fills 320x240)
        Platform.GB or Platform.GBC => (67, 0, 66, 0),     // Virtual Console: 160x144 scaled to 267x240 (measured on Pokémon Red: x 67-333)
        Platform.GBA => (20, 0, 20, 0),                    // 240x160 enlarged 1.5x to 360x240 (measured on Pokémon Ruby: x 20-379)
        _ => (0, 0, 0, 0),
    };

    /// <summary>FFmpeg crop for <see cref="TopScreenBorder"/> (works at any 3DS Capture window size).</summary>
    private static string? TopCrop(Platform p)
    {
        var (l, t, r, b) = TopScreenBorder(p);
        if (l + t + r + b == 0) return null;
        return $"crop=trunc(iw*{400 - l - r}/800)*2:trunc(ih*{240 - t - b}/480)*2:iw*{l}/400:ih*{t}/240";
    }

    public static CaptureCommand BuildCapture(CaptureSpec s, bool bigBuffers)
    {
        var inputs = new List<string>();   // ffmpeg input arguments, in order
        int count = 0;                      // number of ffmpeg inputs so far (one string can hold two on macOS/Linux)
        var videos = new List<(int Index, string Source)>();
        string audioMap = "";
        ProcessAudioCapture? appAudio = null;
        bool hasAudio = !string.IsNullOrWhiteSpace(s.Audio) && s.Audio != NoAudio;
        bool isDevice = !s.Source.StartsWith(WindowPrefix);
        if (OsInfo.IsWindows)
        {
            var sources = s.Source == Both3DS ? new[] { Top3DS, Bottom3DS } : new[] { s.Source };
            foreach (var v in sources)
            {
                inputs.Add(v.StartsWith(WindowPrefix)
                    ? $"-f gdigrab -framerate 30 -draw_mouse 0 -thread_queue_size 512 -i title=\"{WindowTitle(v[WindowPrefix.Length..])}\""
                    : $"-f dshow -rtbufsize {(bigBuffers ? "512M" : "256M")} -i video=\"{(v.StartsWith(DevicePrefix) ? v[DevicePrefix.Length..] : v)}\"");
                videos.Add((count++, v));
            }
            if (s.Audio == AppAudio3DS)
            {
                appAudio = new ProcessAudioCapture(Capture3DSProcess);
                inputs.Add(appAudio.FfmpegInput);
            }
            else if (hasAudio)
                inputs.Add($"-f dshow -rtbufsize {(bigBuffers ? "256M" : "128M")} -i audio=\"{s.Audio}\"");
            if (hasAudio) audioMap = $"-map {count++}:a";
        }
        else
        {
            // macOS: one AVFoundation input with picture + sound; Linux: the video input, then PulseAudio
            var (input, _, a) = BuildInput(s.Source, s.Audio, bigBuffers);
            inputs.Add(input);
            videos.Add((0, s.Source));
            hasAudio = a;
            count = OsInfo.IsLinux && a ? 2 : 1;
            if (a) audioMap = OsInfo.IsMac ? "-map 0:a" : "-map 1:a";
        }
        int nextIndex = count;

        // per-screen picture: crop the black border of 3DS Capture's top window for DS / Game Boy games
        string Prep(int k)
        {
            var (idx, src) = videos[k];
            var crop = s.CropToGame && src == Top3DS ? TopCrop(s.Platform) : null;
            return $"[{idx}:v]" + (crop != null ? crop + "," : "");
        }

        var frame = ConsoleFrames.Get(s.Overlay);
        string graph, what;
        if (frame != null && ConsoleFrames.Exists(frame))
        {
            inputs.Add($"-loop 1 -framerate 30 -i \"{ConsoleFrames.PathOf(frame)}\"");
            int frameIdx = nextIndex;
            // fit the console into 1920x1080 (even size for H.264), then put each screen exactly into its hole, frame on top
            double scale = Math.Min(1.0, Math.Min(1920.0 / frame.Width, 1080.0 / frame.Height));
            int cw = Even(frame.Width * scale), ch = Even(frame.Height * scale);
            double sx = (double)cw / frame.Width, sy = (double)ch / frame.Height;
            var parts = new List<string> { $"color=c=black:s={cw}x{ch}:r={(isDevice ? 60 : 30)}[bg]" };
            string last = "bg";
            for (int k = 0; k < videos.Count && k < frame.Screens.Length; k++)
            {
                var r = frame.Screens[k];
                // cover the hole completely: round outwards (the frame's opaque edge hides the extra pixel)
                int x0 = (int)Math.Floor(r.X * sx), y0 = (int)Math.Floor(r.Y * sy);
                int x1 = (int)Math.Ceiling((r.X + r.Width) * sx), y1 = (int)Math.Ceiling((r.Y + r.Height) * sy);
                parts.Add($"{Prep(k)}scale={x1 - x0}:{y1 - y0}:flags=lanczos,setsar=1[s{k}]");
                parts.Add($"[{last}][s{k}]overlay={x0}:{y0}:shortest=1[c{k}]");
                last = $"c{k}";
            }
            parts.Add($"[{frameIdx}:v]scale={cw}:{ch},format=rgba[fr]");
            parts.Add($"[{last}][fr]overlay=0:0:shortest=1,format=yuv420p[v]");
            graph = string.Join(";", parts);
            what = $"{frame.Label} frame, {cw}x{ch}";
        }
        else if (videos.Count == 2)
        {
            // top above bottom like a real console; the narrower bottom screen is centred (2x, sharp pixels)
            bool ds = s.Platform == Platform.NDS && s.CropToGame;
            int tw = ds ? 320 : 400;
            graph = $"{Prep(0)}scale={tw}:240:flags=neighbor,setsar=1[t];" +
                    $"{Prep(1)}scale=320:240:flags=neighbor,pad={tw}:240:(ow-iw)/2:0,setsar=1[b];" +
                    $"[t][b]vstack=inputs=2,scale=iw*2:ih*2:flags=neighbor,format=yuv420p[v]";
            what = $"top + bottom screen, {tw * 2}x960";
        }
        else
        {
            graph = $"{Prep(0)}scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p[v]";
            what = "one screen";
        }
        if (frame != null && !ConsoleFrames.Exists(frame))
            AppPaths.Log($"Console frame {ConsoleFrames.PathOf(frame)} not found - recording without it");
        var args = $"{string.Join(" ", inputs)} -filter_complex \"{graph}\" -map \"[v]\" {audioMap}".Trim();
        return new CaptureCommand(args, hasAudio, appAudio, what + (hasAudio ? $", sound: {s.Audio}" : ", no sound"));
    }

    /// <summary>
    /// The full title of the window that starts with <paramref name="start"/>: with a 3DS plugged in, 3DS Capture adds the
    /// board's id ("3DS Capture - Top  ::  [abc123]"), and FFmpeg needs the exact title. Unchanged if no such window is open.
    /// </summary>
    public static string WindowTitle(string start) => FindWindow(start)?.Title ?? start;

    /// <summary>The window whose title is (or starts with) <paramref name="start"/>: its full title and inside size.</summary>
    public static (string Title, int Width, int Height)? FindWindow(string start)
    {
        if (!OsInfo.IsWindows) return null;
        (string, int, int)? found = null;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            var t = sb.ToString();
            if (t != start && !(found == null && t.StartsWith(start + " "))) return true;
            // a minimized window can't be captured (it records nothing): bring it back without taking the focus
            if (IsIconic(h)) ShowWindow(h, 4 /* SW_SHOWNOACTIVATE */);
            GetClientRect(h, out var rc);
            found = (t, rc.Right - rc.Left, rc.Bottom - rc.Top);
            return t != start;
        }, IntPtr.Zero);
        return found;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    private struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);

    private static int Even(double v) =>Math.Max(2, (int)Math.Round(v / 2) * 2);

    /// <summary>
    /// The audio that belongs to a capture source: 3DS Capture's own sound for the 3DS windows, otherwise the capture card's
    /// audio device (same name as the video device, e.g. "Game Capture 4K60 Pro MK.2 Audio"), or any "Capture" device.
    /// </summary>
    public static string DefaultAudio(string source, IReadOnlyList<string> audios)
    {
        if (source.StartsWith(WindowPrefix + "3DS Capture") && OsInfo.IsWindows) return AppAudio3DS;
        var dev = source.StartsWith(DevicePrefix) ? source[DevicePrefix.Length..] : source;
        return audios.FirstOrDefault(a => a == dev + " Audio")
            ?? audios.FirstOrDefault(a => dev.Length > 0 && a.StartsWith(dev, StringComparison.OrdinalIgnoreCase))
            ?? audios.FirstOrDefault(a => a.Contains("Capture", StringComparison.OrdinalIgnoreCase) && a.EndsWith("Audio"))
            ?? (dev.Contains("USB Video") ? audios.FirstOrDefault(a => a.Contains("USB Digital Audio")) : null)
            ?? NoAudio;
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

    public void Start(string source, string audio, string folder, int minutes) => Start(new CaptureSpec(source, audio), folder, minutes, minutes * 60);

    public void Start(string source, string audio, string folder, int minutes, int segmentSeconds) => Start(new CaptureSpec(source, audio), folder, minutes, segmentSeconds);

    public void Start(CaptureSpec spec, string folder, int minutes) => Start(spec, folder, minutes, minutes * 60);

    public void Start(CaptureSpec spec, string folder, int minutes, int segmentSeconds)
    {
        if (IsRecording)
            throw new InvalidOperationException("Already recording.");
        var ff = FindFfmpeg() ?? throw new FileNotFoundException("ffmpeg is not installed.");
        Directory.CreateDirectory(folder);
        int start = NextNumber(folder);

        var cmd = BuildCapture(spec, bigBuffers: true);
        _appAudio?.Dispose();
        _appAudio = cmd.AppAudio;
        _appAudio?.Start();
        var output = Path.Combine(folder, "Recording_%04d.mp4");
        var args = $"-hide_banner -y {cmd.Args} " +
                   "-c:v libx264 -preset veryfast -crf 21 " +
                   (cmd.HasAudio ? "-c:a aac -b:a 192k " : "") +
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
        _last = (spec, folder, minutes, segmentSeconds);
        _wanted = true;
        _lastSize = -1;
        _lastGrowth = DateTime.Now;
        Status?.Invoke($"Recording ({cmd.Description}) - {minutes}-minute segments starting at Recording_{start:0000}.mp4");
    }

    private ProcessAudioCapture? _appAudio;

    // ------------------------------------------------------------------ health check (used by HealthMonitor)
    private (CaptureSpec Spec, string Folder, int Minutes, int Seconds)? _last;
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
                Start(l.Spec, l.Folder, l.Minutes, l.Seconds);
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
        _appAudio?.Dispose();
        _appAudio = null;
        Status?.Invoke("Recording stopped.");
    }
}
