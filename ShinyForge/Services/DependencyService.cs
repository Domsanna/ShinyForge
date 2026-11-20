using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ShinyForge.Core;

namespace ShinyForge.Services;

public enum DepState { Ok, Missing, UpdateAvailable }

public sealed class Dependency
{
    public required string Name { get; init; }
    public required string Purpose { get; init; }
    public string? WingetId { get; init; }
    public string? ManualUrl { get; init; }
    public bool ForSwitch { get; init; } = true;
    public bool For3DS { get; init; } = true;
    /// <summary>Looks on the computer (settings, PATH, usual install places). The backup folder is checked afterwards.</summary>
    public required Func<string?> Locate { get; init; }
    /// <summary>Program file names to look for in the Backup Tools folder (empty = can't be used from there).</summary>
    public string[] ExeNames { get; init; } = Array.Empty<string>();
    /// <summary>Where the tool was found last time it was checked (null = missing).</summary>
    public string? Path { get; set; }
    public bool FromBackup => AppPaths.IsInBackupTools(Path);
    public DepState State { get; set; }
    public string Detail { get; set; } = "";
    /// <summary>The installed version ("9.0.2", "v0.66.4", ...), read by <see cref="DependencyService.ReadVersions"/>; null = unknown.</summary>
    public string? Version { get; set; }
    public bool CanAutoInstall => WingetId != null || Name is "PKHeX" or "SysBot.exe" or "pokeldn" or "esptool";
    /// <summary>Only needed once the user set up that feature (the ESP32-S3 trades): not reported as missing before.</summary>
    public bool OnlyAfterSetup { get; init; }
    /// <summary>Nice to have, never needed by ShinyForge itself: not reported as missing (e.g. Pokémon Automation).</summary>
    public bool Optional { get; init; }
    /// <summary>Should a missing copy be reported at startup / installed by the main window's "Install all"?</summary>
    public bool Wanted => !Optional && (!OnlyAfterSetup || AppSettings.Current.PokeldnSetupDone);
    /// <summary>Can only be installed by hand (paid software or no public download).</summary>
    public bool ManualOnly => !CanAutoInstall;
}

/// <summary>Checks the outside programs ShinyForge relies on, installs/updates them (winget or download), and checks for updates.</summary>
public static class DependencyService
{
    private static readonly HttpClient Http = WebClients.Make(TimeSpan.FromSeconds(20), githubJson: false);

    /// <summary>The outside programs for this OS.</summary>
    public static readonly List<Dependency> All = OsInfo.IsWindows ? WindowsTools() : UnixTools();

    /// <summary>macOS / Linux: the tools that exist there (PKHeX, Excel, SysBot and 3DS Capture are Windows programs).</summary>
    private static List<Dependency> UnixTools() => new()
    {
        new() { Name = "FFmpeg", Purpose = "Records gameplay in timed segments", ExeNames = new[] { "ffmpeg" },
                ManualUrl = OsInfo.IsMac ? "https://formulae.brew.sh/formula/ffmpeg" : "https://ffmpeg.org/download.html#build-linux", Locate = RecordingService.FindFfmpeg },
        new() { Name = "Python 3", Purpose = "Runs the ShinyForge Python hunting bot (Switch FireRed/LeafGreen)", For3DS = false, ExeNames = new[] { "python3" },
                ManualUrl = "https://www.python.org/downloads/", Locate = FindPython },
        new() { Name = "pokeldn", Purpose = "Wireless trades and Mystery Gift with Switch games through an ESP32-S3 board (Decryptu/pokeldn)", For3DS = false,
                ExeNames = new[] { "pokeldn" }, OnlyAfterSetup = true, ManualUrl = "https://github.com/Decryptu/pokeldn/releases/latest",
                Locate = PokeldnService.FindApp },
        new() { Name = "esptool", Purpose = "Espressif's tool that installs the pokeldn radio firmware on the ESP32-S3", For3DS = false,
                ExeNames = new[] { "esptool" }, OnlyAfterSetup = true, ManualUrl = "https://github.com/espressif/esptool/releases/latest",
                Locate = () => PokeldnService.FindEsptool() is { } e && !AppPaths.IsInBackupTools(e) ? e : null },
    };

    private static List<Dependency> WindowsTools() => new()
    {
        new() { Name = "FFmpeg", Purpose = "Records gameplay in timed segments", WingetId = "Gyan.FFmpeg", ExeNames = new[] { "ffmpeg.exe" },
                ManualUrl = "https://www.gyan.dev/ffmpeg/builds/", Locate = RecordingService.FindFfmpeg },
        new() { Name = "Streamlabs Desktop", Purpose = "Game capture / streaming software for Switch games", WingetId = "Streamlabs.Streamlabs", ExeNames = new[] { "Streamlabs OBS.exe", "Streamlabs Desktop.exe" },
                ManualUrl = "https://streamlabs.com/streamlabs-live-streaming-software", For3DS = false,
                Locate = () => File.Exists(AppSettings.Current.StreamlabsPath) ? AppSettings.Current.StreamlabsPath : null },
        new() { Name = "3DS Capture", Purpose = "Shows the USB-connected 3DS on the PC", ForSwitch = false, ExeNames = new[] { "3ds_capture.exe" },
                ManualUrl = "https://3dscapture.com/", Locate = () => File.Exists(AppSettings.Current.Capture3DSPath) ? AppSettings.Current.Capture3DSPath : null },
        new() { Name = "PKHeX", Purpose = "Opens exported saves for viewing/editing (ShinyForge reads saves on its own)", ExeNames = new[] { "PKHeX.exe" },
                ManualUrl = "https://projectpokemon.org/home/files/file/1-pkhex/", Locate = FindPKHeX },
        new() { Name = "Microsoft Excel", Purpose = "Updates your Pokédex tracker spreadsheets (.xlsm) safely",
                ManualUrl = "https://www.microsoft.com/microsoft-365/excel",
                Locate = () => Type.GetTypeFromProgID("Excel.Application") != null ? "Excel" : null },
        new() { Name = "Python 3", Purpose = "Runs the ShinyForge Python hunting bot (Switch FireRed/LeafGreen)", WingetId = "Python.Python.3.13", For3DS = false, ExeNames = new[] { "python.exe" },
                ManualUrl = "https://www.python.org/downloads/", Locate = FindPython },
        new() { Name = "Pokémon Automation", Purpose = "Pokémon Automation's Computer Control: tested programs for every Switch Pokémon game, on an unmodded Switch (microcontroller + capture card) or through sys-botbase. Personal, non-profit use", For3DS = false,
                ExeNames = new[] { "SerialPrograms-Windows.cmd" }, Optional = true, ManualUrl = PokemonAutomation.Releases, Locate = PokemonAutomation.Find },
        new() { Name = "SysBot.exe", Purpose = "Sys-EncounterBot with the Switch bots (FRLG, Let's Go, SwSh, BDSP)", For3DS = false, ExeNames = new[] { "SysBot.exe" },
                ManualUrl = "https://github.com/Manu098vm/Sys-EncounterBot.NET/releases/latest",
                Locate = () => File.Exists(AppSettings.Current.SysBotPath) ? AppSettings.Current.SysBotPath : null },
        new() { Name = "pokeldn", Purpose = "Wireless trades and Mystery Gift with Switch games through an ESP32-S3 board (Decryptu/pokeldn)", For3DS = false,
                ExeNames = new[] { "pokeldn.exe" }, OnlyAfterSetup = true, ManualUrl = "https://github.com/Decryptu/pokeldn/releases/latest",
                Locate = PokeldnService.FindApp },
        new() { Name = "esptool", Purpose = "Espressif's tool that installs the pokeldn radio firmware on the ESP32-S3", For3DS = false,
                ExeNames = new[] { "esptool.exe" }, OnlyAfterSetup = true, ManualUrl = "https://github.com/espressif/esptool/releases/latest",
                Locate = () => Directory.Exists(PokeldnService.EsptoolDir) ? Directory.EnumerateFiles(PokeldnService.EsptoolDir, "esptool.exe", SearchOption.AllDirectories).FirstOrDefault() : null },
    };

