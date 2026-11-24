// Cross-platform self-test: dotnet XTest.dll <workdir> <saves-dir> [live]
//   saves-dir: a folder with real save files to copy (only read).  live: also downloads pokeldn/esptool for this OS.
using System.Diagnostics;
using PKHeX.Core;
using ShinyForge.Core;
using ShinyForge.Services;

var work = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "xtest");
var savesSrc = args.Length > 1 ? args[1] : "";
var live = args.Contains("live");
Directory.CreateDirectory(work);
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) failures++; }

Console.WriteLine($"OS: {OsInfo.Name}  data: {AppPaths.DataDir}  documents: {OsInfo.DocumentsDir}  launch dir: {AppPaths.LaunchDir}");
Check(!OsInfo.IsWindows, "running on macOS or Linux");
Check(AppPaths.DataDir.StartsWith(OsInfo.DataRoot), $"data folder under {OsInfo.DataRoot}");

// ---- tools
var names = DependencyService.All.Select(d => d.Name).ToList();
Check(names.SequenceEqual(new[] { "FFmpeg", "Python 3", "pokeldn", "esptool" }), "tool list for this OS: " + string.Join(", ", names));
foreach (var d in DependencyService.All) DependencyService.Refresh(d);
Console.WriteLine("   " + string.Join("  |  ", DependencyService.All.Select(d => $"{d.Name}: {d.State} {d.Path}")));
Check(DependencyService.PathOf("PKHeX") == null && DependencyService.PathOf("SysBot.exe") == null, "Windows-only tools are not offered");
Check(HuntService.OptionsFor(GameCatalog.Get("sw_sword")).Count == 0 && HuntService.OptionsFor(GameCatalog.Get("sw_frlg_lg")).Select(o => o.Key).SequenceEqual(new[] { "python" }),
    "SysBot hunting hidden; the Python hunter offered for FireRed/LeafGreen");

// ---- recording (FFmpeg)
var (video, audio) = RecordingService.ListDevices();
Console.WriteLine($"   video: {string.Join(" | ", video)}\n   audio: {string.Join(" | ", audio)}");
var screen = video.FirstOrDefault(v => v.StartsWith(RecordingService.LinuxScreen) || v.Contains("screen", StringComparison.OrdinalIgnoreCase));
var (input, maps, _) = RecordingService.BuildInput(screen ?? "/dev/video0 (x)", RecordingService.NoAudio, true);
Check(OsInfo.IsLinux ? input.Contains(screen != null ? "x11grab" : "v4l2") : input.Contains("avfoundation"), "capture input for this OS: " + input + " " + maps);
if (screen != null && RecordingService.FindFfmpeg() != null)
{
    var dir = Path.Combine(work, "rec");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    var rec = new RecordingService();
    rec.Status += s => Console.WriteLine("   rec: " + s);
    rec.Start(screen, RecordingService.NoAudio, dir, 1, 3);
    Thread.Sleep(9000);
    rec.Stop();
    var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "Recording_*.mp4") : Array.Empty<string>();
    Check(files.Length >= 2 && files.All(f => new FileInfo(f).Length > 1000), $"screen recorded into {files.Length} numbered segments");
}

// ---- saves (PKHeX), Game Info, HOME export
if (Directory.Exists(savesSrc))
{
    var us = Directory.EnumerateFiles(savesSrc, "main", SearchOption.AllDirectories).FirstOrDefault(f => f.Contains("00001b50"));
    if (us != null)
    {
        var copyDir = Path.Combine(work, "save_copy");
        Directory.CreateDirectory(copyDir);
        File.Copy(us, Path.Combine(copyDir, "main"), true);
        var g = GameCatalog.Get("3ds_usun");
        var found = SaveService.FindSaves(copyDir, g);
        Check(found.Count == 1 && found[0].Save.Version == GameVersion.US, "Ultra Sun save read: " + found.FirstOrDefault()?.Save.OT);
        if (found.Count == 1)
        {
            var (folder, count) = SaveService.ExportPokemon(found[0].Save, g, copyDir);
            Check(count > 100 && Directory.Exists(folder), $"exported {count} Pokémon to {Path.GetFileName(folder)}");
            var info = SaveService.WriteGameInfo(found[0].Save, g, found[0].Path, copyDir);
            Check(File.ReadAllText(info).Contains("GAME INFO"), "Game Info written");
            var col = Collection.FromSave(found[0].Save, found[0].Path);
            Check(col.Total > 100, $"collection read: {col.Total} Pokémon");
        }

        // Checkpoint on a fake SD card (forward-slash paths)
        var sd = Path.Combine(work, "sd");
        if (Directory.Exists(sd)) Directory.Delete(sd, true);
        var b1 = Path.Combine(sd, "3ds", "Checkpoint", "saves", "0x01B50 Pokémon Ultra Sun", "20261110-090000");
        Directory.CreateDirectory(b1);
        File.Copy(us, Path.Combine(b1, "main"));
        AppSettings.Current.CheckpointFolder = sd; // in memory only
        var roots = CheckpointService.FindRoots(false);
        var backups = CheckpointService.FindBackups(g, roots.Where(r => r.StartsWith(sd)));
        Check(backups.Count == 1, $"Checkpoint backup found under {Path.GetRelativePath(sd, roots.FirstOrDefault(r => r.StartsWith(sd)) ?? sd)}");
        if (backups.Count == 1)
        {
            var copied = CheckpointService.CopyToPc(backups[0], g, Path.Combine(work, "saves_copy"));
            Check(File.Exists(copied.Path), "backup copied to the computer");
        }
    }
    else Console.WriteLine("   (no Ultra Sun save in " + savesSrc + ")");
}

