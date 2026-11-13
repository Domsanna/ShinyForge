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
    uint TrainerTID, uint TrainerName, N3DSButton SoftReset)
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

public static class Games3DS
{
    private const N3DSButton ResetCombo = N3DSButton.L | N3DSButton.R | N3DSButton.Start | N3DSButton.Select;

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
    };

    /// <summary>Picks the game from the running process list (Sun/Moon and US/UM share a process name; the
    /// title ID in the process info decides the version when Luma reports it).</summary>
    public static Game3DS? Detect(IEnumerable<GdbProcess> processes, string? preferredId = null)
    {
        var list = processes.ToList();
        var candidates = All.Where(g => list.Any(p => g.ProcessNames.Any(n => p.Raw.Contains(n, StringComparison.OrdinalIgnoreCase)))).ToList();
        if (candidates.Count == 0) return null;
        var byTitle = candidates.FirstOrDefault(g => list.Any(p => g.TitleIds.Any(t => p.Raw.Contains(t, StringComparison.OrdinalIgnoreCase))));
        return byTitle ?? candidates.FirstOrDefault(g => g.ShinyForgeId == preferredId) ?? candidates[0];
    }

    public static GdbProcess? FindProcess(IEnumerable<GdbProcess> processes, Game3DS game) =>
        processes.FirstOrDefault(p => game.ProcessNames.Any(n => p.Raw.Contains(n, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Static encounters on the 3DS games where the bot soft resets (never runs).</summary>
    public static bool IsLegendaryOrMythical(PKM pk) =>
        SpeciesCategory.IsLegendary(pk.Species) || SpeciesCategory.IsSubLegendary(pk.Species) || SpeciesCategory.IsMythical(pk.Species) ||
        pk.Species is (int)Species.TypeNull or (int)Species.Silvally or (int)Species.Poipole or (int)Species.Cosmog;
}
