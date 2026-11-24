using System.Text.Json;
using ShinyForge.Core;

namespace ShinyForge.Services;

public enum HuntState { Active, Paused, Finished }

/// <summary>A span of time a hunt profile was the active one for its bot: the encounters logged in it belong to that hunt.</summary>
public sealed class HuntSegment
{
    public DateTime From { get; set; }
    public DateTime? To { get; set; }
}

/// <summary>
/// One hunt ("Shiny Deoxys in LeafGreen"): its game, target, when it was started and finished, how long was spent hunting and
/// which encounters are its own. Several can exist - start a different hunt and come back to this one later.
/// </summary>
public sealed class HuntProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string GameId { get; set; } = "";
    /// <summary>The Pokémon hunted ("" = any shiny). A shiny of it finishes the hunt.</summary>
    public string Target { get; set; } = "";
    public string Notes { get; set; } = "";
    public HuntState State { get; set; } = HuntState.Paused;
    public DateTime Created { get; set; } = DateTime.Now;
    /// <summary>The first time it was made the active hunt.</summary>
    public DateTime? Started { get; set; }
    public DateTime? Finished { get; set; }
    public string FoundSpecies { get; set; } = "";
    /// <summary>Time spent hunting: counted while the game's bot runs for this hunt, or while its timer is started by hand.</summary>
    public double Seconds { get; set; }
    /// <summary>The timer was started by hand (a hunt without a bot, or to count time the bot can't see).</summary>
    public bool TimerRunning { get; set; }
    /// <summary>Encounters counted by hand (+1 button) - a hunt without a bot log.</summary>
    public int ManualEncounters { get; set; }
    public List<HuntSegment> Segments { get; set; } = new();
    /// <summary>When the timer last added time (to fill in what a bot logged while ShinyForge was closed).</summary>
    public DateTime? LastCounted { get; set; }

    public Game? Game => GameCatalog.All.FirstOrDefault(g => g.Id == GameId);
    public string GameTitle => Game?.Title ?? GameId;
    public TimeSpan Time => TimeSpan.FromSeconds(Seconds);
    /// <summary>From starting the hunt until finishing it (or now) - days included, not only time spent hunting.</summary>
    public TimeSpan? Calendar => Started is { } s ? (Finished ?? DateTime.Now) - s : null;

    public bool Covers(DateTime t) => Segments.Any(s => t >= s.From && (s.To == null || t <= s.To));
}

/// <summary>The hunt profiles (hunt_profiles.json in ShinyForge's data folder) and the hunt timer.</summary>
public static class HuntProfiles
{
    private static readonly object Lock = new();
    private static List<HuntProfile>? _all;
    private static DateTime _lastTick = DateTime.MinValue, _lastSave = DateTime.MinValue;
    public static string FilePath => Path.Combine(AppPaths.DataDir, "hunt_profiles.json");

    /// <summary>Raised after any change (on whatever thread made it).</summary>
    public static event Action? Changed;

    public static List<HuntProfile> All
    {
        get { lock (Lock) return Load().ToList(); }
    }

    private static List<HuntProfile> Load()
    {
        if (_all != null) return _all;
        try
        {
            _all = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<HuntProfile>>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception e)
        {
            AppPaths.Log("hunt_profiles.json: " + e.Message);
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
            _all = new();
        }
        return _all;
    }

