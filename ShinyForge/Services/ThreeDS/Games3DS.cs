using PKHeX.Core;

namespace ShinyForge.Services.ThreeDS;

/// <summary>
/// RAM layout of the 3DS Pokémon games (process virtual addresses, readable through Luma3DS's debugger).
/// Offsets from PKMN-NTR's LookupTable (drgoku282/PKMN-NTR) and SirSnorealot/3DSShinyHunter.
/// Gen 6 games keep the wild Pokémon at a moving address inside a window, so it is found by scanning that window once
/// and then re-checked at the remembered spot.
/// </summary>
public sealed record Game3DS(
    string Name, string ShinyForgeId, string[] ProcessNames, string[] TitleIds, int Gen,
    uint[] WildAddresses, uint WildScanStart, int WildScanLength,
    uint TrainerTID, uint TrainerName, N3DSButton SoftReset, GbMap? Gb = null)
{
    /// <summary>Decodes the 232-byte PK6/PK7 stored format (battle copies add stats after it).</summary>
    public PKM? Decode(byte[] data)
    {
        try
        {
            PKM pk = Gen == 6 ? new PK6(data.AsSpan(0, 232).ToArray()) : new PK7(data.AsSpan(0, 232).ToArray());
            return pk.Species is > 0 and <= 807 && pk.ChecksumValid ? pk : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// Game Boy RAM addresses (GB address space, WRAM starts at 0xC000) of a Gen 1 / Gen 2 game on the 3DS Virtual Console.
/// From pret's disassemblies (pokered / pokeyellow / pokegold / pokecrystal .sym files), English versions.
/// The VC emulator keeps the Game Boy's WRAM somewhere in its own memory; <see cref="VirtualConsoleGb"/> finds it.
/// </summary>
public sealed record GbMap(int Gen, ushort PartyCount, ushort PartyMons, ushort PlayerId, ushort PlayerName,
                           ushort Enemy, ushort EnemyDVs, ushort EnemyLevel, ushort BattleFlag)
{
    public const ushort WramStart = 0xC000;
    public static readonly GbMap RedBlue = new(1, 0xD163, 0xD16B, 0xD359, 0xD158, 0xCFE5, 0xCFF1, 0xCFF3, 0xD057); // wIsInBattle
    public static readonly GbMap Yellow = new(1, 0xD162, 0xD16A, 0xD358, 0xD157, 0xCFE4, 0xCFF0, 0xCFF2, 0xD056);
    public static readonly GbMap GoldSilver = new(2, 0xDA22, 0xDA2A, 0xD1A1, 0xD1A3, 0xD0EF, 0xD0F5, 0xD0FC, 0xD116); // wBattleMode
    public static readonly GbMap Crystal = new(2, 0xDCD7, 0xDCDF, 0xD47B, 0xD47D, 0xD206, 0xD20C, 0xD213, 0xD22D);
}

public static class Games3DS
{
    private const N3DSButton ResetCombo = N3DSButton.L | N3DSButton.R | N3DSButton.Start | N3DSButton.Select;
    private const N3DSButton GbResetCombo = N3DSButton.A | N3DSButton.B | N3DSButton.Start | N3DSButton.Select; // Game Boy soft reset

    public static readonly Game3DS[] All =
    {
        new("Pokémon X", "3ds_x", new[] { "kujira-1" }, new[] { "0004000000055D00" }, 6,
            Array.Empty<uint>(), 0x08800000, 0x80000, 0x08C79C3C, 0x08C79C84, ResetCombo),
        new("Pokémon Y", "3ds_y", new[] { "kujira-2" }, new[] { "0004000000055E00" }, 6,
            Array.Empty<uint>(), 0x08800000, 0x80000, 0x08C79C3C, 0x08C79C84, ResetCombo),
        new("Pokémon Omega Ruby", "3ds_or", new[] { "sango-1" }, new[] { "000400000011C400" }, 6,
            Array.Empty<uint>(), 0x08800000, 0x80000, 0x08C81340, 0x08C81388, ResetCombo),
        new("Pokémon Alpha Sapphire", "3ds_as", new[] { "sango-2" }, new[] { "000400000011C500" }, 6,
            Array.Empty<uint>(), 0x08800000, 0x80000, 0x08C81340, 0x08C81388, ResetCombo),
        new("Pokémon Sun", "3ds_sun", new[] { "niji_loc" }, new[] { "0004000000164800" }, 7,
            new uint[] { 0x3254F4AC, 0x32663BF0, 0x3003969C, 0x3002F7B8 }, 0, 0, 0x330D67D0, 0x330D6808, ResetCombo),
        new("Pokémon Moon", "3ds_moon", new[] { "niji_loc" }, new[] { "0004000000175E00" }, 7,
            new uint[] { 0x3254F4AC, 0x32663BF0, 0x3003969C, 0x3002F7B8 }, 0, 0, 0x330D67D0, 0x330D6808, ResetCombo),
        new("Pokémon Ultra Sun", "3ds_usun", new[] { "momiji" }, new[] { "00040000001B5000" }, 7,
            new uint[] { 0x3254F4AC, 0x32663BF0, 0x30039888, 0x3002F9A0 }, 0, 0, 0x33012818, 0x33012850, ResetCombo),
        new("Pokémon Ultra Moon", "3ds_umoon", new[] { "momiji" }, new[] { "00040000001B5100" }, 7,
            new uint[] { 0x3254F4AC, 0x32663BF0, 0x30039888, 0x3002F9A0 }, 0, 0, 0x33012818, 0x33012850, ResetCombo),
        // Game Boy / Game Boy Color Virtual Console (English). The process is found by elimination and checked by finding the
        // Game Boy RAM in it, so no process name is needed. Title IDs from the community title list (Bl4ckSh4rk).
        new("Pokémon Red (Virtual Console)", "gb_red", Array.Empty<string>(), new[] { "0004000000171000" }, 1,
            Array.Empty<uint>(), 0, 0, 0, 0, GbResetCombo, GbMap.RedBlue),
        new("Pokémon Blue (Virtual Console)", "gb_blue", Array.Empty<string>(), new[] { "0004000000171100" }, 1,
            Array.Empty<uint>(), 0, 0, 0, 0, GbResetCombo, GbMap.RedBlue),
        new("Pokémon Yellow (Virtual Console)", "gb_yellow", Array.Empty<string>(), new[] { "0004000000171200" }, 1,
            Array.Empty<uint>(), 0, 0, 0, 0, GbResetCombo, GbMap.Yellow),
        new("Pokémon Gold (Virtual Console)", "gbc_gold", Array.Empty<string>(), new[] { "0004000000172600" }, 2,
            Array.Empty<uint>(), 0, 0, 0, 0, GbResetCombo, GbMap.GoldSilver),
        new("Pokémon Silver (Virtual Console)", "gbc_silver", Array.Empty<string>(), new[] { "0004000000172700" }, 2,
            Array.Empty<uint>(), 0, 0, 0, 0, GbResetCombo, GbMap.GoldSilver),
        new("Pokémon Crystal (Virtual Console)", "gbc_crystal", Array.Empty<string>(), new[] { "0004000000172800" }, 2,
            Array.Empty<uint>(), 0, 0, 0, 0, GbResetCombo, GbMap.Crystal),
    };

    /// <summary>Picks the game from the running process list (Sun/Moon and US/UM share a process name; the
    /// title ID in the process info decides the version when Luma reports it).</summary>
    public static Game3DS? Detect(IEnumerable<GdbProcess> processes, string? preferredId = null)
    {
        var list = processes.ToList();
        // Virtual Console: the game the user picked (its process has no fixed name - VirtualConsoleGb finds it)
        if (All.FirstOrDefault(g => g.Gb != null && g.ShinyForgeId == preferredId) is { } vc) return vc;
        var candidates = All.Where(g => g.Gb == null && list.Any(p => g.ProcessNames.Any(n => p.Raw.Contains(n, StringComparison.OrdinalIgnoreCase)))).ToList();
        if (candidates.Count == 0) return null;
        var byTitle = candidates.FirstOrDefault(g => list.Any(p => g.TitleIds.Any(t => p.Raw.Contains(t, StringComparison.OrdinalIgnoreCase))));
        return byTitle ?? candidates.FirstOrDefault(g => g.ShinyForgeId == preferredId) ?? candidates[0];
    }

    public static GdbProcess? FindProcess(IEnumerable<GdbProcess> processes, Game3DS game) =>
        game.Gb != null ? VirtualConsoleGb.FindProcess(processes)
                        : processes.FirstOrDefault(p => game.ProcessNames.Any(n => p.Raw.Contains(n, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Static encounters on the 3DS games where the bot soft resets (never runs).</summary>
    public static bool IsLegendaryOrMythical(PKM pk) =>
        SpeciesCategory.IsLegendary(pk.Species) || SpeciesCategory.IsSubLegendary(pk.Species) || SpeciesCategory.IsMythical(pk.Species) ||
        pk.Species is (int)Species.TypeNull or (int)Species.Silvally or (int)Species.Poipole or (int)Species.Cosmog;
}
