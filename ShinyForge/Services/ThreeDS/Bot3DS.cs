using System.Text;
using PKHeX.Core;
using ShinyForge.Core;

namespace ShinyForge.Services.ThreeDS;

public enum Hunt3DSMode { Auto, StaticSoftReset, WildWalking }

public sealed class Bot3DSOptions
{
    public string Host { get; set; } = "";
    public Hunt3DSMode Mode { get; set; } = Hunt3DSMode.Auto;
    public bool WalkHorizontal { get; set; } = true;
    public int WalkMs { get; set; } = 450;
    public int ResetWaitMs { get; set; } = 9000;     // after L+R+Start+Select before mashing A through the title screen
    public int EncounterTimeoutMs { get; set; } = 90_000;
    public string LogFile { get; set; } = Path.Combine(AppPaths.DataDir, "3ds_encounters.csv");
    /// <summary>Extra stop rules (natures / IVs / species, from Hunt options). Returns why to stop, or null.</summary>
    public Func<PKM, string?>? StopRule { get; set; }
}

/// <summary>
/// Shiny-hunting bot for the 3DS Pokémon games (X/Y, ORAS, Sun/Moon, Ultra Sun/Ultra Moon) and the Gen 1 / Gen 2 Virtual
/// Console games (Red, Blue, Yellow, Gold, Silver, Crystal - see <see cref="VirtualConsoleGb"/>) on a real 3DS with Luma3DS:
/// presses buttons through Rosalina InputRedirection and reads the wild Pokémon through Rosalina's debugger.
/// Recognises the game, logs every encounter (stats, IVs, EVs, nature, where it was met), stops on shinies, soft resets for
/// static/legendary encounters and runs from ordinary wild Pokémon (never from legendaries).
/// </summary>
public sealed class Bot3DS
{
    private readonly Bot3DSOptions _o;
    private LumaGdb? _gdb;
    private InputRedirection? _input;
    private uint? _wildAddress;           // remembered wild slot (Gen 6 scans once)
    private uint? _wram;                  // Virtual Console: where the Game Boy's 0xC000 is in the emulator (found once)
    private ulong _lastKey;
    private int _count;
    public Game3DS? Game { get; private set; }
    public (string Name, ushort TID, ushort SID) Trainer { get; private set; }
    public event Action<string>? Log;
    public event Action<PKM, bool>? Encounter; // Pokémon, isStopTarget

    public Bot3DS(Bot3DSOptions options) => _o = options;

    private void Say(string s) => Log?.Invoke(s);

    // ------------------------------------------------------------------ connection
    public Game3DS Connect(string? preferredGameId = null)
    {
        Say($"Connecting to the 3DS at {_o.Host}...");
        _input = new InputRedirection(_o.Host);
        _input.ReleaseAll();
        _gdb = new LumaGdb(_o.Host);
        _gdb.Connect();
        var procs = _gdb.ListProcesses();
        Game = Games3DS.Detect(procs, preferredGameId)
               ?? throw new InvalidOperationException("No supported Pokémon game is running on the 3DS (X/Y, ORAS, Sun/Moon, US/UM). " +
                                                      "Running: " + string.Join(", ", procs.Select(p => p.Name).Where(n => n.Length > 0).Take(12)));
        if (Game.Gb != null) { ConnectVirtualConsole(procs); return Game; }
        var proc = Games3DS.FindProcess(procs, Game)!;
        _gdb.Attach(proc.Pid);
        Say($"Detected {Game.Name} (process {proc.Name}, pid {proc.Pid}).");
        ReadTrainer();
        return Game;
    }

