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
    public bool CanAutoInstall => WingetId != null || Name is "PKHeX" or "SysBot.exe" or "pokeldn" or "esptool";
    /// <summary>Only needed once the user set up that feature (the ESP32-S3 trades): not reported as missing before.</summary>
    public bool OnlyAfterSetup { get; init; }
    /// <summary>Nice to have, never needed by ShinyForge itself: not reported as missing (e.g. Pokémon Automation).</summary>
    public bool Optional { get; init; }
    /// <summary>Should a missing copy be reported / installed by "Install all"?</summary>
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
        d.State = path is null ? DepState.Missing : DepState.Ok;
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
        var rel = await Http.GetFromJsonAsync<JsonObject>("https://api.github.com/repos/Manu098vm/Sys-EncounterBot.NET/releases/latest")
                  ?? throw new IOException("GitHub didn't return the Sys-EncounterBot.NET release.");
        var tag = rel["tag_name"]?.ToString() ?? "latest";
        var asset = (rel["assets"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(a => a["name"]?.ToString().EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
        var url = asset?["browser_download_url"]?.ToString();
        if (url == null)
        {
            OpenUrl("https://github.com/Manu098vm/Sys-EncounterBot.NET/releases/latest");
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
    /// </summary>
    public static async Task<string> InstallAllMissing(bool? isSwitch, Action<string> progress)
    {
        var relevant = All.Where(d => isSwitch is null || (isSwitch.Value ? d.ForSwitch : d.For3DS)).ToList();
        foreach (var d in relevant) Refresh(d);
        var missing = relevant.Where(d => d.State == DepState.Missing && d.Wanted).ToList();
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

    // ------------------------------------------------------------------ update checks (run every start)
    public sealed record UpdateInfo(string Item, string Message, Func<Action<string>, Task<string>>? Apply);

    public static async Task<List<UpdateInfo>> CheckUpdates()
    {
        var list = new List<UpdateInfo>();
        foreach (var d in All) Refresh(d);

        // winget-managed tools (Windows).
        if (OsInfo.IsWindows)
        try
        {
            // Plain listing ("winget upgrade" with no install flags): a table of Name / Id / Version / Available.
            var outp = await RunWinget("upgrade", install: false);
            var hasTable = outp.Contains("Available", StringComparison.OrdinalIgnoreCase);
            foreach (var d in All.Where(d => d.WingetId != null && d.State == DepState.Ok))
            {
                var line = !hasTable ? null : outp.Split('\n').FirstOrDefault(l =>
                    System.Text.RegularExpressions.Regex.IsMatch(l, $@"\s{System.Text.RegularExpressions.Regex.Escape(d.WingetId!)}\s+\S+\s+\S+"));
                if (line != null)
                {
                    d.State = DepState.UpdateAvailable;
                    var dep = d;
                    list.Add(new(d.Name, "A newer version is available.", async p =>
                    {
                        var o = await RunWinget($"upgrade --id {dep.WingetId} --exact", p);
                        Refresh(dep);
                        return $"{dep.Name}: {LastLine(o)}";
                    }));
                }
            }
        }
        catch (Exception e) { AppPaths.Log("winget upgrade check failed: " + e.Message); }

        // PKHeX app vs. latest release tag (Windows).
        if (OsInfo.IsWindows)
        try
        {
            var latest = (await Http.GetFromJsonAsync<JsonObject>("https://api.github.com/repos/kwsch/PKHeX/releases/latest"))?["tag_name"]?.ToString();
            var pkhex = PathOf("PKHeX");
            if (latest != null && pkhex != null)
            {
                var installed = FileVersionInfo.GetVersionInfo(pkhex).FileVersion ?? "";
                if (!VersionsMatch(installed, latest))
                    list.Add(new("PKHeX", $"Installed {installed}, latest is {latest}.", p => InstallPKHeX(p)));
            }
        }
        catch (Exception e) { AppPaths.Log("PKHeX update check failed: " + e.Message); }

        // pokeldn (only once the wireless-trades feature was set up).
        if (AppSettings.Current.PokeldnSetupDone && PokeldnService.FindApp() is { } pokeldn)
        {
            try
            {
                var rel = await PokeldnService.LatestAsync();
                var have = PokeldnService.InstalledAppTag(pokeldn);
                if (AppInfo.ParseTag(rel.Tag) is { } latest && (have == null || AppInfo.ParseTag(have) < latest))
                    list.Add(new("pokeldn", $"Installed {have ?? "unknown version"}, latest is {rel.Tag} (the board's firmware is updated from Wireless trades).",
                        p => PokeldnService.InstallAppAndFirmware(p)));
            }
            catch (Exception e) { AppPaths.Log("pokeldn update check failed: " + e.Message); }
        }

        // PKHeX library bundled inside ShinyForge (new games / save formats).
        try
        {
            var idx = await Http.GetFromJsonAsync<JsonObject>("https://api.nuget.org/v3-flatcontainer/pkhex.core/index.json");
            var newest = (idx?["versions"] as JsonArray)?.LastOrDefault()?.ToString();
            var bundled = typeof(PKHeX.Core.SaveFile).Assembly.GetName().Version;
            if (newest != null && bundled != null && !VersionsMatch(bundled.ToString(), newest))
                list.Add(new("PKHeX library", $"ShinyForge uses PKHeX.Core {bundled.ToString(3)}; {newest} is available (needed only for brand-new games).", null));
        }
        catch (Exception e) { AppPaths.Log("PKHeX.Core check failed: " + e.Message); }

        // The other tools, all at once (each check is independent and logs its own failure).
        foreach (var found in await Task.WhenAll(CheckSysBot(), CheckEsptool(), CheckPokemonAutomation(), CheckSwitchSysBotBase(), CheckUnixPackages()))
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
        return list;
    }

    // ------------------------------------------------------------------ the other tools
    private static async Task<string?> LatestTag(string repo) =>
        (await Http.GetFromJsonAsync<JsonObject>($"https://api.github.com/repos/{repo}/releases/latest"))?["tag_name"]?.ToString();

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
        if (!OsInfo.IsWindows || PathOf("SysBot.exe") is not { } exe) return list;
        try
        {
            var dir = Path.GetDirectoryName(exe)!;
            var marker = Path.Combine(AppPaths.ToolsDir, "Sys-EncounterBot", VersionMarker);
            var installed = exe.StartsWith(Path.Combine(AppPaths.ToolsDir, "Sys-EncounterBot"), StringComparison.OrdinalIgnoreCase) && File.Exists(marker)
                ? File.ReadAllText(marker).Trim()
                : ParseVersion(dir)?.ToString();
            var latest = await LatestTag("Manu098vm/Sys-EncounterBot.NET");
            if (!IsNewer(latest, installed)) return list;
            var selfBuilt = dir.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            list.Add(selfBuilt
                ? new("SysBot.exe", $"Installed {installed}, latest is {latest}. Your copy is built from source (with your own routines, e.g. FRLG): " +
                                    "update the source and rebuild - the download wouldn't have them.", null)
                : new("SysBot.exe", $"Installed {installed ?? "unknown version"}, latest is {latest}.", InstallSysBot));
        }
        catch (Exception e) { AppPaths.Log("SysBot.exe update check failed: " + e.Message); }
        return list;
    }

    /// <summary>esptool (only once the ESP32-S3 / pokeldn feature was set up): asks the program itself for its version.</summary>
    private static async Task<List<UpdateInfo>> CheckEsptool()
    {
        var list = new List<UpdateInfo>();
        if (!AppSettings.Current.PokeldnSetupDone || PokeldnService.FindEsptool() is not { } exe) return list;
        try
        {
            var installed = ParseVersion((await RunTool(exe, "version")).Split('\n').FirstOrDefault())?.ToString();
            var latest = await LatestTag(PokeldnService.EsptoolRepo);
            if (IsNewer(latest, installed))
                list.Add(new("esptool", $"Installed {installed}, latest is {latest}.", PokeldnService.InstallEsptool));
        }
        catch (Exception e) { AppPaths.Log("esptool update check failed: " + e.Message); }
        return list;
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
            if (IsNewer(latest, installed))
                list.Add(new("Pokémon Automation", $"Installed {installed}, latest is {latest}.", _ =>
                {
                    OpenUrl(PokemonAutomation.Releases);
                    return Task.FromResult("Pokémon Automation's download page was opened - unzip the new version next to the old one (not into OneDrive); ShinyForge uses the newest.");
                }));
        }
        catch (Exception e) { AppPaths.Log("Pokémon Automation update check failed: " + e.Message); }
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
        catch (Exception e) { AppPaths.Log("sys-botbase update check failed: " + e.Message); }
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
                    if (outdated.FirstOrDefault(f => f == formula || f.StartsWith(formula + "@")) is { } hit)
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
                var pkgs = new[] { "ffmpeg", "python3" }.Where(p => upgradable.Any(l => l.StartsWith(p + "/"))).ToList();
                if (pkgs.Count > 0)
                    list.Add(new(string.Join(" + ", pkgs), $"Newer versions are available. Run in a terminal:  sudo apt install --only-upgrade {string.Join(' ', pkgs)}", null));
            }
        }
        catch (Exception e) { AppPaths.Log("Package update check failed: " + e.Message); }
        return list;
    }

    private static bool VersionsMatch(string installed, string latest)
    {
        static int[] Parts(string v) => v.Split('.', '-', ' ').Select(x => int.TryParse(x, out var n) ? n % 100 : -1).Where(n => n >= 0).Take(3).ToArray();
        var a = Parts(installed);
        var b = Parts(latest);
        return a.Length >= 3 && b.Length >= 3 && !b.Zip(a).Select(t => t.First.CompareTo(t.Second)).SkipWhile(c => c == 0).Take(1).Any(c => c > 0);
    }
}