    private static void SaveLocked()
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Load(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, true);
        _lastSave = DateTime.Now;
    }

    private static void Change(Action<List<HuntProfile>> edit)
    {
        lock (Lock)
        {
            edit(Load());
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Which bot a game's hunt runs on: one active hunt per bot (FireRed and LeafGreen share the Python hunter).</summary>
    public static string BotGroup(Game? g) => g == null ? "" : g.Bot switch
    {
        BotKind.FrlgSwitch => "python",
        BotKind.SysBotLGPE or BotKind.SysBotSWSH or BotKind.SysBotBDSP => "sysbot",
        _ => g.Id,
    };

    public static HuntProfile? ActiveFor(Game game) => All.FirstOrDefault(p => p.State == HuntState.Active && BotGroup(p.Game) == BotGroup(game));

    public static HuntProfile Create(string name, Game game, string target, bool activate)
    {
        var p = new HuntProfile { Name = name.Trim(), GameId = game.Id, Target = target.Trim() };
        Change(l => l.Add(p));
        if (activate) Activate(p.Id);
        return Get(p.Id)!;
    }

    public static HuntProfile? Get(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>Makes it the active hunt of its bot; the hunt that was active there is paused (its time and encounters stay).</summary>
    public static void Activate(string id) => Change(l =>
    {
        var p = l.FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        var now = DateTime.Now;
        foreach (var other in l.Where(x => x.Id != id && x.State == HuntState.Active && BotGroup(x.Game) == BotGroup(p.Game)))
            PauseLocked(other, now);
        if (p.State == HuntState.Active) return;
        p.State = HuntState.Active;
        p.Finished = null;
        p.Started ??= now;
        p.Segments.Add(new HuntSegment { From = now });
    });

    public static void Pause(string id) => Change(l => { if (l.FirstOrDefault(x => x.Id == id) is { } p) PauseLocked(p, DateTime.Now); });

    private static void PauseLocked(HuntProfile p, DateTime now)
    {
        if (p.Segments.LastOrDefault() is { To: null } s) s.To = now;
        p.TimerRunning = false;
        if (p.State == HuntState.Active) p.State = HuntState.Paused;
    }

    /// <summary>The hunt is over (found it, or given up): its time stops for good, until it's reopened.</summary>
    public static void Finish(string id, string foundSpecies) => Change(l =>
    {
        if (l.FirstOrDefault(x => x.Id == id) is not { } p) return;
        var now = DateTime.Now;
        PauseLocked(p, now);
        p.State = HuntState.Finished;
        p.Started ??= p.Created;
        p.Finished = now;
        p.FoundSpecies = foundSpecies.Trim();
    });

    public static void Edit(string id, Action<HuntProfile> edit) => Change(l => { if (l.FirstOrDefault(x => x.Id == id) is { } p) edit(p); });

    public static void Delete(string id) => Change(l => l.RemoveAll(x => x.Id == id));

    /// <summary>Starts / stops the timer by hand. Starting it makes the hunt active.</summary>
    public static void SetTimer(string id, bool running)
    {
        if (running) Activate(id);
        Edit(id, p => p.TimerRunning = running && p.State == HuntState.Active);
    }

    // ------------------------------------------------------------------ the timer

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Hunting3DS = new();

    /// <summary>The 3DS bot started / stopped hunting a game (the game pages call this).</summary>
    public static void Set3DSHunting(string gameId, bool hunting)
    {
        if (hunting) Hunting3DS[gameId] = 0;
        else Hunting3DS.TryRemove(gameId, out _);
    }

    /// <summary>Is this game's bot hunting right now? (Python hunter, SysBot.exe, or the 3DS bot.)</summary>
    public static bool IsHunting(Game g) => g.Bot switch
    {
        BotKind.FrlgSwitch => HuntService.IsPythonHuntRunning(),
        BotKind.SysBotLGPE or BotKind.SysBotSWSH or BotKind.SysBotBDSP => HuntService.SysBotProcesses().Count > 0,
        _ => Hunting3DS.ContainsKey(g.Id),
    };

    /// <summary>
    /// Called every few seconds: adds the time since the last call to each active hunt whose bot is hunting
    /// (<paramref name="isHunting"/>) or whose timer was started by hand. If ShinyForge was closed while a bot kept hunting,
    /// the time it hunted is taken from its encounter log (encounters less than 10 minutes apart).
    /// </summary>
    public static void Tick() => Tick(IsHunting, AutoDetect);

    // ------------------------------------------------------------------ finding the hunt by itself

    /// <summary>A hunt was started from ShinyForge: remembered per bot (so a hunt still running after a restart is matched to its game).</summary>
    public static void NoteHuntStarted(Game game)
    {
        var s = AppSettings.Current;
        s.LastHuntGames[BotGroup(game)] = game.Id;
        s.Save();
    }

    /// <summary>The game each bot is hunting now (bot running, no active hunt yet) - for the automatic hunt profile.</summary>
    private static IEnumerable<Game> AutoDetect()
    {
        var s = AppSettings.Current;
        Game? Last(string group, string fallback) =>
            (s.LastHuntGames.TryGetValue(group, out var id) ? GameCatalog.All.FirstOrDefault(g => g.Id == id) : null) ?? GameCatalog.All.FirstOrDefault(g => g.Id == fallback);
        if (HuntService.IsPythonHuntRunning() && Last("python", "sw_frlg_lg") is { } py) yield return py;
        if (HuntService.SysBotProcesses().Count > 0 && Last("sysbot", "") is { } sb) yield return sb;
        foreach (var id in Hunting3DS.Keys)
            if (GameCatalog.All.FirstOrDefault(g => g.Id == id) is { } g3) yield return g3;
    }

    /// <summary>
    /// A bot is hunting a game that has no active hunt: the hunt is found by itself. A paused hunt of that game whose target is
    /// the PokÃ©mon being hunted is made active again; otherwise a new one is made from the encounter log - its target (the
    /// PokÃ©mon met most since the last shiny), start (the first encounter since the last shiny), encounters and the time
    /// already spent hunting all come from the log. Returns the hunt, or null.
    /// </summary>
    public static HuntProfile? AutoStart(Game game)
    {
        if (ActiveFor(game) != null) return null;
        var group = BotGroup(game);
        List<EncounterRow> phase = new();
        if (HuntStats.SourceFor(game) is { } src)
        {
            try
            {
                var rows = HuntStats.ReadRows(src.CsvPath, src.GameFilter, out _);
                phase = rows.Skip(rows.FindLastIndex(r => r.Shiny) + 1).ToList();
            }
            catch (Exception e) { AppPaths.Log("Hunt profile (log): " + e.Message); }
        }
        // the target: what the recent encounters are (a soft-reset hunt meets one PokÃ©mon), else the game's first stop target
        var recent = phase.TakeLast(100).GroupBy(r => r.Species).OrderByDescending(g => g.Count()).FirstOrDefault();
        var target = recent != null && recent.Count() >= phase.TakeLast(100).Count() * 0.6 ? recent.Key
            : AppSettings.Current.For(game.Id).StopTargets.FirstOrDefault() is { } t && t.Species > 0 ? PKHeX.Core.SpeciesName.GetSpeciesName(t.Species, 2) : "";

        // a paused hunt of this bot for the same PokÃ©mon (same game, or the other FireRed / LeafGreen) comes back
        var paused = All.Where(p => p.State == HuntState.Paused && BotGroup(p.Game) == group &&
                                    (target.Length > 0 ? Collection.Normalize(p.Target) == Collection.Normalize(target) : p.GameId == game.Id))
                        .OrderByDescending(p => p.GameId == game.Id).ThenByDescending(p => p.Segments.LastOrDefault()?.To ?? p.Created).FirstOrDefault();
        if (paused != null)
        {
            Activate(paused.Id);
            AppPaths.Log($"Hunt '{paused.Name}' continued by itself ({game.Title} is hunting)");
            return Get(paused.Id);
        }

        var now = DateTime.Now;
        var from = phase.FirstOrDefault()?.Time ?? now;
        var p = new HuntProfile
        {
            Name = $"{(target.Length > 0 ? target : "Shiny")} hunt ({game.Title})", GameId = game.Id, Target = target, State = HuntState.Active,
            Created = now, Started = from, LastCounted = now, Notes = "Started by itself when the bot began hunting" +
                                                                       (phase.Count > 0 ? $" - picked up {phase.Count:N0} encounters since the last shiny from the log." : "."),
            Segments = { new HuntSegment { From = from } },
            Seconds = HuntingTime(phase.Select(r => r.Time).ToList()),
        };
        Change(l =>
        {
            foreach (var other in l.Where(x => x.State == HuntState.Active && BotGroup(x.Game) == group)) PauseLocked(other, now);
            l.Add(p);
        });
        AppPaths.Log($"Hunt '{p.Name}' started by itself: {phase.Count:N0} encounters and {Format(p.Time)} of hunting from the log");
        return Get(p.Id);
    }

    /// <summary>Time spent hunting in a list of encounter times: the gaps between encounters less than 10 minutes apart.</summary>
    private static double HuntingTime(List<DateTime> times)
    {
        double total = 0;
        for (int i = 1; i < times.Count; i++)
        {
            var d = (times[i] - times[i - 1]).TotalSeconds;
            if (d is > 0 and < 600) total += d;
        }
        return total;
    }

    /// <summary>Raised when a hunt was found / started by itself (for a status message).</summary>
    public static event Action<HuntProfile>? AutoStarted;

    public static void Tick(Func<Game, bool> isHunting, Func<IEnumerable<Game>>? autoDetect = null)
    {
        if (autoDetect != null)
            foreach (var g in autoDetect().ToList())
                if (AutoStart(g) is { } started) AutoStarted?.Invoke(started);
        var now = DateTime.Now;
        var since = _lastTick;
        _lastTick = now;
        List<HuntProfile> active;
        lock (Lock) active = Load().Where(p => p.State == HuntState.Active).ToList();
        if (active.Count == 0) return;
        var add = new Dictionary<string, double>();
        foreach (var p in active)
        {
            var g = p.Game;
            var hunting = p.TimerRunning || (g != null && isHunting(g));
            var gap = since == DateTime.MinValue ? 0 : (now - since).TotalSeconds;
            if (gap is > 0 and <= 30) { if (hunting) add[p.Id] = gap; }
            else if (g != null)
            {
                // first tick after starting ShinyForge (or after the PC slept): what the bot logged meanwhile
                var from = since == DateTime.MinValue ? LastCounted(p) : since;
                if (from != null && LoggedHuntingTime(p, g, from.Value, now) is var logged && logged > 0) add[p.Id] = logged;
            }
        }
        lock (Lock)
        {
            foreach (var p in Load())
                if (add.TryGetValue(p.Id, out var s)) { p.Seconds += s; p.LastCounted = now; }
            if (add.Count > 0 && (now - _lastSave).TotalSeconds >= 30) SaveLocked();
        }
        if (add.Count > 0) Changed?.Invoke();
    }

    /// <summary>Saves the timers now (ShinyForge closing).</summary>
    public static void Flush()
    {
        lock (Lock) if (_all != null) SaveLocked();
    }

    private static DateTime? LastCounted(HuntProfile p) => p.LastCounted ?? p.Segments.LastOrDefault()?.From;

    /// <summary>Hunting time in a log between two moments: the spans between encounters less than 10 minutes apart.</summary>
    private static double LoggedHuntingTime(HuntProfile p, Game g, DateTime from, DateTime to)
    {
        try
        {
            if (HuntStats.SourceFor(g) is not { } src) return 0;
            return HuntingTime(HuntStats.ReadRows(src.CsvPath, src.GameFilter, out _).Select(r => r.Time)
                                        .Where(t => t > from && t <= to && p.Covers(t)).ToList());
        }
        catch (Exception e) { AppPaths.Log("Hunt timer (log): " + e.Message); return 0; }
    }

    // ------------------------------------------------------------------ encounters and finds

    /// <summary>The hunt's encounters: its bot's log rows inside its active spans, plus the ones counted by hand.</summary>
    public static int Encounters(HuntProfile p, out int shinies)
    {
        shinies = 0;
        var n = p.ManualEncounters;
        if (p.Game is { } g && HuntStats.SourceFor(g) is { } src)
        {
            try
            {
                var rows = HuntStats.ReadRows(src.CsvPath, src.GameFilter, out _).Where(r => p.Covers(r.Time)).ToList();
                n += rows.Count;
                shinies = rows.Count(r => r.Shiny);
            }
            catch (Exception e) { AppPaths.Log("Hunt profile encounters: " + e.Message); }
        }
        return n;
    }

    /// <summary>
    /// A bot found a shiny: the active hunt of that bot is finished when the shiny is its target (or it has no target).
    /// Returns the finished hunt, or null.
    /// </summary>
    public static HuntProfile? OnFound(string botGroup, string species, bool shiny)
    {
        if (!shiny) return null;
        var p = All.FirstOrDefault(x => x.State == HuntState.Active && BotGroup(x.Game) == botGroup);
        if (p == null) return null;
        if (p.Target.Length > 0 && Collection.Normalize(p.Target) != Collection.Normalize(species)) return null;
        Finish(p.Id, species);
        AppPaths.Log($"Hunt '{p.Name}' finished: shiny {species} after {Format(p.Time)}");
        return Get(p.Id);
    }

    /// <summary>"3h 05m 12s", "2d 4h 10m".</summary>
    public static string Format(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes:00}m"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s"
        : $"{t.Minutes}m {t.Seconds:00}s";
}