    /// <summary>Virtual Console: try the newest non-system processes until one holds the Game Boy RAM (the search runs once).</summary>
    private void ConnectVirtualConsole(List<GdbProcess> procs)
    {
        foreach (var proc in VirtualConsoleGb.Candidates(procs).Take(3))
        {
            _gdb!.Attach(proc.Pid);
            Say($"Looking for {Game!.Name} in process {proc.Name} (pid {proc.Pid})...");
            if (_wram is { } known && Probe(known)) { Say($"Game Boy RAM at 0x{known:X8} (remembered)."); ReadTrainer(); return; }
            var found = VirtualConsoleGb.FindWram(_gdb, Game.Gb!, Say);
            if (found != null)
            {
                _wram = found;
                Say($"Detected {Game.Name}: Game Boy RAM at 0x{found:X8} in process {proc.Name}.");
                ReadTrainer();
                return;
            }
            _gdb.Detach();
        }
        throw new InvalidOperationException($"Couldn't find {Game!.Name}'s memory on the 3DS. Start the game, CONTINUE into your save " +
                                            "(with at least one Pokémon in your party), then connect again.");
    }

    /// <summary>Is the remembered Game Boy RAM still there (the party list checks out)?</summary>
    private bool Probe(uint wram)
    {
        try { return VirtualConsoleGb.IsWram(_gdb!.Read(wram, 0x2000), 0, Game!.Gb!); }
        catch { return false; }
    }

    private void ReadTrainer()
    {
        try
        {
            if (Game!.Gb is { } gb && _wram is { } wram)
            {
                var (gbName, gbId) = VirtualConsoleGb.ReadTrainer(_gdb!, wram, gb);
                Trainer = (gbName, gbId, 0);
                Say($"Trainer {gbName}, ID {gbId:00000}.");
                return;
            }
            var id = _gdb!.Read(Game!.TrainerTID, 4);
            var nameRaw = _gdb.Read(Game.TrainerName, 26);
            var name = Encoding.Unicode.GetString(nameRaw).Split('\0')[0];
            Trainer = (name, BitConverter.ToUInt16(id, 0), BitConverter.ToUInt16(id, 2));
            var display = Game.Gen >= 7 ? $"{((uint)(Trainer.SID << 16 | Trainer.TID)) % 1_000_000:000000}" : Trainer.TID.ToString();
            Say($"Trainer {Trainer.Name}, ID {display}.");
        }
        catch (Exception e) { Say("Couldn't read the trainer card: " + e.Message); }
    }

    // ------------------------------------------------------------------ reading the wild Pokémon
    private PKM? ReadAt(uint addr)
    {
        try { return Game!.Decode(_gdb!.Read(addr, 232)); }
        catch (IOException) { throw; }
        catch { return null; }
    }

    /// <summary>The Pokémon in the current/last wild battle, or null.</summary>
    public PKM? ReadWild()
    {
        if (Game!.Gb is { } gb)
            return _wram is { } wram ? VirtualConsoleGb.ReadWild(_gdb!, wram, gb) : null;
        if (_wildAddress is { } known && ReadAt(known) is { } k)
            return k;
        foreach (var a in Game!.WildAddresses)
            if (ReadAt(a) is { } pk) { _wildAddress = a; return pk; }
        if (Game.WildScanLength > 0)
            return ScanGen6();
        return null;
    }

    /// <summary>Gen 6: the wild Pokémon lives somewhere in a 512 KB window; scan it (slow, done once, then remembered).</summary>
    private PKM? ScanGen6()
    {
        Say("Searching the 3DS memory for the wild Pokémon (first time only - this can take a little while)...");
        const int chunk = 0x6000;
        for (int off = 0; off < Game!.WildScanLength; off += chunk)
        {
            var data = _gdb!.Read(Game.WildScanStart + (uint)off, Math.Min(chunk + 232, Game.WildScanLength - off));
            for (int i = 0; i + 232 <= data.Length; i += 4)
            {
                var pk = Game.Decode(data.AsSpan(i, 232).ToArray());
                if (pk == null || pk.PID == 0) continue;
                _wildAddress = Game.WildScanStart + (uint)(off + i);
                Say($"Found the wild Pokémon at 0x{_wildAddress:X8}.");
                return pk;
            }
        }
        return null;
    }

    /// <summary>Identity of an encounter (a new battle overwrites the wild slot; PID alone isn't trusted).</summary>
    private static ulong Key(PKM pk) =>
        ((ulong)pk.PID << 32 ^ pk.EncryptionConstant) ^ ((ulong)pk.Species << 48) ^ (ulong)(uint)(pk.IV_HP | pk.IV_ATK << 5 | pk.IV_DEF << 10 | pk.IV_SPE << 15 | pk.IV_SPA << 20 | pk.IV_SPD << 25) * 2654435761UL;

