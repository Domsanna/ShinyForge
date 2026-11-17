using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Puts Pokémon Automation's sys-botbase-cpp (github.com/PokemonAutomation/sys-botbase-cpp, GPL-3.0) on the Switch's SD card
/// over WiFi, in place of olliz0r's sys-botbase 2.x: same sys-module slot (atmosphere/contents/430000000000000B), same
/// commands, but tick-precise, lighter on memory, with a fix for random crashes, a USB mode and a log file on the SD card.
///
/// Install: download the release zip from GitHub → check it only holds that sys-module → start the FTP app on the Switch if
/// needed → back up the files that are there now (Data\Backups\sys-botbase ...) → upload exefs.nsp (as .new, then swapped
/// in), config.cfg ("wifi" / "usb") and flags/boot2.flag. sys-botbase loads at boot, so the Switch is restarted by the user.
/// </summary>
public static class SysBotBaseInstaller
{
    public const string Repo = "PokemonAutomation/sys-botbase-cpp";
    private const string ModuleDir = "/atmosphere/contents/430000000000000B";
    private const string ZipPrefix = "atmosphere/contents/430000000000000B/";
    private static readonly HttpClient Http = WebClients.Make(TimeSpan.FromSeconds(60));

    public sealed record Release(string Tag, string ZipUrl, long Size);

    /// <summary>The latest sys-botbase-cpp release on GitHub (its atmosphere.zip).</summary>
    public static async Task<Release> Latest()
    {
        var json = await Http.GetFromJsonAsync<JsonObject>($"https://api.github.com/repos/{Repo}/releases/latest")
                   ?? throw new IOException("GitHub didn't answer.");
        var asset = json["assets"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>() == "atmosphere.zip")
                    ?? throw new IOException("The latest sys-botbase-cpp release has no atmosphere.zip.");
        return new(json["tag_name"]!.GetValue<string>(), asset["browser_download_url"]!.GetValue<string>(), asset["size"]!.GetValue<long>());
    }

    /// <summary><see cref="InstalledVersion"/>: sys-botbase accepts the connection but answers nothing (a crashed sys-botbase-cpp).</summary>
    public const string Frozen = "frozen";

    /// <summary>
    /// The sys-botbase version the Switch reports ("2.5", "3.33", ...), "old" when sys-botbase answers but is too old to say,
    /// <see cref="Frozen"/> when it connects but answers nothing at all, or null when nothing is there. (A frozen sys-botbase-cpp
    /// used to be reported as "old" = the original, which hid the Restore button.)
    /// </summary>
    public static string? InstalledVersion(string host, int port)
    {
        try
        {
            using (var bot = new SysBotSession(host, port, controller: false))
                if (bot.Version() is { } v) return v;
            using var again = new SysBotSession(host, port, controller: false); // the timed-out session can't be reused
            return again.Answers() ? "old" : Frozen;
        }
        catch { return null; }
    }

    /// <summary>sys-botbase-cpp reports 3.x; olliz0r's original is 2.x or older.</summary>
    public static bool IsCpp(string? version) =>
        version != null && Version.TryParse(version.Split('/')[^1], out var v) && v.Major >= 3;

    /// <summary>
    /// Installs the release on the Switch (<paramref name="mode"/> "wifi" or "usb"). No game may be open (the FTP app is
    /// started from the HOME menu). Returns the backup folder of the files that were replaced.
    /// </summary>
    public static async Task<string> Install(string host, int botPort, string mode, Action<string>? progress = null)
    {
        var release = await Latest();
        var dir = Path.Combine(AppPaths.ToolsDir, "sys-botbase-cpp", Safe(release.Tag));
        var zip = Path.Combine(dir, "atmosphere.zip");
        if (!File.Exists(zip) || new FileInfo(zip).Length != release.Size)
        {
            progress?.Invoke($"Downloading sys-botbase-cpp {release.Tag} from GitHub...");
            Directory.CreateDirectory(dir);
            using var download = WebClients.Make(TimeSpan.FromMinutes(5), githubJson: false);
            await using (var s = await download.GetStreamAsync(release.ZipUrl))
            await using (var f = File.Create(zip + ".part")) await s.CopyToAsync(f);
            File.Move(zip + ".part", zip, true);
        }
        var files = Extract(zip, Path.Combine(dir, "files"));
        return await Task.Run(() => Upload(host, botPort, mode, files, progress));
    }

