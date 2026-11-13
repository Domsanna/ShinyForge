using System.Globalization;
using System.Text;
using PKHeX.Core;

namespace ShinyForge.Services;

/// <summary>What the player owns, per species: in box/party, caught by the player (own OT), shiny.</summary>
public sealed class Collection
{
    public sealed class Entry
    {
        public string Name = "";
        public bool Owned;
        public bool OwnOT;
        public bool Shiny;
    }

    /// <summary>One Pokémon the player has, with where it is (for the Pokédex window).</summary>
    public sealed record Owned(PKM Pk, bool OwnOT, string Where);

    public readonly Dictionary<string, Entry> BySpecies = new();
    public readonly List<Owned> Mons = new();
    /// <summary>Species the save's own Pokédex marks as caught (even if no longer owned).</summary>
    public readonly HashSet<ushort> DexCaught = new();
    public int Total;
    public string Source = "";

    public void Add(PKM pk, bool ownOT, string where = "")
    {
        if (pk.Species == 0 || pk.IsEgg)
            return;
        Mons.Add(new Owned(pk, ownOT, where));
        var name = SpeciesName.GetSpeciesName(pk.Species, (int)LanguageID.English);
        var key = Normalize(name);
        if (!BySpecies.TryGetValue(key, out var e))
            BySpecies[key] = e = new Entry { Name = name };
        e.Owned = true;
        e.OwnOT |= ownOT;
        e.Shiny |= pk.IsShiny;
        Total++;
    }

    public static Collection FromSave(SaveFile sav, string source)
    {
        var c = new Collection { Source = source };
        var slot = 0;
        foreach (var pk in SaveService.Party(sav))
            c.Add(pk, SaveService.IsOwnOT(pk, sav), $"Party slot {++slot}");
        foreach (var (box, s, pk) in SaveService.Boxes(sav))
            c.Add(pk, SaveService.IsOwnOT(pk, sav), $"Box {box + 1}, slot {s + 1}");
        try
        {
            for (ushort sp = 1; sp <= sav.MaxSpeciesID; sp++)
                if (sav.GetCaught(sp)) c.DexCaught.Add(sp);
        }
        catch { /* some saves have no readable Pokédex */ }
        return c;
    }

    /// <summary>"Nidoran (F)" / "Nidoran♀" -> "nidoranf", "Mr. Mime" -> "mrmime", "Flabébé" -> "flabebe".</summary>
    public static string Normalize(string name)
    {
        name = name.Replace("♀", "f").Replace("♂", "m");
        var d = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