    private bool IsNew(PKM? pk) => pk != null && Key(pk) != _lastKey;

    // ------------------------------------------------------------------ actions
    private void SoftReset()
    {
        Say(Game!.Gb != null ? "Soft resetting (A+B+Start+Select)..." : "Soft resetting (L+R+Start+Select)...");
        _input!.Press(Game!.SoftReset, _resetHoldMs, 300);
    }

    // ------------------------------------------------------------------ same-Pokémon guard
    private int _resetHoldMs = 700;
    private int _stuck;                                    // static attempts in a row that didn't give a new Pokémon
    private readonly HashSet<ulong> _seen = new();          // every encounter of this hunt
    /// <summary>Raised for problems the user should know about (same Pokémon repeating, reset not working...).</summary>
    public event Action<string>? Warning;

    private void Warn(string text)
    {
        Say("⚠ WARNING: " + text);
        AppPaths.Log("3DS bot: " + text);
        Warning?.Invoke(text);
    }

    /// <summary>A static attempt ended without a new Pokémon (the old one stayed): the reset didn't work. Fix: longer hold + reconnect.</summary>
    private void FixStuckReset()
    {
        _stuck++;
        if (_stuck < 2) return;
        _resetHoldMs = Math.Min(_resetHoldMs + 500, 3000);
        Warn($"The same Pokémon is still there after {_stuck} soft resets - the reset isn't registering. " +
             $"Fixing automatically: holding the reset buttons {_resetHoldMs} ms and reconnecting to the 3DS.");
        try
        {
            var id = Game?.ShinyForgeId;
            _input?.Dispose();
            _gdb?.Dispose();
            Game = null;
            Connect(id);
        }
        catch (Exception e) { Warn("Reconnecting failed: " + e.Message); }
        if (_stuck >= 6) throw new InvalidOperationException("The soft reset still doesn't work after the automatic fixes - stopping.");
    }

    /// <summary>Mash A until a new wild Pokémon appears (static encounters, after a reset).</summary>
    private PKM? MashUntilEncounter(CancellationToken token)
    {
        var end = Environment.TickCount64 + _o.EncounterTimeoutMs;
        while (Environment.TickCount64 < end && !token.IsCancellationRequested)
        {
            var pk = ReadWild();
            if (IsNew(pk)) return pk;
            _input!.Press(N3DSButton.A, 120, 450);
        }
        return null;
    }

    /// <summary>Walk back and forth until a new wild Pokémon appears.</summary>
    private PKM? WalkUntilEncounter(CancellationToken token)
    {
        bool flip = false;
        int steps = 0;
        while (!token.IsCancellationRequested)
        {
            var dx = _o.WalkHorizontal ? (flip ? -1 : 1) : 0;
            var dy = _o.WalkHorizontal ? 0 : (flip ? -1 : 1);
            _input!.Tilt(dx, dy, _o.WalkMs);
            flip = !flip;
            if (++steps % 6 == 0) _input.Press(N3DSButton.B, 100, 150); // clear stray messages
            var pk = ReadWild();
            if (IsNew(pk)) return pk;
        }
        return null;
    }

    /// <summary>Run from a wild battle: B through the intro text, then Run (D-pad Down from FIGHT, then A), repeated.
    /// Game Boy games: RUN is bottom-right of FIGHT / PKMN / ITEM (PACK) / RUN - Down, Right, A.</summary>
    private void Flee(CancellationToken token)
    {
        Say("Running away...");
        Thread.Sleep(3500); // battle intro
        for (int i = 0; i < 4 && !token.IsCancellationRequested; i++)
        {
            _input!.Press(N3DSButton.B, 120, 300);
            _input.Press(N3DSButton.B, 120, 300);
            _input.Press(N3DSButton.Down, 120, 250);
            if (Game!.Gb != null) _input.Press(N3DSButton.Right, 120, 250);
            _input.Press(N3DSButton.A, 120, 1800);
            if (Game.Gb != null && ReadWild() == null) break; // the battle is over
        }
        for (int i = 0; i < 6; i++) _input!.Press(N3DSButton.B, 100, 400);
    }

