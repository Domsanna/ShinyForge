using PKHeX.Core;

namespace ShinyForge.Services.ThreeDS;

/// <summary>
/// Reads Pokémon Red / Blue / Yellow / Gold / Silver / Crystal running on the 3DS Virtual Console.
/// The VC emulator keeps the Game Boy's work RAM (0xC000-0xDFFF, Gen 2's banks in order) in a buffer of its own; where that
/// buffer is in the 3DS process isn't published, so it is FOUND: the process's writable memory regions (Luma's
/// "getmemregions") are searched once for the party list - count 1-6, that many species, the 0xFF end marker, the first
/// party Pokémon's species and a level of 1-100 at the addresses from pret's RAM maps (<see cref="GbMap"/>). The emulator
/// keeps the buffer for the whole session (soft resets included), so the address is remembered.
/// The game's save must be loaded (in-game, with at least one Pokémon in the party) when the search runs.
/// </summary>
public static class VirtualConsoleGb
{
    // 3DS system modules and applets: never the VC game.
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "fs", "pm", "sm", "loader", "pxi", "ns", "ptm", "cfg", "gsp", "hid", "ac", "am", "dsp", "mcu", "i2c", "csnd", "cecd", "nim",
        "news", "ndm", "friends", "boss", "act", "http", "ssl", "ps", "nwm", "mic", "ir", "mvd", "qtm", "dlp", "err", "menu", "rosalina",
        "pdn", "gpio", "spi", "codec", "mp", "nfc", "camera", "socket", "srv", "dmnt", "swkbd", "erreula", "mint", "extrapad", "memolib",
        "mset", "dlpclnt", "pnote", "rnote", "miiplugin", "bootnand", "processmanager", "",
    };

    /// <summary>Candidate processes for the VC game: not a system module, newest (highest pid) first.</summary>
    public static List<GdbProcess> Candidates(IEnumerable<GdbProcess> processes) =>
        processes.Where(p => !SystemProcesses.Contains(p.Name.Trim())).OrderByDescending(p => p.Pid).ToList();

    public static GdbProcess? FindProcess(IEnumerable<GdbProcess> processes) => Candidates(processes).FirstOrDefault();

    /// <summary>Where the Game Boy's 0xC000 is in the attached process, or null (searching every writable region once).</summary>
    public static uint? FindWram(LumaGdb gdb, GbMap map, Action<string>? progress = null)
    {
        const int chunk = 0x8000, overlap = 0x2000; // a whole 8 KB WRAM window fits into every step
        var regions = gdb.MemoryRegions().Where(r => r.Writable && r.Size >= 0x2000 && r.Size <= 0x4000000)
                         .OrderBy(r => r.State.Contains("Private", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(r => r.Start).ToList();
        var total = regions.Sum(r => (long)r.Size);
        long done = 0;
        foreach (var r in regions)
        {
            for (long off = 0; off < r.Size; off += chunk)
            {
                var len = (int)Math.Min(chunk + overlap, r.Size - off);
                byte[] data;
                try { data = gdb.Read(r.Start + (uint)off, len); }
                catch (IOException) { break; } // unreadable region: skip it
                for (var p = 0; p + 0x2000 <= data.Length; p++)
                    if (IsWram(data, p, map)) return r.Start + (uint)(off + p);
                done += Math.Min(chunk, len);
                if (off % (chunk * 32) == 0) progress?.Invoke($"Searching the 3DS memory for the Game Boy RAM ({done * 100 / Math.Max(1, total)}%)...");
            }
        }
        return null;
    }

    /// <summary>Does <paramref name="d"/> hold the Game Boy's 0xC000 at index <paramref name="b"/>? (the party list must check out)</summary>
    internal static bool IsWram(byte[] d, int b, GbMap m)
    {
        var pc = b + (m.PartyCount - GbMap.WramStart);
        int n = d[pc];
        if (n is < 1 or > 6) return false;
        for (var i = 1; i <= n; i++)
        {
            var s = d[pc + i];
            if (s is 0 or 0xFF) return false;
            if (m.Gen == 1 ? SpeciesConverter.GetNational1(s) == 0 : s > 251 && s != 0xFD) return false; // 0xFD = an egg (Gen 2)
        }
        if (d[pc + 1 + n] != 0xFF) return false;
        var mon = b + (m.PartyMons - GbMap.WramStart);
        if (d[pc + 1] != 0xFD && d[mon] != d[pc + 1]) return false;
        var level = d[mon + (m.Gen == 1 ? 0x21 : 0x1F)];
        return level is >= 1 and <= 100;
    }

    /// <summary>The wild Pokémon of the current wild battle (null outside battles and in trainer battles).</summary>
    public static PKM? ReadWild(LumaGdb gdb, uint wram, GbMap m)
    {
        var flag = gdb.Read(wram + (uint)(m.BattleFlag - GbMap.WramStart), 1)[0];
        if (flag != 1) return null; // 0 = no battle, 2 = trainer battle
        var e = gdb.Read(wram + (uint)(m.Enemy - GbMap.WramStart), m.EnemyLevel - m.Enemy + 1);
        var dv0 = e[m.EnemyDVs - m.Enemy];
        var dv1 = e[m.EnemyDVs - m.Enemy + 1];
        return Make(m.Gen, e[0], e[m.EnemyLevel - m.Enemy], dv0, dv1);
    }

    /// <summary>A PK1 / PK2 from the battle data: species (Gen 1: internal index), level and the two DV bytes (Atk|Def, Spe|Spc).</summary>
    internal static PKM? Make(int gen, byte species, byte level, byte dv0, byte dv1)
    {
        var national = gen == 1 ? SpeciesConverter.GetNational1(species) : species;
        if (national is 0 or > 251 || level is 0 or > 100) return null;
        GBPKM pk = gen == 1 ? new PK1() : new PK2();
        pk.Species = national;
        pk.CurrentLevel = level;
        pk.DV16 = (ushort)(dv0 << 8 | dv1);
        return pk;
    }

    /// <summary>Trainer name and ID from WRAM (English text: A-Z 0x80.., a-z 0xA0.., 0-9 0xF6.., 0x50 ends the name).</summary>
    public static (string Name, ushort Id) ReadTrainer(LumaGdb gdb, uint wram, GbMap m)
    {
        var id = gdb.Read(wram + (uint)(m.PlayerId - GbMap.WramStart), 2);
        var raw = gdb.Read(wram + (uint)(m.PlayerName - GbMap.WramStart), 11);
        var name = new System.Text.StringBuilder();
        foreach (var c in raw)
        {
            if (c == 0x50) break;
            name.Append(c switch
            {
                >= 0x80 and <= 0x99 => (char)('A' + c - 0x80),
                >= 0xA0 and <= 0xB9 => (char)('a' + c - 0xA0),
                >= 0xF6 => (char)('0' + c - 0xF6),
                0x7F => ' ',
                _ => '?',
            });
        }
        return (name.ToString(), (ushort)(id[0] << 8 | id[1]));
    }
}