    /// <summary>
    /// Unzips the release after checking it's what it should be: only files of the sys-botbase sys-module, with exefs.nsp.
    /// Returns (path on the SD card relative to the module folder, local file).
    /// </summary>
    internal static List<(string Remote, string Local)> Extract(string zip, string into)
    {
        using var z = ZipFile.OpenRead(zip);
        var entries = z.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')).ToList();
        if (entries.Any(e => !e.FullName.StartsWith(ZipPrefix, StringComparison.Ordinal) || e.FullName.Contains("..")))
            throw new InvalidDataException("The sys-botbase-cpp download holds files outside its own sys-module folder - not installed.");
        if (entries.All(e => e.FullName != ZipPrefix + "exefs.nsp"))
            throw new InvalidDataException("The sys-botbase-cpp download has no exefs.nsp - not installed.");
        if (Directory.Exists(into)) Directory.Delete(into, true);
        var list = new List<(string, string)>();
        foreach (var e in entries)
        {
            var rel = e.FullName[ZipPrefix.Length..];
            var local = Path.Combine(into, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            e.ExtractToFile(local, true);
            list.Add((rel, local));
        }
        return list;
    }

    private static string Upload(string host, int botPort, string mode, List<(string Remote, string Local)> files, Action<string>? progress)
    {
        using var ftp = OpenFtp(host, botPort, progress);
        progress?.Invoke("Backing up the sys-botbase that's on the SD card now...");
        var backup = Path.Combine(AppPaths.DataDir, "Backups", $"sys-botbase {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
        BackupDir(ftp, ModuleDir, backup);
        // sys-botbase-cpp is already there (a reinstall / update): that's not "the previous sys-botbase" - keep the real backup
        // of the original as the one Restore uses
        if (File.Exists(Path.Combine(backup, "exefs.nsp")) && IsCppBuild(Path.Combine(backup, "exefs.nsp")))
        {
            Directory.Delete(backup, true);
            backup = LatestBackup() ?? "(none - the SD card already had sys-botbase-cpp)";
        }

        progress?.Invoke("Copying sys-botbase-cpp onto the SD card...");
        ftp.MakeDirs(ModuleDir + "/flags");
        foreach (var (remote, local) in files.Where(f => f.Remote != "exefs.nsp" && f.Remote != "config.cfg"))
            ftp.Upload(local, $"{ModuleDir}/{remote}");
        // the program itself: uploaded next to the old one, then swapped in (a broken upload never replaces a working file)
        var nsp = files.First(f => f.Remote == "exefs.nsp").Local;
        ftp.Upload(nsp, ModuleDir + "/exefs.nsp.new");
        var swapped = true;
        try { ftp.Delete(ModuleDir + "/exefs.nsp"); }
        catch (FtpException) { swapped = ftp.Size(ModuleDir + "/exefs.nsp") == null; } // not there yet = fine; there but locked = no
        if (swapped) ftp.Rename(ModuleDir + "/exefs.nsp.new", ModuleDir + "/exefs.nsp");
        else
        {
            ftp.Upload(nsp, ModuleDir + "/exefs.nsp"); // the old one can't be deleted: write over it
            ftp.Delete(ModuleDir + "/exefs.nsp.new");
        }
        WriteMode(ftp, mode);
        AppPaths.Log($"sys-botbase-cpp installed ({mode}); backup of the old files: {backup}");
        return backup;
    }

    /// <summary>
    /// Switches sys-botbase-cpp between WiFi and USB (its config.cfg) and the ShinyForge setting with it. Takes effect after
    /// the Switch restarts.
    /// </summary>
    public static void SetMode(string host, int botPort, string mode, Action<string>? progress = null)
    {
        using (var ftp = OpenFtp(host, botPort, progress)) WriteMode(ftp, mode);
        AppSettings.Current.SwitchConnection = mode == "usb" ? "USB" : "WiFi";
        AppSettings.Current.Save();
    }

    /// <summary>
    /// The newest backup made by <see cref="Install"/> that holds a sys-botbase OTHER than sys-botbase-cpp (the one that was on
    /// the SD card before), or null. A backup taken while cpp was already installed would just put cpp back.
    /// </summary>
    public static string? LatestBackup()
    {
        var root = Path.Combine(AppPaths.DataDir, "Backups");
        return Directory.Exists(root)
            ? Directory.GetDirectories(root, "sys-botbase *").Where(d => File.Exists(Path.Combine(d, "exefs.nsp")))
                .OrderByDescending(d => d).FirstOrDefault(d => !IsCppBuild(Path.Combine(d, "exefs.nsp")))
            : null;
    }

    /// <summary>Is this exefs.nsp byte-for-byte one of the sys-botbase-cpp releases ShinyForge downloaded?</summary>
    internal static bool IsCppBuild(string nsp)
    {
        var root = Path.Combine(AppPaths.ToolsDir, "sys-botbase-cpp");
        if (!Directory.Exists(root)) return false;
        var bytes = File.ReadAllBytes(nsp);
        return Directory.EnumerateFiles(root, "exefs.nsp", SearchOption.AllDirectories)
            .Any(k => new FileInfo(k).Length == bytes.Length && File.ReadAllBytes(k).AsSpan().SequenceEqual(bytes));
    }

    /// <summary>
    /// Why sys-botbase-cpp can't be used over WiFi on consoles like the user's: its screenshot command runs out of memory
    /// (std::bad_alloc in pixelPeek_cmd, seen in its log.txt) and its command thread dies - it keeps accepting connections but
    /// answers nothing until the Switch restarts. ShinyForge needs screenshots to open games, find the FTP app and save.
    /// </summary>
    public const string CppWifiProblem =
        "sys-botbase-cpp runs out of memory when it's asked for a screenshot over WiFi and then stops answering anything until the " +
        "Switch restarts (its log on the SD card says \"std::bad_alloc in pixelPeek\"). ShinyForge needs screenshots to open games, " +
        "find the FTP app and save.";

    /// <summary>
    /// Can sys-botbase take a screenshot? ShinyForge needs that to open games, find the FTP app and save. sys-botbase-cpp over
    /// WiFi is never asked: the test itself would crash it (<see cref="CppWifiProblem"/>). <paramref name="version"/> = what
    /// <see cref="InstalledVersion"/> said.
    /// </summary>
    public static bool ScreenshotWorks(string host, int port, string? version)
    {
        if (version == null || version == Frozen || (IsCpp(version) && !AppSettings.Current.SwitchUsb)) return false;
        try
        {
            using var bot = new SysBotSession(host, port, controller: false);
            return bot.Screenshot() is { Length: > 1000 };
        }
        catch { return false; }
    }

    /// <summary>
    /// Puts the backed-up sys-botbase back onto the SD card. The FTP app must already run (a sys-botbase that can't take
    /// screenshots can't start it). Copies sys-botbase-cpp's log.txt into the backup folder first, for reporting. The Switch
    /// is restarted by the user afterwards.
    /// </summary>
    public static void Restore(string host, string backup, Action<string>? progress = null)
    {
        var port = ConsoleSdService.FindPort(host)
                   ?? throw new IOException("The FTP app isn't running on the Switch. Start it yourself (hold R while starting a game → ftpd), then try again.");
        using var ftp = ConsoleFtp.Connect(host, port);
        try { ftp.Download(ModuleDir + "/log.txt", Path.Combine(backup, "sys-botbase-cpp log.txt")); } catch (IOException) { } // no log
        progress?.Invoke("Putting the previous sys-botbase back onto the SD card...");
        foreach (var file in Directory.GetFiles(backup, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("log.txt")))
        {
            var rel = Path.GetRelativePath(backup, file).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.Contains('/')) ftp.MakeDirs($"{ModuleDir}/{rel[..rel.LastIndexOf('/')]}");
            ftp.Upload(file, $"{ModuleDir}/{rel}");
        }
        AppSettings.Current.SwitchConnection = "WiFi"; // the original sys-botbase only speaks WiFi
        AppSettings.Current.Save();
        AppPaths.Log($"sys-botbase restored from {backup}");
    }

    private static void WriteMode(ConsoleFtp ftp, string mode)
    {
        if (mode is not ("wifi" or "usb")) throw new ArgumentException("mode must be wifi or usb", nameof(mode));
        var tmp = Path.Combine(Path.GetTempPath(), "shinyforge-sbb-config.cfg");
        File.WriteAllText(tmp, mode + "\n");
        try { ftp.Upload(tmp, ModuleDir + "/config.cfg"); }
        finally { File.Delete(tmp); }
    }

    /// <summary>The FTP app on the Switch (started from the HOME menu when it isn't running yet).</summary>
    private static ConsoleFtp OpenFtp(string host, int botPort, Action<string>? progress)
    {
        var port = ConsoleSdService.FindPort(host) ?? ConsoleFtpLauncher.StartFtpApp(host, botPort, progress);
        return ConsoleFtp.Connect(host, port);
    }

    private static void BackupDir(ConsoleFtp ftp, string remote, string local)
    {
        List<FtpEntry> entries;
        try { entries = ftp.List(remote); }
        catch (FtpException) { return; } // nothing installed yet
        foreach (var e in entries)
            if (e.IsDir) BackupDir(ftp, $"{remote}/{e.Name}", Path.Combine(local, e.Name));
            else ftp.Download($"{remote}/{e.Name}", Path.Combine(local, e.Name));
    }

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
