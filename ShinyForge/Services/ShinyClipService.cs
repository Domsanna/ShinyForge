using System.Diagnostics;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Rolling "replay buffer" while a hunt runs: FFmpeg records the capture source into a ring of 10-second pieces
/// (only the last ~N seconds are kept). When a shiny appears, the pieces are joined into one video
/// "Shiny_&lt;species&gt;_&lt;time&gt;.mp4" (in the game's recording folder, or Data\Shiny Clips).
/// Not used while the normal recording is running (that already has the moment) - one capture device can't be opened twice.
/// </summary>
public sealed class ShinyClipService
{
    private Process? _ff;
    private string _dir = "";
    private string _outFolder = "";
    private bool _wanted;
    public bool Running => _ff is { HasExited: false };
    public string? LastError { get; private set; }

    public static string DefaultFolder => Path.Combine(AppPaths.DataDir, "Shiny Clips");

    private ProcessAudioCapture? _appAudio;

    public void Start(string source, string audio, string gameId, string outFolder) => Start(new RecordingService.CaptureSpec(source, audio), gameId, outFolder);

    public void Start(RecordingService.CaptureSpec spec, string gameId, string outFolder)
    {
        var source = spec.Source;
        Stop();
        var ff = RecordingService.FindFfmpeg();
        if (ff == null || string.IsNullOrWhiteSpace(source)) { LastError = "no FFmpeg or capture source"; return; }
        _dir = Path.Combine(AppPaths.DataDir, "ClipBuffer", gameId);
        _outFolder = string.IsNullOrWhiteSpace(outFolder) ? DefaultFolder : outFolder;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        Directory.CreateDirectory(_dir);
        var seconds = Math.Clamp(AppSettings.Current.ShinyClipSeconds, 20, 300);
        var wrap = seconds / 10 + 2;
        var cmd = RecordingService.BuildCapture(spec, bigBuffers: false);
        _appAudio = cmd.AppAudio;
        _appAudio?.Start();
        var args = $"-hide_banner -loglevel error -y {cmd.Args} -c:v libx264 -preset veryfast -crf 23 " +
                   "-g 60 " + (cmd.HasAudio ? "-c:a aac -b:a 128k " : "") +
                   $"-f segment -segment_time 10 -segment_wrap {wrap} -reset_timestamps 1 \"{Path.Combine(_dir, "buf%03d.ts")}\"";
        try
        {
            _ff = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true })!;
            _ff.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { LastError = e.Data; AppPaths.Log("clip buffer ffmpeg: " + e.Data); } };
            _ff.BeginErrorReadLine();
            _wanted = true;
            LastError = null;
            AppPaths.Log($"Shiny clip buffer started ({seconds}s) from {source}");
        }
        catch (Exception e) { LastError = e.Message; }
    }

    public void Stop()
    {
        _wanted = false;
        if (_ff is { HasExited: false })
        {
            try { _ff.StandardInput.WriteLine("q"); if (!_ff.WaitForExit(5000)) _ff.Kill(); } catch { try { _ff.Kill(); } catch { } }
        }
        _ff = null;
        _appAudio?.Dispose();
        _appAudio = null;
    }

    /// <summary>For the system check: the buffer should be running while wanted.</summary>
    public string? Problem() => _wanted && !Running ? "The shiny-clip buffer stopped (FFmpeg exited): " + (LastError ?? "unknown error") : null;

    /// <summary>Keeps recording a few more seconds, then joins the buffer into one video. Returns its path (or null).</summary>
    public async Task<string?> SaveClipAsync(string tag)
    {
        if (!Running) return null;
        await Task.Delay(8000); // a little after the moment too
        var pieces = new DirectoryInfo(_dir).EnumerateFiles("buf*.ts").Where(f => f.Length > 0).OrderBy(f => f.LastWriteTimeUtc).ToList();
        if (pieces.Count == 0) return null;
        var copyDir = Path.Combine(_dir, "save_" + DateTime.Now.Ticks);
        Directory.CreateDirectory(copyDir);
        var list = new List<string>();
        foreach (var p in pieces)
        {
            var c = Path.Combine(copyDir, p.Name);
            try { using var src = new FileStream(p.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var dst = File.Create(c); await src.CopyToAsync(dst); list.Add(c); }
            catch { }
        }
        var listFile = Path.Combine(copyDir, "list.txt");
        await File.WriteAllLinesAsync(listFile, list.Select(f => $"file '{f.Replace("'", "'\\''")}'"));
        Directory.CreateDirectory(_outFolder);
        var safe = string.Concat(tag.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var outFile = Path.Combine(_outFolder, $"Shiny_{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        var ff = RecordingService.FindFfmpeg()!;
        var psi = new ProcessStartInfo(ff, $"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{listFile}\" -c copy -bsf:a aac_adtstoasc \"{outFile}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var p2 = Process.Start(psi)!;
        var err = await p2.StandardError.ReadToEndAsync();
        await p2.WaitForExitAsync();
        try { Directory.Delete(copyDir, true); } catch { }
        if (p2.ExitCode != 0 || !File.Exists(outFile))
        {
            HealthMonitor.Report("Shiny clips", HealthState.Warning, "Couldn't save the shiny clip: " + err.Trim());
            return null;
        }
        AppPaths.Log("Shiny clip saved: " + outFile);
        return outFile;
    }
}
