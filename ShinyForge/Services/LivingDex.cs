using System.Text.Json;
using PKHeX.Core;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// The Living Dex: every Pokémon form that can be put in a Pokémon HOME box - each species, regional form, other form,
/// visible gender difference and Alcremie cream/sweet - in HOME's box order, with Pokémon HOME's own pictures.
/// The list (Assets\Home\home_dex.json) was built from Serebii's "Depositable Pokémon" list and checked slot by slot
/// against PKHeX's forms (tools\make_home_sprites.py + SelfTest homedex).
/// </summary>
public static class LivingDex
{
    private sealed class Row { public ushort species { get; set; } public byte form { get; set; } public bool female { get; set; } public int sweet { get; set; } public string name { get; set; } = ""; public string formName { get; set; } = ""; public string file { get; set; } = ""; }

    public static string HomeDir => Path.Combine(AppPaths.AssetsDir, "Home");

    /// <summary>All boxable entries (1,391 when written). Falls back to one entry per species if the HOME list is missing.</summary>
    public static List<DexEntry> Load()
    {
        try
        {
            var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(Path.Combine(HomeDir, "home_dex.json")))!;
            return rows.Select(r => new DexEntry
            {
                Species = r.species, Form = r.form, Female = r.female, Sweet = r.species == (ushort)Species.Alcremie ? r.sweet : -1,
                Name = r.name, FormName = r.formName,
                Sprite = Path.Combine("Home", "n", r.file), ShinySprite = Path.Combine("Home", "s", r.file),
            }).ToList();
        }
        catch (Exception e)
        {
            AppPaths.Log("home_dex.json: " + e.Message);
            return Enumerable.Range(1, 1025).Select(sp => new DexEntry
            {
                Species = (ushort)sp, Name = DexEntry.Plain(SpeciesName.GetSpeciesName((ushort)sp, 2)), Sprite = Path.Combine("Dex", $"{sp}.png"),
            }).ToList();
        }
    }

    /// <summary>One entry per species (the first HOME slot of each) - the "species only" view.</summary>
    public static List<DexEntry> SpeciesOnly(IEnumerable<DexEntry> all) => all.GroupBy(e => e.Species).Select(g => g.First()).ToList();

    /// <summary>(species, form) pairs where the male and female look are separate entries (Venusaur, Pikachu, Hisuian Sneasel...).</summary>
    public static HashSet<(ushort, byte)> GenderSplit(IEnumerable<DexEntry> all) =>
        all.GroupBy(e => (e.Species, e.Form)).Where(g => g.Any(e => e.Female) && g.Any(e => !e.Female)).Select(g => g.Key).ToHashSet();

    /// <summary>Does this Pokémon fill this Living Dex slot? Same species, form, gender look (where it differs) and Alcremie sweet.</summary>
    public static bool Fills(DexEntry e, PKM pk, HashSet<(ushort, byte)> genderSplit)
    {
        if (pk.Species != e.Species) return false;
        var form = pk.Form;
        if (e.Species == (ushort)Species.Minior) form = (byte)(form % 7);                      // Meteor and Core forms of one colour
        if (e.Species == (ushort)Species.Pikachu && pk.Context == EntityContext.Gen6 && form > 0) return false; // cosplay Pikachu can't go to HOME
        if (form != e.Form) return false;
        if (genderSplit.Contains((e.Species, e.Form)) && (pk.Gender == 1) != e.Female) return false;
        if (e.Sweet >= 0 && (pk is not IFormArgument fa || fa.FormArgument != (uint)e.Sweet)) return false;
        return true;
    }

    /// <summary>The (species, form) pairs that exist in a game (for "in these games").</summary>
    public static HashSet<(ushort, byte)> FormsIn(Game game) =>
        DexData.Build(game, includeForms: true).Select(x => (x.Species, x.Form)).ToHashSet();

    /// <summary>A Pokédex entry of this game for the slot (its species + form), for "where to find it".</summary>
    public static DexEntry ForGame(Game game, DexEntry e)
    {
        var dex = DexData.Build(game, includeForms: true);
        return dex.FirstOrDefault(x => x.Species == e.Species && x.Form == e.Form && x.Female == e.Female)
               ?? dex.FirstOrDefault(x => x.Species == e.Species && x.Form == e.Form)
               ?? e;
    }
}