    // ------------------------------------------------------------------ main loop
    public void Run(CancellationToken token)
    {
        if (Game == null) Connect();
        _lastKey = ReadWild() is { } current ? Key(current) : 0;
        var mode = _o.Mode;
        if (mode == Hunt3DSMode.Auto)
            mode = DetectMode(token);
        Say($"Mode: {(mode == Hunt3DSMode.StaticSoftReset ? "soft-reset (static encounter)" : "wild encounters")}.");

        while (!token.IsCancellationRequested)
        {
            var pk = mode == Hunt3DSMode.StaticSoftReset ? MashUntilEncounter(token) : WalkUntilEncounter(token);
            if (token.IsCancellationRequested) break;
            if (pk == null)
            {
                Say("No encounter before the timeout.");
                if (mode == Hunt3DSMode.StaticSoftReset)
                {
                    if (ReadWild() is { } old && Key(old) == _lastKey) FixStuckReset(); // the old Pokémon is still there
                    SoftReset(); Thread.Sleep(_o.ResetWaitMs); continue;
                }
                break;
            }
            _stuck = 0;
            _lastKey = Key(pk);
            if (!_seen.Add(_lastKey))
                Warn($"{SpeciesName.GetSpeciesName(pk.Species, 2)} (PID {pk.PID:X8}) was already found earlier in this hunt - " +
                     "it's counted again but the timing is being varied more.");
            var stop = HandleEncounter(pk);
            if (stop)
            {
                _input!.ReleaseAll();
                Say("Stopping - the game is left in the battle for you.");
                return;
            }

            if (mode == Hunt3DSMode.StaticSoftReset)
            {
                SoftReset();
                Thread.Sleep(_o.ResetWaitMs);
            }
            else if (Games3DS.IsLegendaryOrMythical(pk))
            {
                Say($"{SpeciesName.GetSpeciesName(pk.Species, 2)} is legendary - not running. Switching to soft resets (save in front of it first!).");
                mode = Hunt3DSMode.StaticSoftReset;
                SoftReset();
                Thread.Sleep(_o.ResetWaitMs);
            }
            else
            {
                Flee(token);
            }
        }
        _input?.ReleaseAll();
    }

    /// <summary>Auto: press A a few times; if a battle starts it's a static encounter in front of the player, otherwise walk.</summary>
    private Hunt3DSMode DetectMode(CancellationToken token)
    {
        Say("Auto mode: checking whether a Pokémon is in front of you (pressing A)...");
        for (int i = 0; i < 12 && !token.IsCancellationRequested; i++)
        {
            _input!.Press(N3DSButton.A, 120, 700);
            var pk = ReadWild();
            if (IsNew(pk))
            {
                Say($"A battle started with {SpeciesName.GetSpeciesName(pk!.Species, 2)} - static encounter.");
                _lastKey = 0; // let the main loop handle this first encounter
                return Hunt3DSMode.StaticSoftReset;
            }
        }
        _input!.Press(N3DSButton.B, 100, 300);
        return Hunt3DSMode.WildWalking;
    }

    private bool HandleEncounter(PKM pk)
    {
        _count++;
        var legendary = Games3DS.IsLegendaryOrMythical(pk);
        Say($"Encounter {_count}:{Environment.NewLine}{Format(pk)}");
        AppendCsv(pk);
        var rule = pk.IsShiny ? null : _o.StopRule?.Invoke(pk);
        Encounter?.Invoke(pk, pk.IsShiny || rule != null);
        if (pk.IsShiny || rule != null)
        {
            Say(pk.IsShiny ? $"★ SHINY {SpeciesName.GetSpeciesName(pk.Species, 2)} after {_count} encounters!"
                           : $"Stopping: {SpeciesName.GetSpeciesName(pk.Species, 2)} {rule}.");
            Sounds.Play();
            return true;
        }
        _ = legendary;
        return false;
    }