// ---- DexTracker (Living Dex / Pokédex): its files are there and its server answers on this OS
Check(DexTrackerService.Installed && File.Exists(Path.Combine(DexTrackerService.AppDir, "data", "reference_data.json")), "DexTracker files present: " + DexTrackerService.Version);
try
{
    DexTrackerService.EnsureRunning();
    using var http = new System.Net.Http.HttpClient();
    var page = http.GetStringAsync(DexTrackerService.Url).GetAwaiter().GetResult();
    Check(page.Contains("shinyforge-dextracker.js"), "DexTracker served on " + DexTrackerService.Url);
}
catch (Exception e) { Check(false, "DexTracker server: " + e.Message); }

// ---- update check
try
{
    var rel = AppUpdateService.CheckAsync().GetAwaiter().GetResult();
    Check(rel != null && rel.Version >= new Version(1, 0, 6), $"latest ShinyForge on GitHub: {rel?.Tag}");
}
catch (Exception e) { Check(false, "update check: " + e.Message); }

// ---- pokeldn
Check(PokeldnService.AppAsset.Contains(OsInfo.IsMac ? "macos" : "linux"), "pokeldn download for this OS: " + PokeldnService.AppAsset);
Console.WriteLine("   boards: " + (PokeldnService.FindBoards() is { Count: > 0 } b ? string.Join(", ", b.Select(x => x.Kind + " " + x.Port)) : "none plugged in"));
if (live)
{
    var had = Directory.Exists(PokeldnService.AppDir);
    Console.WriteLine("   " + PokeldnService.InstallAppAndFirmware(_ => { }).GetAwaiter().GetResult());
    Console.WriteLine("   " + PokeldnService.InstallEsptool(_ => { }).GetAwaiter().GetResult());
    var app = PokeldnService.FindApp();
    Check(app != null && PokeldnService.InstalledAppTag(app) != null, $"pokeldn installed: {app} ({PokeldnService.InstalledAppTag(app)})");
    Check(PokeldnService.FirmwareVersionOf(PokeldnService.FindFirmware()) is { } v && Version.TryParse(v, out _), "firmware downloaded: " + PokeldnService.FirmwareVersionOf(PokeldnService.FindFirmware()));
    var esp = PokeldnService.FindEsptool();
    if (esp != null)
    {
        using var p = Process.Start(new ProcessStartInfo(esp, "version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30000);
        Check(o.Contains("esptool"), "esptool runs: " + o.Split('\n')[0].Trim());
    }
    else Check(false, "esptool installed");
    if (app != null && !OsInfo.IsMac)
    {
        using var p = Process.Start(new ProcessStartInfo(app) { UseShellExecute = false, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(app)! })!;
        Thread.Sleep(12000);
        p.Refresh();
        Check(!p.HasExited, "pokeldn starts on this OS" + (p.HasExited ? " - " + p.StandardError.ReadToEnd().Split('\n').LastOrDefault(l => l.Length > 0) : ""));
        try { p.Kill(true); } catch { }
    }
    if (!had) foreach (var d in new[] { PokeldnService.AppDir, PokeldnService.FirmwareDir, PokeldnService.EsptoolDir }) try { Directory.Delete(d, true); } catch { }
}

// ---- Checkpoint matching and reading a console's SD card over FTP
Check(CheckpointService.Matches(GameCatalog.Get("sw_za"), "0x0100F43008C44000 0x0100F43008C44000") &&
      CheckpointService.Matches(GameCatalog.Get("nds_black"), "IRBO POKEMON B"), "Checkpoint folder names matched");
if (savesSrc.Length > 0 && Directory.Exists(savesSrc))
{
    string? pick = null; Game? pg = null;
    foreach (var f in Directory.EnumerateFiles(savesSrc, "*", SearchOption.AllDirectories))
    {
        if (new FileInfo(f).Length > 16 * 1024 * 1024 || !SaveUtil.TryGetSaveFile(f, out var sv) || sv == null) continue;
        pg = GameCatalog.All.FirstOrDefault(x => x.Versions.Contains(sv.Version) && CheckpointService.TitleIds.ContainsKey(x.Id) && x.Platform is Platform.Switch or Platform.N3DS);
        if (pg != null) { pick = f; break; }
    }
    if (pick != null && pg != null)
    {
        var id = CheckpointService.TitleIds[pg.Id][0];
        var sdRoot = Path.Combine(work, "mock_sd");
        if (Directory.Exists(sdRoot)) Directory.Delete(sdRoot, true);
        var backup = Path.Combine(sdRoot, pg.IsSwitch ? "switch" : "3ds", "Checkpoint", "saves", pg.IsSwitch ? $"0x{id} 0x{id}" : $"0x{id} Pokémon {pg.Title}", "20261115-120000 Dom");
        Directory.CreateDirectory(backup);
        File.Copy(pick, Path.Combine(backup, Path.GetFileName(pick)), true);
        using var ftpd = new MockFtpd(sdRoot);
        var mirror = ConsoleSdService.Mirror(pg, "127.0.0.1", ftpd.Port, null, Path.Combine(work, "mock_mirror"));
        var found = CheckpointService.FindBackups(pg, new[] { mirror.Root });
        Check(found.Count == 1 && found[0].Save.Save.Version == SaveUtil.GetSaveFile(pick)!.Version,
            $"{pg.Title} Checkpoint backup read over FTP ({mirror.Downloaded} file downloaded)");
    }
    else Check(false, "a Switch / 3DS save to test the FTP reading with");
}

// ---- self-diagnosis, minor (non-gameplay) problems, GitHub error reports
{
    HealthItem H(string f, HealthState st, string m, string? det = null) => new(f, st, m, DateTime.Now, det);
    Check(H("Python hunter", HealthState.Warning, "x").AffectsGameplay && H("3DS hunt (X)", HealthState.Warning, "x").AffectsGameplay
          && H("Program", HealthState.Error, "x").AffectsGameplay && H("Keep awake", HealthState.Error, "x").AffectsGameplay,
        "hunting / crashes / sleep problems count as gameplay problems");
    Check(!H("Overlay / remote", HealthState.Error, "x").AffectsGameplay && !H("Shiny clips (X)", HealthState.Warning, "x").AffectsGameplay
          && !H("Recording (X)", HealthState.Error, "x").AffectsGameplay && !H("Program", HealthState.Warning, "x").AffectsGameplay
          && !H("Backups", HealthState.Warning, "x").AffectsGameplay && !H("Disk space", HealthState.Warning, "x").AffectsGameplay,
        "overlay / clips / recordings / backups / background hiccups are minor");

    HealthItem? announced = null;
    Action<HealthItem> grab = i => announced ??= i;
    HealthMonitor.Problem += grab;
    var issuesBefore = File.Exists(ErrorDoctor.IssuesLog) ? new FileInfo(ErrorDoctor.IssuesLog).Length : 0;
    HealthMonitor.Report("Overlay / remote", HealthState.Error, "doctor-test: port 8765 in use");
    Check(announced == null, "a minor problem is not announced (no banner / sound / phone alert)");
    Check(HealthMonitor.MinorItems.Any(i => i.Message.Contains("doctor-test")) && !HealthMonitor.Items.Any(i => i.Message.Contains("doctor-test")),
        "a minor problem is listed only under minor issues");
    Check(File.Exists(ErrorDoctor.IssuesLog) && new FileInfo(ErrorDoctor.IssuesLog).Length > issuesBefore
          && ErrorDoctor.QuietLogLines(5).Any(l => l.Contains("doctor-test")), "a minor problem is written to issues.log");
    HealthMonitor.Report("Python hunter", HealthState.Warning, "doctor-test: Bot: the game did not reset");
    Check(announced?.Message.Contains("doctor-test") == true, "a gameplay problem is announced");
    HealthMonitor.Problem -= grab;
    HealthMonitor.Clear("Overlay / remote");
    HealthMonitor.Clear("Python hunter");

    // every known problem gets a cause and steps; the ones ShinyForge can fix offer a fix
    var known = new[]
    {
        H("Disk space", HealthState.Error, "Only 0.5 GB free on C:\\"), H("Program files", HealthState.Error, "Pokédex data is missing"),
        H("Settings / data folder", HealthState.Error, "Can't write"), H("Keep awake", HealthState.Error, "The PC went to sleep"),
        H("Python hunter", HealthState.Error, "Can't reach the Switch at 1.2.3.4"), H("Python hunter", HealthState.Warning, "The Python hunter has stopped."),
        H("Python hunter", HealthState.Error, "The same Deoxys (PID 1) was logged 3 times"), H("Python hunter", HealthState.Warning, "No new encounter for 9 minutes"),
        H("3DS hunt (X)", HealthState.Error, "Lost the connection to the 3DS"), H("Recording (X)", HealthState.Error, "ffmpeg exited"),
        H("Collection tracker (X)", HealthState.Warning, "Last sync failed"), H("Shiny clips (X)", HealthState.Warning, "clip"),
        H("Overlay / remote", HealthState.Error, "port"), H("Backups", HealthState.Warning, "backup"), H("Program", HealthState.Error, "Unexpected error: boom"),
    };
    Check(known.All(k => ErrorDoctor.Diagnose(k) is { Cause.Length: > 10, Steps.Count: > 0 }), $"all {known.Length} known problems are diagnosed with steps");
    var withFix = new[] { 0, 1, 2, 4, 5, 6, 7, 8, 12, 14 };
    Check(withFix.All(n => ErrorDoctor.Diagnose(known[n]).Fix != null) && ErrorDoctor.Diagnose(known[3]).Fix == null,
        "fixable problems offer an automatic fix");
    var repaired = await ErrorDoctor.Diagnose(known[2]).Fix!();
    Check(repaired.Contains("can be written"), "data-folder repair fix runs: " + repaired);

    // the GitHub report: no personal details, fits in a link
    var home = OsInfo.Home;
    var secret = $"Crash in {Path.Combine(home, "Documents", "save.sav")} by {Environment.UserName} on {Environment.MachineName} " +
                 "from 192.168.0.16, mail someone@example.com, mac 01:23:45:67:89:ab";
    var clean = ErrorDoctor.Sanitize(secret);
    Check(!clean.Contains(home, StringComparison.OrdinalIgnoreCase) && !clean.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase)
          && !clean.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && !clean.Contains("192.168.0.16")
          && !clean.Contains("someone@example.com") && !clean.Contains("01:23:45") && clean.Contains("<ip>") && clean.Contains("~"),
        "report text is sanitized: " + clean);
    var crash = H("Program", HealthState.Error, "Unexpected error: " + secret, string.Concat(Enumerable.Repeat("   at ShinyForge.Something.Method() in " + home + "\\x.cs:line 1\n", 400)));
    var url = ErrorDoctor.ReportUrl(crash, ErrorDoctor.Diagnose(crash), "You already have the latest version.", "I pressed Start hunt");
    var decoded = Uri.UnescapeDataString(url);
    Check(url.StartsWith($"https://github.com/{AppInfo.Repo}/issues/new?title=") && url.Length <= 8000, $"report link fits GitHub's limit ({url.Length} chars)");
    Check(decoded.Contains(ErrorDoctor.ReportTitlePrefix) && decoded.Contains("ShinyForge's diagnosis") && decoded.Contains(AppInfo.Display)
          && decoded.Contains("I pressed Start hunt") && decoded.Contains("latest version"), "report has the title tag, diagnosis, version, user note and fix result");
    Check(!decoded.Contains(home, StringComparison.OrdinalIgnoreCase) && !decoded.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase)
          && !decoded.Contains("192.168.0.16"), "report link has no personal details");
    Check(ErrorDoctor.ReportPrompt(crash, ErrorDoctor.Diagnose(crash), null).Contains("Nothing is sent until you press"), "the user confirms before anything opens");
}

// ---- keep awake + sound (must never crash)
try { Sounds.Play(); Check(true, "alert sound call is safe"); } catch (Exception e) { Check(false, "sound: " + e.Message); }

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