    public static string? FindPKHeX()
    {
        if (File.Exists(AppSettings.Current.PKHeXPath))
            return AppSettings.Current.PKHeXPath;
        var local = Path.Combine(AppPaths.ToolsDir, "PKHeX", "PKHeX.exe");
        if (File.Exists(local))
            return local;
        // Common places people unzip PKHeX: the Desktop and Downloads (one or two folders deep).
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var hit = Directory.EnumerateFiles(root, "PKHeX.exe")
                    .Concat(Directory.EnumerateDirectories(root).SelectMany(d =>
                    {
                        try { return Directory.EnumerateFiles(d, "PKHeX.exe").Concat(Directory.EnumerateDirectories(d).SelectMany(dd => Directory.EnumerateFiles(dd, "PKHeX.exe"))); }
                        catch { return Enumerable.Empty<string>(); }
                    }))
                    .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (hit != null)
                    return hit;
            }
            catch { /* inaccessible folder */ }
        }
        return null;
    }

    public static string? FindPython()
    {
        if (!OsInfo.IsWindows) return OsInfo.Which("python3") ?? OsInfo.Which("python");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "python.exe");
                if (File.Exists(p) && !p.Contains("WindowsApps")) return p;
            }
            catch { }
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, "python.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
    }

    /// <summary>Checks the computer first, then the Backup Tools folder.</summary>
    public static void Refresh(Dependency d)
    {
        string? path = null;
        try { path = d.Locate(); } catch (Exception e) { AppPaths.Log($"Locate {d.Name}: {e.Message}"); }
        path ??= AppPaths.FindInBackupTools(d.ExeNames);
        d.Path = path;
        d.State = path is null ? DepState.Missing : Updates.Any(u => u.Item == d.Name) ? DepState.UpdateAvailable : DepState.Ok;
        d.Detail = path is null ? "Not found" : d.FromBackup ? $"{path}   (from the Backup Tools folder)" : path;
    }

    /// <summary>Path of a tool by name (computer first, then the Backup Tools folder), or null.</summary>
    public static string? PathOf(string name)
    {
        var d = All.FirstOrDefault(x => x.Name == name);
        if (d == null) return null; // not a tool on this OS
        Refresh(d);
        return d.Path;
    }

    public static async Task<string> RunWinget(string args, Action<string>? progress = null, bool install = true)
    {
        progress?.Invoke($"winget {args}");
        var extra = install ? " --source winget --accept-package-agreements --accept-source-agreements --silent" : " --source winget --accept-source-agreements";
        var psi = new ProcessStartInfo("winget", args + extra)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }

    /// <summary>Installs automatically. Returns a message for the user.</summary>
    public static async Task<string> Install(Dependency d, Action<string> progress)
    {
        if (!OsInfo.IsWindows && d.Name is "FFmpeg" or "Python 3")
            return await InstallUnixPackage(d, progress);
        if (d.WingetId != null)
        {
            var output = await RunWinget($"install --id {d.WingetId} --exact", progress);
            Refresh(d);
            return d.State == DepState.Ok ? $"{d.Name} installed." : $"{d.Name}: winget finished - {LastLine(output)}";
        }
        if (d.Name == "PKHeX")
            return await InstallPKHeX(progress);
        if (d.Name == "SysBot.exe")
            return await InstallSysBot(progress);
        if (d.Name == "pokeldn")
            return await PokeldnService.InstallAppAndFirmware(progress);
        if (d.Name == "esptool")
            return await PokeldnService.InstallEsptool(progress);
        OpenUrl(d.ManualUrl);
        return $"{d.Name} can't be installed automatically - its download page was opened.";
    }

    /// <summary>
    /// PKHeX is only distributed through Project Pokémon's download page (GitHub has no binaries). The page is opened;
    /// as soon as the downloaded PKHeX zip appears in the Downloads folder it is extracted to ShinyForge's Tools folder.
    /// </summary>
    private static async Task<string> InstallPKHeX(Action<string> progress)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var before = DateTime.Now;
        OpenUrl("https://projectpokemon.org/home/files/file/1-pkhex/");
        progress("Click 'Download' on the PKHeX page - ShinyForge will pick up the zip automatically (waiting up to 5 minutes)...");
        for (int i = 0; i < 300; i++)
        {
            await Task.Delay(1000);
            var zip = Directory.EnumerateFiles(downloads, "PKHeX*.zip").Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTime >= before.AddSeconds(-5)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (zip == null)
                continue;
            await Task.Delay(1500); // let the browser finish writing
            var dest = Path.Combine(AppPaths.ToolsDir, "PKHeX");
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            ZipFile.ExtractToDirectory(zip.FullName, dest);
            var exe = Directory.EnumerateFiles(dest, "PKHeX.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe != null)
            {
                AppSettings.Current.PKHeXPath = exe;
                AppSettings.Current.Save();
                return $"PKHeX installed to {Path.GetDirectoryName(exe)}.";
            }
        }
        return "No PKHeX download was detected. You can also point ShinyForge at PKHeX.exe in Settings.";
    }

    /// <summary>
    /// Downloads the latest Sys-EncounterBot.NET release (Manu098vm on GitHub, prebuilt zip) into ShinyForge's Tools folder.
    /// Note: the official release doesn't contain custom routines added to a self-built copy (e.g. FRLG_EncounterBot).
    /// </summary>
    private static async Task<string> InstallSysBot(Action<string> progress)
    {
        progress("Looking up the latest Sys-EncounterBot.NET release...");
        var rel = await Http.GetFromJsonAsync<JsonObject>($"https://api.github.com/repos/{SysBotRepo}/releases/latest")
                  ?? throw new IOException("GitHub didn't return the Sys-EncounterBot.NET release.");
        var tag = rel["tag_name"]?.ToString() ?? "latest";
        var asset = (rel["assets"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(a => a["name"]?.ToString().EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
        var url = asset?["browser_download_url"]?.ToString();
        if (url == null)
        {
            OpenUrl(SysBotReleases);
            return "The Sys-EncounterBot.NET release has no zip to download - its download page was opened.";
        }

        progress($"Downloading Sys-EncounterBot.NET {tag}...");
        var zipPath = Path.Combine(Path.GetTempPath(), $"Sys-EncounterBot-{tag}.zip");
        using (var http = WebClients.Make(TimeSpan.FromMinutes(5), githubJson: false))
        {
            await File.WriteAllBytesAsync(zipPath, await http.GetByteArrayAsync(url));
        }

        progress("Unpacking...");
        var dest = Path.Combine(AppPaths.ToolsDir, "Sys-EncounterBot");
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        ZipFile.ExtractToDirectory(zipPath, dest);
        File.WriteAllText(Path.Combine(dest, VersionMarker), tag); // what the update check compares with
        ForgetSysBotCheck();
        try { File.Delete(zipPath); } catch { }

        var exe = Directory.EnumerateFiles(dest, "SysBot.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? Directory.EnumerateFiles(dest, "*.exe", SearchOption.AllDirectories).FirstOrDefault(f => Path.GetFileName(f).Contains("SysBot", StringComparison.OrdinalIgnoreCase));
        if (exe == null)
            return $"Sys-EncounterBot.NET {tag} was unpacked to {dest}, but no SysBot.exe was found - locate it in Settings.";
        AppSettings.Current.SysBotPath = exe;
        AppSettings.Current.Save();
        return $"Sys-EncounterBot.NET {tag} installed to {Path.GetDirectoryName(exe)}.";
    }

    /// <summary>
    /// Installs every missing program that can be installed automatically (PKHeX last, because it waits for the browser download),
    /// then opens the download pages of the ones that can't. Returns a summary for the user.
    /// <paramref name="everything"/> = every tool shown as "Not installed" in Tools &amp; Updates, also the optional ones
    /// (Pokémon Automation) and the ones for a feature not set up yet (pokeldn, esptool); otherwise only <see cref="Dependency.Wanted"/>.
    /// </summary>
    public static List<Dependency> MissingTools(bool? isSwitch, bool everything)
    {
        var relevant = All.Where(d => isSwitch is null || (isSwitch.Value ? d.ForSwitch : d.For3DS)).ToList();
        foreach (var d in relevant) Refresh(d);
        return relevant.Where(d => d.State == DepState.Missing && (everything || d.Wanted)).ToList();
    }

    public static async Task<string> InstallAllMissing(bool? isSwitch, bool everything, Action<string> progress)
    {
        var missing = MissingTools(isSwitch, everything);
        if (missing.Count == 0)
            return "Everything is already installed.";

        var lines = new List<string>();
        var auto = missing.Where(d => d.CanAutoInstall).OrderBy(d => d.Name == "PKHeX").ToList();
        for (int i = 0; i < auto.Count; i++)
        {
            var d = auto[i];
            progress($"({i + 1}/{auto.Count}) Installing {d.Name}...");
            try
            {
                var step = i + 1;
                var msg = await Install(d, s => progress($"({step}/{auto.Count}) {s}"));
                Refresh(d);
                lines.Add((d.State == DepState.Missing ? "✗ " : "✓ ") + msg);
            }
            catch (Exception e) { lines.Add($"✗ {d.Name}: {e.GetBaseException().Message}"); }
        }
        foreach (var d in missing.Where(d => d.ManualOnly))
        {
            OpenUrl(d.ManualUrl);
            lines.Add($"→ {d.Name} has to be installed by hand - its download page was opened.");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static void OpenUrl(string? url)
    {
        if (!string.IsNullOrEmpty(url))
            OsInfo.Open(url);
    }

    /// <summary>
    /// macOS: installs FFmpeg / Python with Homebrew if it's there. Linux: system packages need the user's password, so
    /// the exact command is returned for them to run (ShinyForge never asks for or types passwords).
    /// </summary>
    private static async Task<string> InstallUnixPackage(Dependency d, Action<string> progress)
    {
        var pkg = d.Name == "FFmpeg" ? "ffmpeg" : "python3";
        if (OsInfo.IsMac)
        {
            var brew = OsInfo.Which("brew");
            if (brew == null)
            {
                OpenUrl("https://brew.sh");
                return $"{d.Name}: install Homebrew first (its page was opened), then run:  brew install {(pkg == "python3" ? "python" : pkg)}";
            }
            progress($"brew install {pkg}...");
            var psi = new ProcessStartInfo(brew) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("install");
            psi.ArgumentList.Add(pkg == "python3" ? "python" : pkg);
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            Refresh(d);
            return d.State == DepState.Ok ? $"{d.Name} installed with Homebrew." : $"{d.Name}: brew finished - {LastLine(output)}";
        }
        var cmd = File.Exists("/usr/bin/apt") ? $"sudo apt install {pkg}" : File.Exists("/usr/bin/dnf") ? $"sudo dnf install {pkg}"
                : File.Exists("/usr/bin/pacman") ? $"sudo pacman -S {(pkg == "python3" ? "python" : pkg)}" : $"install the '{pkg}' package with your package manager";
        return $"{d.Name}: run this in a terminal (it asks for your password), then click Check for updates:\n\n    {cmd}" +
               (pkg == "python3" ? "\n\nThe hunter also needs:  python3 -m pip install -r requirements.txt  (in the PythonBot folder)" : "");
    }

    private static string LastLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";

    // ------------------------------------------------------------------ update checks (at start, then every hour while open)
    public sealed record UpdateInfo(string Item, string Message, Func<Action<string>, Task<string>>? Apply);

    /// <summary>
    /// How often a running ShinyForge looks again. Checking only at start missed releases made while it stayed open
    /// (pokeldn published three versions in one night and the open window still said everything was current).
    /// </summary>
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromHours(1);
    /// <summary>When the last <see cref="CheckUpdates"/> finished (MinValue = never).</summary>
    public static DateTime LastChecked { get; private set; } = DateTime.MinValue;
    /// <summary>The last answer is older than <paramref name="age"/> (or there is none): time to ask GitHub / winget again.</summary>
    public static bool CheckIsOlderThan(TimeSpan age) => DateTime.Now - LastChecked > age;
    /// <summary>Tools the last check couldn't ask (offline, GitHub busy...): "No updates found" isn't said for them.</summary>
    public static List<string> CheckFailures { get; private set; } = new();
    private static List<string> _failing = new();
    private static void CheckFailed(string what, Exception e) => Note(what, null, "couldn't reach its download site: " + e.GetBaseException().Message);
    /// <summary>The outcome in words for Tools &amp; Updates: updates found, nothing found, or which tools couldn't be checked.</summary>
    public static string CheckSummary(int found) =>
        (found == 0 ? "No updates found" : $"{found} update(s) available") +
        (CheckFailures.Count == 0 ? "." : $" - couldn't check {string.Join(", ", CheckFailures)} (see their rows).");

    /// <summary>
    /// What the last check found for one tool: the newest version it saw (<paramref name="Latest"/>), why it couldn't compare
    /// (<paramref name="Problem"/>), or how the tool gets its updates instead (<paramref name="Info"/>, e.g. Excel via Office).
    /// Every installed tool gets one - a tool can't silently show "Installed" without having been compared.
    /// </summary>
    public sealed record CheckResult(string? Latest, string? Problem, string? Info = null);
    /// <summary>The last check's result per tool name.</summary>
    public static Dictionary<string, CheckResult> Checked { get; private set; } = new();
    private static Dictionary<string, CheckResult> _checking = new();
    public static CheckResult? CheckedFor(Dependency d) => Checked.GetValueOrDefault(d.Name);

    /// <summary>A found tool's state word for its row: "Up to date" once compared, "Installed (couldn't check)" when it failed.</summary>
    public static string StateText(Dependency d) => d.State switch
    {
        DepState.UpdateAvailable => "Update available",
        DepState.Missing => "Not installed",
        _ => CheckedFor(d) switch
        {
            { Problem: not null } => "Installed (couldn't check)",
            { Latest: not null } => "Up to date",
            _ => d.FromBackup ? "Using Backup Tools folder" : "Installed",
        },
    };

    /// <summary>The second line of a found tool's row: what the check found, then where the tool is.</summary>
    public static string CheckLine(Dependency d) => CheckedFor(d) switch
    {
        { Problem: { } p } => "Couldn't check for updates: " + p,
        { Info: { } i } => i,
        { Latest: { } l } => $"Latest {l}  •  {ShortPath(d.Detail)}",
        _ => ShortPath(d.Detail),
    };

    /// <summary>A long path cut to its end ("…\bin\ffmpeg.exe") so it fits on the row's one line.</summary>
    private static string ShortPath(string path, int max = 70)
    {
        if (path.Length <= max) return path;
        var parts = path.Split(Path.DirectorySeparatorChar);
        var tail = parts[^1];
        for (int i = parts.Length - 2; i > 0 && tail.Length + parts[i].Length + 3 <= max; i--) tail = parts[i] + Path.DirectorySeparatorChar + tail;
        return "…" + Path.DirectorySeparatorChar + tail;
    }

    private static void Note(string name, string? latest, string? problem = null, string? info = null)
    {
        lock (_checking) _checking[name] = new(latest, problem, info);
        if (problem == null) return;
        AppPaths.Log($"{name} update check: {problem}");
        lock (_failing) _failing.Add(name);
    }

    /// <summary>
    /// The one comparison every tool goes through: records what was found and returns true when <paramref name="latest"/> is
    /// newer. A version that can't be read is reported on the tool's row instead of passing as "up to date".
    /// </summary>
    private static bool Compare(string name, string? installed, string? latest)
    {
        if (ParseVersion(latest) == null)
        {
            Note(name, latest, "couldn't read the newest version" + (string.IsNullOrWhiteSpace(latest) ? "" : $" ('{latest}')"));
            return false;
        }
        if (ParseVersion(installed) == null)
        {
            Note(name, latest, $"couldn't read the installed version (latest is {latest})");
            return false;
        }
        Note(name, latest);
        return IsNewer(latest, installed);
    }

    /// <summary>One check at a time: the hourly check, Tools &amp; Updates and the start-up check share the same list.</summary>
    private static readonly SemaphoreSlim CheckGate = new(1, 1);

    /// <summary>
    /// What the last <see cref="CheckUpdates"/> found. A tool listed here shows "Update available" in its own row in Tools &amp;
    /// Updates (with the Update button there), also after it's re-checked; <see cref="ApplyUpdate"/> removes it once done.
    /// </summary>
    public static List<UpdateInfo> Updates { get; private set; } = new();

    /// <summary>
    /// <see cref="Updates"/> changed: a check finished or an update was installed. The update banner listens, so it says
    /// what's still pending and goes away once nothing is (it used to stay up after the update was done). May fire on any thread.
    /// </summary>
    public static event Action? UpdatesChanged;

    /// <summary>The newer version found for this tool, or null.</summary>
    public static UpdateInfo? UpdateFor(Dependency d) => Updates.FirstOrDefault(u => u.Item == d.Name);

    /// <summary>
    /// Runs an update, then checks the tool again: the update leaves the list once the tool is really there (its version
    /// is re-read). Returns the message for the user.
    /// </summary>
    public static async Task<string> ApplyUpdate(UpdateInfo u, Action<string> progress)
    {
        if (u.Apply == null) return u.Message;
        var msg = await u.Apply(progress);
        Updates = Updates.Where(x => x != u).ToList();
        if (All.FirstOrDefault(d => d.Name == u.Item) is { } dep)
        {
            Refresh(dep);
            try { dep.Version = await ReadVersion(dep); } catch { }
        }
        if (u.Item == "SysBot.exe") ForgetSysBotCheck();
        UpdatesChanged?.Invoke();
        return msg;
    }

    /// <summary>The updates ShinyForge can install itself (the others only say what to do, e.g. a self-built SysBot).</summary>
    public static List<UpdateInfo> InstallableUpdates => Updates.Where(u => u.Apply != null).ToList();

    /// <summary>"Install all updates": runs every update one after the other. Returns a summary for the user.</summary>
    public static async Task<string> InstallAllUpdates(Action<string> progress)
    {
        var todo = InstallableUpdates;
        var lines = new List<string>();
        for (int i = 0; i < todo.Count; i++)
        {
            var u = todo[i];
            var step = $"({i + 1}/{todo.Count})";
            progress($"{step} Updating {u.Item}...");
            try { lines.Add("✓ " + await ApplyUpdate(u, s => progress($"{step} {s}"))); }
            catch (Exception e) { lines.Add($"✗ {u.Item}: {e.GetBaseException().Message}"); }
        }
        foreach (var u in Updates.Where(u => u.Apply == null))
            lines.Add($"• {u.Item} needs you: {u.Message}");
        return lines.Count == 0 ? "There are no updates to install." : string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    public static async Task<List<UpdateInfo>> CheckUpdates()
    {
        await CheckGate.WaitAsync();
        try
        {
            _failing = new();
            _checking = new();
            var list = await CheckUpdatesNow();
            Checked = _checking;
            CheckFailures = _failing.Distinct().ToList();
            LastChecked = DateTime.Now;
            UpdatesChanged?.Invoke();
            return list;
        }
        finally { CheckGate.Release(); }
    }

    private static async Task<List<UpdateInfo>> CheckUpdatesNow()
    {
        var list = new List<UpdateInfo>();
        Updates = new();
        foreach (var d in All) Refresh(d);

        // winget tools (Windows): the version the program REALLY has against the newest one winget offers. ("winget upgrade"
        // only knows programs winget installed - not Python from python.org - and goes by its own records, which miss
        // Streamlabs' self-updates.)
        if (OsInfo.IsWindows)
            foreach (var d in All.Where(d => d.WingetId != null && d.Path != null))
            {
                try
                {
                    d.Version ??= await ReadVersion(d);
                    var latest = await WingetLatest(d.WingetId!);
                    if (!Compare(d.Name, d.Version, latest)) continue;
                    var dep = d;
                    list.Add(new(d.Name, $"Installed {d.Version}, latest is {latest}.", async p =>
                    {
                        // "install" on an installed program upgrades it - also when winget didn't install it (python.org)
                        var o = await RunWinget($"install --id {dep.WingetId} --exact", p);
                        Refresh(dep);
                        return $"{dep.Name}: {LastLine(o)}";
                    }));
                }
                catch (Exception e) { CheckFailed(d.Name, e); }
            }

        // PKHeX app vs. its latest release tag (Windows).
        if (OsInfo.IsWindows && All.FirstOrDefault(x => x.Name == "PKHeX") is { Path: not null } pk)
            try
            {
                pk.Version ??= await ReadVersion(pk);
                var latest = await LatestTag("kwsch/PKHeX");
                if (Compare("PKHeX", pk.Version, latest))
                    list.Add(new("PKHeX", $"Installed {pk.Version}, latest is {latest}.", p => InstallPKHeX(p)));
            }
            catch (Exception e) { CheckFailed("PKHeX", e); }

        // pokeldn: whenever it's installed. (It used to wait for the wireless-trades setup, so a pokeldn installed with
        // "Install all missing" showed "Installed" in Tools & Updates but was never checked.)
        if (PokeldnService.FindApp() is { } pokeldn)
        {
            try
            {
                var tag = await LatestTag(PokeldnService.Repo);
                var have = PokeldnService.InstalledAppTag(pokeldn);
                Note("pokeldn", tag, AppInfo.ParseTag(tag) == null ? $"couldn't read the newest version ('{tag}')" : null);
                if (AppInfo.ParseTag(tag) is { } latest && (have == null || AppInfo.ParseTag(have) < latest))
                    list.Add(new("pokeldn", $"Installed {have ?? "unknown version"}, latest is {tag} (the board's firmware is updated from Wireless trades).",
                        p => PokeldnService.InstallAppAndFirmware(p)));
            }
            catch (Exception e) { CheckFailed("pokeldn", e); }
        }

        // PKHeX library bundled inside ShinyForge (new games / save formats).
        try
        {
            var idx = await Http.GetFromJsonAsync<JsonObject>("https://api.nuget.org/v3-flatcontainer/pkhex.core/index.json");
            var newest = (idx?["versions"] as JsonArray)?.LastOrDefault()?.ToString();
            var bundled = typeof(PKHeX.Core.SaveFile).Assembly.GetName().Version;
            if (newest != null && bundled != null && IsNewer(newest, bundled.ToString()))
                list.Add(new("PKHeX library", $"ShinyForge uses PKHeX.Core {bundled.ToString(3)}; {newest} is available (needed only for brand-new games).", null));
        }
        catch (Exception e) { CheckFailed("PKHeX library", e); }

        // The other tools, all at once (each check is independent and logs its own failure).
        foreach (var found in await Task.WhenAll(CheckSysBot(), CheckEsptool(), CheckPokemonAutomation(), Check3DSCapture(), CheckSwitchSysBotBase(), CheckUnixPackages()))
            list.AddRange(found);

        // Tile artwork files: re-download anything missing.
        var missing = GameCatalog.All.Select(g => g.Mascot).Distinct().Where(m => !File.Exists(Path.Combine(AppPaths.ArtDir, $"{m}.png"))).ToList();
        if (missing.Count > 0)
            list.Add(new("Game artwork", $"{missing.Count} artwork file(s) missing.", async p =>
            {
                Directory.CreateDirectory(AppPaths.ArtDir);
                foreach (var m in missing)
                {
                    var bytes = await Http.GetByteArrayAsync($"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/other/home/{m}.png");
                    await File.WriteAllBytesAsync(Path.Combine(AppPaths.ArtDir, $"{m}.png"), bytes);
                }
                return "Artwork restored.";
            }));
        // tools whose makers publish no version to compare with: say how they're updated instead
        if (All.FirstOrDefault(d => d.Name == "Microsoft Excel") is { Path: not null })
            Note("Microsoft Excel", null, info: "Updated by Microsoft Office itself (in Excel: File → Account → Update Options → Update Now).");
        // safety net: an installed tool no check above looked at is reported, never shown as fine
        foreach (var d in All.Where(d => d.Path != null))
            lock (_checking)
                if (!_checking.ContainsKey(d.Name)) Note(d.Name, null, "wasn't checked for updates");

        // every tool with a newer version shows it in its own row (not only the winget ones)
        Updates = list;
        foreach (var d in All.Where(d => d.Path != null && list.Any(u => u.Item == d.Name))) d.State = DepState.UpdateAvailable;
        return list;
    }

    // ------------------------------------------------------------------ the other tools
    /// <summary>The newest version winget offers for a package ("Version: 9.0.2" in "winget show"), or null.</summary>
    private static async Task<string?> WingetLatest(string id)
    {
        var o = await RunWinget($"show --id {id} --exact", install: false);
        var m = System.Text.RegularExpressions.Regex.Match(o, @"^\s*Vers\w*\s*:\s*(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static Task<string> LatestTag(string repo) => WebClients.LatestGitHubTag(repo);

    /// <summary>The last "1.2" / "1.2.3" in a text ("v0.71.6-beta" → 0.71.6, "3.32/3.33" → 3.33), or null.</summary>
    internal static Version? ParseVersion(string? text) =>
        System.Text.RegularExpressions.Regex.Matches(text ?? "", @"\d+\.\d+(?:\.\d+)?").LastOrDefault() is { } m && Version.TryParse(m.Value, out var v) ? v : null;

    /// <summary>Is <paramref name="latest"/> newer than <paramref name="installed"/>? False when either can't be read.</summary>
    internal static bool IsNewer(string? latest, string? installed) =>
        ParseVersion(latest) is { } l && ParseVersion(installed) is { } i && l > i;

    /// <summary>Runs a command-line tool and returns what it printed (empty after <paramref name="timeoutMs"/>).</summary>
    private static async Task<string> RunTool(string exe, string args, int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var read = p.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return ""; }
        return await read;
    }

    /// <summary>Marker file ShinyForge writes next to tools it downloads, holding the release tag.</summary>
    private const string VersionMarker = "shinyforge-installed-version.txt";

    /// <summary>
    /// SysBot.exe (Sys-EncounterBot.NET). A copy ShinyForge downloaded is updated with one click. A copy built from source
    /// (…\bin\…\SysBot.exe - e.g. with the user's own FRLG routine) is only reported: the download wouldn't have those routines.
    /// </summary>
    private static async Task<List<UpdateInfo>> CheckSysBot()
    {
        var list = new List<UpdateInfo>();
        if (await SysBotOutdated(fresh: true) is not { } o) return list;
        list.Add(o.SelfBuilt
            ? new("SysBot.exe", $"Installed {o.Installed}, latest is {o.Latest}. Your copy is built from source (with your own routines, e.g. FRLG): " +
                                "update the source and rebuild - the download wouldn't have them.", null)
            : new("SysBot.exe", $"Installed {o.Installed}, latest is {o.Latest}.", InstallSysBot));
        return list;
    }

    public const string SysBotRepo = "Manu098vm/Sys-EncounterBot.NET";
    public const string SysBotReleases = "https://github.com/" + SysBotRepo + "/releases/latest";

    /// <summary>An outdated SysBot.exe: its version, the newest release, and whether it was built from source (…\bin\…).</summary>
    public sealed record SysBotOutdatedInfo(string Installed, string Latest, bool SelfBuilt);

    private static (DateTime At, SysBotOutdatedInfo? Result)? _sysBotCheck;

    /// <summary>
    /// Is SysBot.exe older than its latest release? Null = up to date, not installed, or unknown (offline - nothing is
    /// blocked then). The answer is kept for an hour so starting several things doesn't ask GitHub every time
    /// (<paramref name="fresh"/> = ask now).
    /// </summary>
    public static async Task<SysBotOutdatedInfo?> SysBotOutdated(bool fresh = false)
    {
        if (!fresh && _sysBotCheck is { } c && DateTime.Now - c.At < TimeSpan.FromHours(1)) return c.Result;
        if (!OsInfo.IsWindows || PathOf("SysBot.exe") is not { } exe) return null;
        try
        {
            var installed = SysBotVersion(exe);
            var latest = await LatestTag(SysBotRepo);
            var dir = Path.GetDirectoryName(exe)!;
            var result = Compare("SysBot.exe", installed, latest)
                ? new SysBotOutdatedInfo(installed ?? "unknown", latest!, dir.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                : null;
            _sysBotCheck = (DateTime.Now, result);
            return result;
        }
        catch (Exception e) { CheckFailed("SysBot.exe", e); return null; }
    }

    /// <summary>After SysBot.exe was updated: check again next time.</summary>
    public static void ForgetSysBotCheck() => _sysBotCheck = null;

    /// <summary>
    /// SysBot.exe's version: the release tag ShinyForge wrote when it downloaded it, else the version in its folder names
    /// ("Sys-EncounterBot.NET-2.7.1\...") - the program itself always says 1.0.0.0.
    /// </summary>
    public static string? SysBotVersion(string exe)
    {
        var marker = Path.Combine(AppPaths.ToolsDir, "Sys-EncounterBot", VersionMarker);
        return exe.StartsWith(Path.Combine(AppPaths.ToolsDir, "Sys-EncounterBot"), StringComparison.OrdinalIgnoreCase) && File.Exists(marker)
            ? File.ReadAllText(marker).Trim()
            : ParseVersion(Path.GetDirectoryName(exe))?.ToString();
    }

    // ------------------------------------------------------------------ the Python hunter's packages
    /// <summary>requirements.txt's packages by import name (the hunter fails to start without them).</summary>
    private static readonly (string Import, string Package)[] HunterPackages = { ("cv2", "opencv-python"), ("numpy", "numpy"), ("yaml", "pyyaml"), ("mss", "mss") };

    /// <summary>The Python hunter's packages this Python doesn't have (empty = all there; null = Python couldn't be asked).</summary>
    public static async Task<List<string>?> MissingPythonPackages(string python)
    {
        var imports = string.Join(",", HunterPackages.Select(p => $"'{p.Import}'"));
        var o = await RunTool(python, $"-c \"import importlib.util as u; print('missing:' + ','.join(m for m in [{imports}] if u.find_spec(m) is None))\"");
        var line = o.Split('\n').FirstOrDefault(l => l.StartsWith("missing:"));
        if (line == null) return null;
        var missing = line["missing:".Length..].Trim().Split(',', StringSplitOptions.RemoveEmptyEntries);
        return HunterPackages.Where(p => missing.Contains(p.Import)).Select(p => p.Package).ToList();
    }

    /// <summary>pip-installs the hunter's requirements.txt. Returns pip's last line.</summary>
    public static async Task<string> InstallPythonPackages(string python, string botDir) =>
        LastLine(await RunTool(python, $"-m pip install -r \"{Path.Combine(botDir, "requirements.txt")}\"", 600000));

    // ------------------------------------------------------------------ installed versions (Tools & Updates)
    /// <summary>Reads the version of every tool that was found - all at once, off the UI thread (some ask the program).</summary>
    public static Task ReadVersions() => Task.WhenAll(All.Where(d => d.Path != null).Select(async d =>
    {
        try { d.Version = await ReadVersion(d); }
        catch (Exception e) { d.Version = null; AppPaths.Log($"Version of {d.Name}: {e.Message}"); }
    }));

    private static async Task<string?> ReadVersion(Dependency d)
    {
        var path = d.Path!;
        string? First(string text) => ParseVersion(text.Split('\n').FirstOrDefault())?.ToString();
        string? FileVersion() => File.Exists(path) && FileVersionInfo.GetVersionInfo(path) is var v
            ? ParseVersion(v.ProductVersion) is { } pv && pv > new Version(0, 0) ? pv.ToString() : ParseVersion(v.FileVersion)?.ToString()
            : null;
        return d.Name switch
        {
            "FFmpeg" => First(await RunTool(path, "-version")),                   // "ffmpeg version 9.0.2-full_build-..."
            "Python 3" => First(await RunTool(path, "--version")),                // "Python 3.13.1"
            "esptool" => First(await RunTool(path, "version")),                   // "esptool v5.6.0"
            "pokeldn" => PokeldnService.InstalledAppTag(path),
            "SysBot.exe" => SysBotVersion(path),
            "Pokémon Automation" => PokemonAutomation.VersionOf(path)?.ToString(),
            "Microsoft Excel" => ExcelVersion(),
            // 3DS Capture's program has no version: its folder does ("3ds_capture.6.1")
            "3DS Capture" => FileVersion() is { } fv && fv != "1.0.0.0" ? fv : ParseVersion(Path.GetFileName(Path.GetDirectoryName(path)))?.ToString(),
            _ => FileVersion(),                                                    // Streamlabs, PKHeX: the program's own version
        };
    }

    /// <summary>Office's version (Click-to-Run writes it to the registry; Excel itself isn't started).</summary>
    private static string? ExcelVersion()
    {
        if (!OsInfo.IsWindows) return null;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
        return key?.GetValue("VersionToReport") as string;
    }

    /// <summary>esptool (whenever it is installed): asks the program itself for its version.</summary>
    private static async Task<List<UpdateInfo>> CheckEsptool()
    {
        var list = new List<UpdateInfo>();
        if (PokeldnService.FindEsptool() is not { } exe) return list; // whenever it's installed, setup done or not
        try
        {
            var installed = ParseVersion((await RunTool(exe, "version")).Split('\n').FirstOrDefault())?.ToString();
            var latest = await LatestTag(PokeldnService.EsptoolRepo);
            if (Compare("esptool", installed, latest))
                list.Add(new("esptool", $"Installed {installed}, latest is {latest}.", PokeldnService.InstallEsptool));
        }
        catch (Exception e) { CheckFailed("esptool", e); }
        return list;
    }

    private const string Capture3DSSite = "https://3dscapture.com/";

    /// <summary>The newest 3DS Capture on its website ("3ds_capture.6.1.zip" → 6.1 and the zip's address), or null.</summary>
    internal static async Task<(string Version, string Url)?> Latest3DSCapture()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Capture3DSSite);
        req.Headers.Accept.ParseAdd("text/html");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var m = System.Text.RegularExpressions.Regex.Match(await resp.Content.ReadAsStringAsync(),
            @"href=""([^""]*3ds_capture\.(\d+(?:\.\d+)+)\.zip)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[2].Value, new Uri(new Uri(Capture3DSSite), m.Groups[1].Value).ToString()) : null;
    }

    /// <summary>3DS Capture (Windows): its folder's version ("3ds_capture.6.1") against the download on 3dscapture.com.</summary>
    private static async Task<List<UpdateInfo>> Check3DSCapture()
    {
        var list = new List<UpdateInfo>();
        if (!OsInfo.IsWindows || All.FirstOrDefault(d => d.Name == "3DS Capture") is not { Path: { } exe } d) return list;
        try
        {
            d.Version ??= await ReadVersion(d);
            var latest = await Latest3DSCapture();
            if (Compare("3DS Capture", d.Version, latest?.Version))
                list.Add(new("3DS Capture", $"Installed {d.Version}, latest is {latest!.Value.Version}.",
                    p => Install3DSCapture(exe, latest.Value.Version, latest.Value.Url, p)));
        }
        catch (Exception e) { CheckFailed("3DS Capture", e); }
        return list;
    }

    /// <summary>
    /// Unzips the new 3DS Capture next to the old one ("…\3ds_capture.6.2\3ds_capture.exe") and uses it from then on.
    /// The old copy stays (its driver and settings are shared), so going back is just picking it again.
    /// </summary>
    private static async Task<string> Install3DSCapture(string oldExe, string version, string url, Action<string> progress)
    {
        progress($"Downloading 3DS Capture {version}...");
        var bytes = await Http.GetByteArrayAsync(url);
        var oldDir = Path.GetDirectoryName(oldExe)!;
        var parent = Path.GetFileName(oldDir).StartsWith("3ds_capture", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(oldDir)! : oldDir;
        var dest = Path.Combine(parent, $"3ds_capture.{version}");
        Directory.CreateDirectory(dest);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
            zip.ExtractToDirectory(dest, overwriteFiles: true);
        var exe = Directory.EnumerateFiles(dest, "3ds_capture.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new IOException($"The 3DS Capture {version} download has no 3ds_capture.exe - get it from {Capture3DSSite}");
        AppSettings.Current.Capture3DSPath = exe;
        AppSettings.Current.Save();
        return $"3DS Capture {version} installed to {dest} (the old copy is kept). If 3DS Capture is open, close it - ShinyForge starts the new one.";
    }

    /// <summary>
    /// Pokémon Automation's Computer Control: its version is in the unzipped folder's name. Updating = their download page
    /// (the user unzips it; ShinyForge doesn't download or bundle their program).
    /// </summary>
    private static async Task<List<UpdateInfo>> CheckPokemonAutomation()
    {
        var list = new List<UpdateInfo>();
        if (PokemonAutomation.Find() is not { } cmd) return list;
        try
        {
            var installed = PokemonAutomation.VersionOf(cmd)?.ToString();
            var latest = await LatestTag("PokemonAutomation/ComputerControl");
            if (Compare("Pokémon Automation", installed, latest))
                list.Add(new("Pokémon Automation", $"Installed {installed}, latest is {latest}.", _ =>
                {
                    OpenUrl(PokemonAutomation.Releases);
                    return Task.FromResult("Pokémon Automation's download page was opened - unzip the new version next to the old one (not into OneDrive); ShinyForge uses the newest.");
                }));
        }
        catch (Exception e) { CheckFailed("Pokémon Automation", e); }
        return list;
    }

    /// <summary>
    /// sys-botbase on the Switch (when it answers and no hunt is using it): original 2.x vs olliz0r's latest release, or
    /// sys-botbase-cpp vs Pokémon Automation's. Reported only - it's installed onto the Switch's SD card.
    /// </summary>
    private static async Task<List<UpdateInfo>> CheckSwitchSysBotBase()
    {
        var list = new List<UpdateInfo>();
        var s = AppSettings.Current;
        if (string.IsNullOrWhiteSpace(s.SwitchIP) || s.SwitchUsb || HuntService.IsPythonHuntRunning()) return list;
        try
        {
            var installed = await Task.Run(() => HealthMonitor.CanReach(s.SwitchIP, s.SwitchPort, 1500) ? SysBotBaseInstaller.InstalledVersion(s.SwitchIP, s.SwitchPort) : null);
            if (installed == null || installed == "old") return list;
            var cpp = SysBotBaseInstaller.IsCpp(installed);
            var latest = await LatestTag(cpp ? SysBotBaseInstaller.Repo : "olliz0r/sys-botbase");
            if (!IsNewer(latest, installed)) return list;
            list.Add(cpp
                ? new("sys-botbase (Switch)", $"The Switch runs sys-botbase-cpp {installed}, latest is {latest}. Update it from a Switch game page: Shiny Hunt card → sys-botbase.", null)
                : new("sys-botbase (Switch)", $"The Switch runs sys-botbase {installed}, latest is {latest}.", _ =>
                {
                    OpenUrl("https://github.com/olliz0r/sys-botbase/releases/latest");
                    return Task.FromResult("sys-botbase's download page was opened: copy its atmosphere folder onto the SD card, then restart the Switch.");
                }));
        }
        catch (Exception e) { CheckFailed("sys-botbase", e); }
        return list;
    }

    /// <summary>
    /// macOS / Linux: FFmpeg and Python come from the system's package manager (winget only exists on Windows). Homebrew
    /// upgrades them with one click; on Linux the command is shown (it needs the user's password).
    /// </summary>
    private static async Task<List<UpdateInfo>> CheckUnixPackages()
    {
        var list = new List<UpdateInfo>();
        if (OsInfo.IsWindows) return list;
        try
        {
            if (OsInfo.IsMac && OsInfo.Which("brew") is { } brew)
            {
                var outdated = (await RunTool(brew, "outdated --quiet", 60000)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var (name, formula) in new[] { ("FFmpeg", "ffmpeg"), ("Python 3", "python") })
                    if (outdated.FirstOrDefault(f => f == formula || f.StartsWith(formula + "@")) is not { } hit)
                        Note(name, null, info: "Homebrew has nothing newer.");
                    else
                        list.Add(new(name, $"Homebrew has a newer {hit}.", async p =>
                        {
                            p($"brew upgrade {hit}...");
                            var o = await RunTool(brew, $"upgrade {hit}", 600000);
                            return $"{name}: {LastLine(o)}";
                        }));
            }
            else if (File.Exists("/usr/bin/apt"))
            {
                var upgradable = (await RunTool("/usr/bin/apt", "list --upgradable", 30000)).Split('\n');
                foreach (var (name, pkg) in new[] { ("FFmpeg", "ffmpeg"), ("Python 3", "python3") })
                    if (!upgradable.Any(l => l.StartsWith(pkg + "/")))
                        Note(name, null, info: "The package manager (apt) has nothing newer.");
                    else
                        list.Add(new(name, $"A newer version is available. Run in a terminal:  sudo apt install --only-upgrade {pkg}", null));
            }
        }
        catch (Exception e) { CheckFailed("system packages", e); }
        return list;
    }

}