    public static string LocationName(PKM pk)
    {
        try
        {
            // A wild Pokémon still in battle hasn't been "met" yet: location 0 → PKHeX returns a dash placeholder.
            if (pk.MetLocation == 0) return "not recorded (wild battle)";
            var name = GameInfo.GetLocationName(false, pk.MetLocation, pk.Format, pk.Generation, pk.Version);
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Trim('—', '-', '–').Length == 0) return $"location {pk.MetLocation}";
            return name;
        }
        catch { return $"location {pk.MetLocation}"; }
    }

    public static string Format(PKM pk)
    {
        if (pk.Format <= 2)
        {
            // Game Boy: DVs (0-15). Shiny = the Gen 2 rule, which Pokémon Bank uses for Gen 1 / Gen 2 Pokémon too.
            var dv = $"DVs Atk {pk.IV_ATK} Def {pk.IV_DEF} Spe {pk.IV_SPE} Spc {pk.IV_SPA} (HP {pk.IV_HP})";
            return $"{SpeciesName.GetSpeciesName(pk.Species, 2)}  Lv.{pk.CurrentLevel}  Shiny: {(pk.IsShiny ? "Yes" : "No")}{Environment.NewLine}" +
                   $"   {dv}" + (pk.IsShiny ? $"{Environment.NewLine}   Shiny in Pokémon Bank too (Gen 2 DV rule)." : "");
        }
        var s = pk.Clone();
        s.ResetPartyStats();
        var shiny = !pk.IsShiny ? "No" : pk.ShinyXor == 0 ? "Square" : "Star";
        return $"{SpeciesName.GetSpeciesName(pk.Species, 2)}  Lv.{pk.MetLevel}  Shiny: {shiny}  Met: {LocationName(pk)}{Environment.NewLine}" +
               $"   Nature {(Nature)pk.Nature}  Ability {GameInfo.Strings.Ability[pk.Ability]}  HP {s.Stat_HPMax} Atk {s.Stat_ATK} Def {s.Stat_DEF} SpA {s.Stat_SPA} SpD {s.Stat_SPD} Spe {s.Stat_SPE}{Environment.NewLine}" +
               $"   IVs {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}  EVs {pk.EV_HP}/{pk.EV_ATK}/{pk.EV_DEF}/{pk.EV_SPA}/{pk.EV_SPD}/{pk.EV_SPE}  PID {pk.PID:X8}";
    }

    private void AppendCsv(PKM pk)
    {
        try
        {
            var s = pk.Clone();
            s.ResetPartyStats();
            var newFile = !File.Exists(_o.LogFile);
            using var w = new StreamWriter(_o.LogFile, append: true);
            if (newFile)
                w.WriteLine("time,game,encounter,species,level,location,hp,atk,def,spa,spd,spe,iv_hp,iv_atk,iv_def,iv_spa,iv_spd,iv_spe,iv_total," +
                            "nature,ability,shiny,legendary,pid");
            w.WriteLine(string.Join(",", new object[]
            {
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Game?.Name ?? "", _count, SpeciesName.GetSpeciesName(pk.Species, 2), pk.Format <= 2 ? pk.CurrentLevel : pk.MetLevel,
                $"\"{LocationName(pk)}\"", s.Stat_HPMax, s.Stat_ATK, s.Stat_DEF, s.Stat_SPA, s.Stat_SPD, s.Stat_SPE,
                pk.IV_HP, pk.IV_ATK, pk.IV_DEF, pk.IV_SPA, pk.IV_SPD, pk.IV_SPE, pk.IVTotal,
                // Game Boy Pokémon have no nature, ability or PID (Gen 1 / 2): the DVs go in the pid column instead
                pk.Format <= 2 ? "" : ((Nature)pk.Nature).ToString(), pk.Format <= 2 ? "" : GameInfo.Strings.Ability[pk.Ability],
                pk.IsShiny, Games3DS.IsLegendaryOrMythical(pk), pk.Format <= 2 ? $"DV{((GBPKM)pk).DV16:X4}" : pk.PID.ToString("X8"),
            }));
        }
        catch (Exception e) { Say("Couldn't write the encounter log: " + e.Message); }
    }

    public void Close()
    {
        try { _input?.Dispose(); } catch { }
        try { _gdb?.Dispose(); } catch { }
    }
}
