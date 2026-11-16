using System.Diagnostics;
using System.Text.Json.Nodes;
using ShinyForge.Core;

namespace ShinyForge.Services;

public sealed record HuntOption(string Key, string Title, string Description);

/// <summary>Starts the right shiny-hunting bot for a game: the ShinyForge Python bot or SysBot.exe with a routine pre-selected.</summary>
public static class HuntService
{
    // PokeRoutineType values in Sys-EncounterBot (incl. the FRLG routine added there).
    private const int LGPE = 2, SWSH = 4, BDSP = 6, FRLG = 7;

    /// <summary>The bots for a game on this OS (SysBot.exe is a Windows program, so its options are Windows-only).</summary>
    public static List<HuntOption> OptionsFor(Game game) =>
        AllOptionsFor(game).Where(o => o.Key != "sysbot" || OsInfo.IsWindows).ToList();

    private static List<HuntOption> AllOptionsFor(Game game) => game.Bot switch
    {
        BotKind.FrlgSwitch => new()
        {
            new("python", "ShinyForge hunter (Python)",
                "Auto mode: detects where you are. Soft resets at legendary spots (Deoxys, birds, Mewtwo, Lugia/Ho-Oh, Snorlax), " +
                "walks for wild encounters elsewhere, never runs from legendaries. Logs IVs/EVs/nature for every encounter."),
            new("sysbot", "SysBot.exe - FRLG_EncounterBot",
                "The same hunting logic inside Sys-EncounterBot (Auto / Static / Wild / Fishing modes)."),
        },
        BotKind.SysBotLGPE => new() { new("sysbot", "SysBot.exe - LGPE_EncounterBot", "Let's Go encounter bot (stationary, gifts, trades, GO Park, live stats).") },
        BotKind.SysBotSWSH => new() { new("sysbot", "SysBot.exe - SWSH_EncounterBot", "Sword/Shield encounter bot (resets, lines, dogs, Keldeo, Max Lair, eggs, live stats).") },
        BotKind.SysBotBDSP => new() { new("sysbot", "SysBot.exe - BDSP_RNG", "Brilliant Diamond/Shining Pearl RNG tool.") },
        _ => new(),
    };

    public static string UnsupportedReason(Game game) => game.Platform switch
    {
        Platform.Switch when game.Bot is BotKind.SysBotLGPE or BotKind.SysBotSWSH or BotKind.SysBotBDSP && !OsInfo.IsWindows =>
            "This game's hunting bot (Sys-EncounterBot / SysBot.exe) runs only on Windows. Recording, collection tracking, " +
            "save export and wireless trades all work here.",
        Platform.Switch => "No automatic hunting bot exists for this game yet. Recording, collection tracking and save export all work.",
        Platform.NDS or Platform.GBA =>
            "A 3DS runs DS and GBA games in a special compatibility mode where Luma3DS's tools (input redirection, memory access) " +
            "aren't available, so these games can't be controlled over WiFi. Recording, collection tracking and save export all work.",
        _ => "The 3DS Virtual Console hunter supports the English releases of Red, Blue, Yellow, Gold, Silver and Crystal " +
             "(their Game Boy memory maps). Recording, collection tracking and save export all work for this game.",
    };

    public static Process? PythonHunt;
    private static (Game Game, string Key, string BotDir)? _lastPython;
    private static bool _stopRequested;
    private static long _warningsOffset = -1;
    private static int _autoRestarts;
    private static DateTime _startedAt;

    /// <summary>Raised (background thread) when the Switch can't be reached while a hunt runs - the UI asks for a new IP.</summary>
    public static event Action<string>? SwitchUnreachable;

