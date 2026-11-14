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

// ---- Pokédex data
var dex = DexData.Build(GameCatalog.Get("gba_firered"), true);
Check(dex.Count > 380, $"FireRed Pokédex built: {dex.Count} entries");

// ---- trackers from GitHub
try
{
    var entries = SheetsDownloadService.ManifestAsync().GetAwaiter().GetResult();
    var small = entries.OrderBy(e => e.Size).First();
    var root = Path.Combine(work, "sheets");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    var path = SheetsDownloadService.DownloadAsync(small, _ => { }, root).GetAwaiter().GetResult();
    Check(File.Exists(path) && path.Contains(Path.Combine("Gen 1", "(3) Yellow")), "tracker downloaded with this OS's folder separators: " + Path.GetRelativePath(root, path));
}
catch (Exception e) { Check(false, "tracker download: " + e.Message); }

// ---- update check
try
{
    var rel = AppUpdateService.CheckAsync().GetAwaiter().GetResult();
    Check(rel != null && rel.Build >= 6, $"latest ShinyForge on GitHub: {rel?.Tag}");
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

// ---- keep awake + sound (must never crash)
try { Sounds.Play(); Check(true, "alert sound call is safe"); } catch (Exception e) { Check(false, "sound: " + e.Message); }

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
