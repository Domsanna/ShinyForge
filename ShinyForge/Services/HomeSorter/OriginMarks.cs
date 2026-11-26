using System.Reflection;
using PKHeX.Core;
using ShinyForge.Core;

namespace ShinyForge.Services.HomeSorter;

/// <summary>
/// The origin marks HOME shows (the file names of Pokémon Automation's origin mark pictures) and which Pokémon can carry
/// each one, from PKHeX's game data: a Pokémon has the Galar mark only if it can be caught in Sword / Shield, the GO mark
/// only if it's in Pokémon GO, and so on. Used for the origin mark living dex: only Pokémon that can have the mark get a
/// slot, so the number of boxes it needs is right.
/// </summary>
public static class OriginMarks
{
    /// <summary>(mark, label) in the order shown in the window. "" = any origin.</summary>
    public static readonly (string Mark, string Label)[] All =
    {
        ("", "Any origin (no filter)"),
        ("go", "Pokémon GO"),
        ("galar", "Galar (Sword / Shield)"),
        ("lgpe", "Let's Go Pikachu / Eevee"),
        ("bdsp", "Brilliant Diamond / Shining Pearl"),
        ("la", "Legends: Arceus"),
        ("sv", "Scarlet / Violet (Paldea)"),
        ("lza", "Legends: Z-A"),
        ("alola", "Alola (Sun / Moon, Ultra Sun / Ultra Moon)"),
        ("kalos", "Kalos (X / Y, Omega Ruby / Alpha Sapphire)"),
        ("gameboy", "Game Boy (Virtual Console Red / Blue / Yellow / Gold / Silver / Crystal)"),
        ("gba", "Game Boy Advance"),
    };

    /// <summary>
    /// Can this template entry (species + form) carry the mark? Regional and Gigantamax forms only exist in some games:
    /// an Alolan form can't have the Game Boy mark, a Gigantamax one only the Galar mark, and so on.
    /// </summary>
    public static bool CanHave(string mark, DexEntry e)
    {
        if (!CanHave(mark, e.NatId)) return false;
        var form = e.Form.ToLowerInvariant();
        string[]? only = form.Contains("alola") ? new[] { "alola", "lgpe", "galar", "sv", "go", "lza" }
            : form.Contains("galar") ? new[] { "galar", "sv", "go" }
            : form.Contains("hisui") ? new[] { "la", "sv", "go", "lza" }
            : form.Contains("paldea") ? new[] { "sv", "go" }
            : form.Contains("gigantamax") ? new[] { "galar" }
            : null;
        return mark.Length == 0 || only == null || only.Contains(mark);
    }

    public static string Label(string mark) => All.FirstOrDefault(m => m.Mark == mark).Label ?? mark;

    private static HashSet<int>? _go;
    /// <summary>False when PKHeX's GO list couldn't be read (then every species counts for GO).</summary>
    public static bool GoListKnown { get; private set; } = true;

    /// <summary>Can a Pokémon of this species carry the mark? (Per species - its forms are counted with it.)</summary>
    public static bool CanHave(string mark, int species)
    {
        var sp = (ushort)species;
        bool In(IPersonalTable t) => sp <= t.MaxSpeciesID && t.IsPresentInGame(sp, 0);
        return mark switch
        {
            "" => true,
            "go" => GoSpecies().Contains(species),
            "galar" => In(PersonalTable.SWSH),
            "lgpe" => In(PersonalTable.GG),
            "bdsp" => In(PersonalTable.BDSP),
            "la" => In(PersonalTable.LA),
            "sv" => In(PersonalTable.SV),
            "lza" => In(PersonalTable.ZA),
            "alola" => species <= 809 && In(PersonalTable.USUM),
            "kalos" => species <= 721,
            "gameboy" => species <= 251,
            "gba" => species <= 386,
            _ => true,
        };
    }

    /// <summary>
    /// Species catchable in Pokémon GO, from PKHeX's GO encounter lists (EncountersGO.SlotsGO / SlotsGO_GG - internal, read
    /// by reflection). If PKHeX changes them, every species counts and <see cref="GoListKnown"/> says so.
    /// </summary>
    private static HashSet<int> GoSpecies()
    {
        if (_go != null) return _go;
        var set = new HashSet<int>();
        try
        {
            var type = typeof(PersonalTable).Assembly.GetType("PKHeX.Core.EncountersGO");
            foreach (var name in new[] { "SlotsGO", "SlotsGO_GG" })
                if (type?.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) is Array areas)
                    foreach (var area in areas)
                    {
                        // each area is one species (and form) with its GO encounter windows
                        var sp = area?.GetType().GetProperty("Species")?.GetValue(area) ?? area?.GetType().GetField("Species")?.GetValue(area);
                        if (sp != null) set.Add(Convert.ToInt32(sp));
                    }
        }
        catch (Exception e) { AppPaths.Log("HOME sorter: PKHeX's GO list couldn't be read: " + e.Message); }
        if (set.Count < 100) { GoListKnown = false; set = Enumerable.Range(1, 1025).ToHashSet(); }
        return _go = set;
    }
}