    /// <summary>
    /// shinyforge.json in the bot folder: the hunt options, notification settings and (only if the user turned it on) the
    /// RNG mode. The bot merges it over config.yaml when it starts.
    /// </summary>
    public static void WriteBotOverrides(string botDir)
    {
        var s = AppSettings.Current;
        var h = s.Hunt;
        var o = new JsonObject
        {
            // "rng" only when the user chose it in Hunt options; otherwise null = keep config.yaml's mode (auto).
            ["mode"] = h.RngEnabled ? "rng" : null,
            ["stop_rules"] = new JsonObject
            {
                ["species"] = new JsonArray(h.StopSpecies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => (JsonNode)x).ToArray()),
                ["natures"] = new JsonArray(h.StopNatures.Select(x => (JsonNode)x).ToArray()),
                ["min_iv_total"] = h.MinIvTotal,
                ["min_ivs"] = new JsonObject(h.MinIvs.Where(kv => kv.Value > 0).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["require_all"] = h.RequireAll,
            },
            ["auto_catch"] = new JsonObject { ["mode"] = h.AutoCatch, ["ball_slot"] = h.BallSlot, ["max_balls"] = h.MaxBalls },
            ["rng"] = new JsonObject
            {
                ["natures"] = new JsonArray(h.RngNatures.Select(x => (JsonNode)x).ToArray()),
                ["min_ivs"] = new JsonObject(h.RngMinIvs.Where(kv => kv.Value > 0).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            },
            ["notify"] = new JsonObject
            {
                ["discord_webhook"] = s.DiscordWebhookUrl, ["mention_everyone"] = s.DiscordMentionEveryone,
                ["ntfy_topic"] = s.NtfyTopic, ["ntfy_server"] = s.NtfyServer,
                ["shiny"] = s.NotifyShiny, ["problems"] = false, // problems are sent by ShinyForge's system check
            },
        };
        File.WriteAllText(Path.Combine(botDir, "shinyforge.json"), o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    // ------------------------------------------------------------------ pause / remote / status
    public static string? BotDir => _lastPython?.BotDir ?? AppPaths.FindPythonBotFolder(AppSettings.Current.PythonBotFolder);

    public static bool IsPaused => BotDir is { } d && File.Exists(Path.Combine(d, "pause.flag"));

    public static void SetPaused(bool paused)
    {
        if (BotDir is not { } d) return;
        var flag = Path.Combine(d, "pause.flag");
        if (paused) File.WriteAllText(flag, DateTime.Now.ToString("s"));
        else if (File.Exists(flag)) File.Delete(flag);
    }

    /// <summary>One line about the Python hunter for the phone remote.</summary>
    public static string StatusLine()
    {
        if (BotDir is not { } d) return "No hunt set up.";
        var running = PythonProcesses().Any(p => CommandLineContains(p, "shinybot"));
        var state = "";
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(Path.Combine(d, "bot_status.json")));
            state = j?["state"]?.GetValue<string>() ?? "";
        }
        catch { }
        return !running ? "Python hunter: not running" + (state == "found" ? " (it found something - check the game!)" : "")
             : IsPaused ? "Python hunter: PAUSED" : $"Python hunter: {state switch { "found" => "FOUND SOMETHING - waiting for you", "catching" => "catching...", "rng-waiting" => "RNG: waiting for the shiny frame", _ => "hunting" }}";
    }

    /// <summary>Actions from the phone remote.</summary>
    public static string RemoteAction(string what)
    {
        try
        {
            switch (what)
            {
                case "pause": SetPaused(true); return "Paused - the bot stops after the current attempt.";
                case "resume": SetPaused(false); return "Resumed.";
                case "stop": StopPythonHunt(); return "Stopped.";
                case "start":
                    if (_lastPython is not { } h) return "Start the hunt once from ShinyForge first (then the remote can restart it).";
                    if (IsPythonHuntRunning()) return "Already running.";
                    ResetAutoFixes();
                    Start(h.Game, h.Key);
                    return "Started.";
                default: return "Unknown action.";
            }
        }
        catch (Exception e) { return "Failed: " + e.Message; }
    }

    /// <summary>Raised when the Python hunter finds something (shiny / stop rule): species, is shiny.</summary>
    public static event Action<string, bool>? Found;
    private static System.Threading.Timer? _statusTimer;
    private static string _lastStatusKey = "";

    private static void StartStatusWatch(string botDir)
    {
        _statusTimer?.Dispose();
        var path = Path.Combine(botDir, "bot_status.json");
        _lastStatusKey = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString() : "";
        _statusTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (!File.Exists(path)) return;
                var key = File.GetLastWriteTimeUtc(path).Ticks.ToString();
                if (key == _lastStatusKey) return;
                _lastStatusKey = key;
                var j = JsonNode.Parse(File.ReadAllText(path));
                if (j?["state"]?.GetValue<string>() == "found" && j["last"] is JsonObject last)
                    Found?.Invoke(last["species"]?.GetValue<string>() ?? "?", last["shiny"]?.GetValue<bool>() ?? false);
            }
            catch { }
        }, null, 2000, 2000);
    }

    /// <summary>Writes the Switch IP/port from ShinyForge's settings into the Python bot's config.yaml (controller section).</summary>
    public static void SyncBotConfig(string botDir, string ip, int port)
    {
        var cfg = Path.Combine(botDir, "config.yaml");
        if (!File.Exists(cfg)) return;
        var text = File.ReadAllText(cfg);
        var updated = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^(\s+host:\s*)[^\s#]+", m => m.Groups[1].Value + ip, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        updated = System.Text.RegularExpressions.Regex.Replace(updated, @"(?m)^(\s+port:\s*)\d+", m => m.Groups[1].Value + port);
        if (updated != text) File.WriteAllText(cfg, updated);
    }

    public static void Start(Game game, string key)
    {
        var s = AppSettings.Current;
        if (key == "python")
        {
            var botDir = AppPaths.FindPythonBotFolder(s.PythonBotFolder)
                ?? throw new FileNotFoundException($"The ShinyForge Python bot wasn't found in {s.PythonBotFolder} or the Backup Tools folder ({AppPaths.BackupToolsDir}). Set its folder in Settings.");
            SyncBotConfig(botDir, s.SwitchIP, s.SwitchPort);
            WriteBotOverrides(botDir);
            try { File.Delete(Path.Combine(botDir, "pause.flag")); } catch { }
            _lastPython = (game, key, botDir);
            StartStatusWatch(botDir);
            _stopRequested = false;
            _startedAt = DateTime.Now;
            var warnLog = Path.Combine(botDir, "bot_warnings.log");
            _warningsOffset = File.Exists(warnLog) ? new FileInfo(warnLog).Length : 0;
            HealthMonitor.Register("python-hunt", CheckPythonHunt);
            var bat = Path.Combine(botDir, "start_hunt.bat");
            if (IsPythonHuntRunning())
                throw new InvalidOperationException("The Python hunter is already running.");
            if (!OsInfo.IsWindows)
            {
                // macOS / Linux: python3 -m shinybot hunt, its console output kept in hunt_console.log in the bot folder.
                var py = DependencyService.PathOf("Python 3") ?? throw new FileNotFoundException("Python 3 wasn't found - install it first (Tools & Updates).");
                var unix = new ProcessStartInfo(py) { WorkingDirectory = botDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var a in new[] { "-u", "-m", "shinybot", "hunt" }) unix.ArgumentList.Add(a);
                var log = new StreamWriter(Path.Combine(botDir, "hunt_console.log"), append: true) { AutoFlush = true };
                log.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} hunt started by ShinyForge ====");
                PythonHunt = Process.Start(unix)!;
                PythonHunt.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
                PythonHunt.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
                PythonHunt.EnableRaisingEvents = true;
                PythonHunt.Exited += (_, _) => { lock (log) log.Dispose(); };
                PythonHunt.BeginOutputReadLine();
                PythonHunt.BeginErrorReadLine();
                return;
            }
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"") { WorkingDirectory = botDir, UseShellExecute = true };
            var python = DependencyService.PathOf("Python 3");
            if (python != null && AppPaths.IsInBackupTools(python))
            {
                // Python from the Backup Tools folder isn't on PATH: put it first so the bot's "python" finds it.
                var dir = Path.GetDirectoryName(python)!;
                psi.UseShellExecute = false; // needed to pass the environment (a console window still opens)
                psi.Environment["PATH"] = $"{dir};{Path.Combine(dir, "Scripts")};{Environment.GetEnvironmentVariable("PATH")}";
            }
            PythonHunt = Process.Start(psi);
            return;
        }

        if (!OsInfo.IsWindows)
            throw new PlatformNotSupportedException("SysBot.exe (Sys-EncounterBot) runs only on Windows.");
        var sysbot = DependencyService.PathOf("SysBot.exe")
            ?? throw new FileNotFoundException($"SysBot.exe wasn't found on this PC or in the Backup Tools folder ({AppPaths.BackupToolsDir}). Set its path in Settings.", s.SysBotPath);
        var routine = game.Bot switch { BotKind.SysBotLGPE => LGPE, BotKind.SysBotSWSH => SWSH, BotKind.SysBotBDSP => BDSP, _ => FRLG };
        PrepareSysBotConfig(Path.Combine(Path.GetDirectoryName(sysbot)!, "config.json"), s.SwitchIP, s.SwitchPort, routine);
        Process.Start(new ProcessStartInfo(sysbot) { WorkingDirectory = Path.GetDirectoryName(sysbot)!, UseShellExecute = true });
    }

    /// <summary>Points SysBot's first bot at the Switch and pre-selects the routine (keeps every other setting).</summary>
    private static void PrepareSysBotConfig(string path, string ip, int port, int routine)
    {
        JsonObject root;
        try { root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch { root = new JsonObject(); }

        var bots = root["Bots"] as JsonArray;
        if (bots is null || bots.Count == 0)
        {
            bots = new JsonArray(new JsonObject());
            root["Bots"] = bots;
        }
        var bot = (JsonObject)bots[0]!;
        var conn = bot["Connection"] as JsonObject ?? new JsonObject();
        conn["IP"] = ip;
        conn["Port"] = port;
        conn["Protocol"] ??= 0;
        bot["Connection"] = conn;
        bot["InitialRoutine"] = routine;
        File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool IsPythonHuntRunning() =>
        (PythonHunt is { HasExited: false }) || PythonProcesses().Any(p => CommandLineContains(p, "shinybot"));

    /// <summary>python / python3 / python3.12 ... processes.</summary>
    private static IEnumerable<Process> PythonProcesses() =>
        OsInfo.IsWindows ? Process.GetProcessesByName("python")
            : Process.GetProcesses().Where(p => { try { return p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase); } catch { return false; } });

    /// <summary>
    /// Health check for the Python hunter (every minute while it's in use):
    /// - shows the bot's own warnings (bot_warnings.log: same Pokémon repeating, reset not working...)
    /// - the same Pokémon logged 3x in a row in encounters.csv = stuck: restarts the hunt automatically
    /// - no new encounter for 5 minutes: checks the Switch is reachable (else asks for its new IP)
    /// - the hunter window closed without Stop being pressed: tells the user (shiny found? error?)
    /// </summary>
    private static IEnumerable<HealthItem> CheckPythonHunt()
    {
        if (_lastPython is not { } h) yield break;
        const string F = "Python hunter";
        // The bot itself (the console window stays open on "Press any key" after it ends, so don't look at cmd.exe).
        var running = Process.GetProcessesByName("python").Any(p => CommandLineContains(p, "shinybot"));

        var warnLog = Path.Combine(h.BotDir, "bot_warnings.log");
        if (File.Exists(warnLog) && new FileInfo(warnLog).Length > _warningsOffset)
        {
            using var fs = new FileStream(warnLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(Math.Max(0, _warningsOffset), SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _warningsOffset = fs.Length;
            foreach (var l in lines.TakeLast(3))
                HealthMonitor.Report(F, HealthState.Warning, "Bot: " + (l.Contains('\t') ? l[(l.IndexOf('\t') + 1)..] : l).Trim());
        }

        if (!running)
        {
            if (!_stopRequested && (DateTime.Now - _startedAt).TotalSeconds > 30)
            {
                _stopRequested = true; // report once
                yield return HealthMonitor.Warn(F, "The Python hunter has stopped. If it found a shiny its window says so; " +
                                                   "otherwise it stopped because of a problem (see its window) - start it again from the Shiny Hunt card.");
            }
            yield break;
        }

        var csv = Path.Combine(h.BotDir, "encounters.csv");
        if (File.Exists(csv))
        {
            if (FindRepeat(csv, _startedAt) is { } rep)
            {
                if (_autoRestarts < 3)
                {
                    _autoRestarts++;
                    StopPythonHunt();
                    Thread.Sleep(3000);
                    Start(h.Game, h.Key);
                    yield return HealthMonitor.Error(F, $"The same {rep.Species} (PID {rep.Pid}) was logged 3 times in a row - the game wasn't resetting. " +
                                                        $"Fixed automatically: the hunter was restarted (restart {_autoRestarts}/3).");
                }
                else
                    yield return HealthMonitor.Error(F, $"The same {rep.Species} (PID {rep.Pid}) keeps repeating even after 3 automatic restarts. " +
                                                        "Check the Switch (is the game frozen? another controller connected?).");
                yield break;
            }

            var idle = DateTime.Now - File.GetLastWriteTime(csv);
            var sinceStart = DateTime.Now - _startedAt;
            if (idle.TotalMinutes > 5 && sinceStart.TotalMinutes > 5)
            {
                var s = AppSettings.Current;
                if (!HealthMonitor.CanReach(s.SwitchIP, s.SwitchPort, 3000))
                {
                    SwitchUnreachable?.Invoke($"The Python hunter has had no encounter for {(int)idle.TotalMinutes} minutes and the Switch " +
                                              $"doesn't answer at {s.SwitchIP}:{s.SwitchPort}.");
                    yield return HealthMonitor.Error(F, $"Can't reach the Switch at {s.SwitchIP} - its IP address may have changed.");
                }
                else
                    yield return HealthMonitor.Warn(F, $"No new encounter for {(int)idle.TotalMinutes} minutes - check the hunter window and the game.");
            }
        }
    }

    /// <summary>The same Pokémon logged 3 times in a row (since <paramref name="since"/>) in the bot's encounters.csv, or null.</summary>
    public static (string Species, string Pid)? FindRepeat(string csvPath, DateTime since)
    {
        // columns: encounter,time,verdict,species,...,pid (index 30),note
        var rows = ReadLastLines(csvPath, 4).Select(l => l.Split(',')).Where(c => c.Length > 31)
            .Where(c => DateTime.TryParse(c[1], out var t) && t >= since).ToList();
        if (rows.Count >= 3 && rows.TakeLast(3).Select(c => c[30]).Distinct().Count() == 1)
            return (rows[^1][3], rows[^1][30]);
        return null;
    }

    /// <summary>The user started a hunt by hand: allow the automatic fixes again.</summary>
    public static void ResetAutoFixes() => _autoRestarts = 0;

    /// <summary>Restarts the Python hunt (after the Switch IP changed, for example). False if none was running.</summary>
    public static bool RestartPythonHunt()
    {
        if (_lastPython is not { } h) return false;
        StopPythonHunt();
        Thread.Sleep(2000);
        Start(h.Game, h.Key);
        return true;
    }

    private static IEnumerable<string> ReadLastLines(string path, int count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var len = fs.Length;
        fs.Seek(Math.Max(0, len - 4096), SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).TakeLast(count).ToList();
    }

    public static void StopPythonHunt()
    {
        _stopRequested = true;
        foreach (var p in PythonProcesses().Where(p => CommandLineContains(p, "shinybot")))
            try { p.Kill(entireProcessTree: true); } catch { }
        try { PythonHunt?.Kill(entireProcessTree: true); } catch { }
    }

    private static bool CommandLineContains(Process p, string text)
    {
        if (OsInfo.IsLinux)
        {
            try { return File.ReadAllText($"/proc/{p.Id}/cmdline").Replace('\0', ' ').Contains(text, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        if (OsInfo.IsMac)
        {
            try
            {
                using var ps = Process.Start(new ProcessStartInfo("ps", $"-o command= -p {p.Id}") { RedirectStandardOutput = true, UseShellExecute = false })!;
                var cmd = ps.StandardOutput.ReadToEnd();
                ps.WaitForExit(3000);
                return cmd.Contains(text, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {p.Id}");
            foreach (var o in searcher.Get())
                return (o["CommandLine"] as string ?? "").Contains(text, StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return false;
    }
}
