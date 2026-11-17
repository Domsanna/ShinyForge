using System.Text.Json;
using PKHeX.Core;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Stop targets: "stop when THIS Pokémon shows up" - any Pokémon obtainable in the game, with a form, a gender and shiny or
/// not. Which Pokémon a game has, its forms, genders and whether it can be shiny come from PKHeX's own game data:
/// a Pokémon is offered when PKHeX knows at least one way to get it in that game (wild, gift, trade, egg...), and it can
/// be shiny unless every one of those is shiny-locked. Impossible picks (a male Chansey, a shiny-locked shiny) are refused
/// by <see cref="Problem"/> with the message the user sees. The bots always stop on any shiny on top of the targets.
/// </summary>
public static class StopTargets
{
    /// <summary>A Pokémon a game offers: its forms (index = form number), gender ratio and whether it can be shiny there.</summary>
    public sealed record Option(ushort Species, string Name, string[] Forms, byte GenderRatio, bool ShinyPossible)
    {
        public bool Genderless => GenderRatio == PersonalInfo.RatioMagicGenderless;
        public bool OnlyFemale => GenderRatio == PersonalInfo.RatioMagicFemale;
        public bool OnlyMale => GenderRatio == PersonalInfo.RatioMagicMale;
        public override string ToString() => $"{Name} (#{Species})";
    }

    public const string NotExisting = "That Pokémon does not exist. Please choose a different Pokémon or check your conditions and try again.";

    /// <summary>Does the game tell star from square shinies (Sword / Shield)? Otherwise there's just "shiny".</summary>
    public static bool HasSquareShinies(Game game) => game.Id is "sw_sword" or "sw_shield";

    private static readonly Dictionary<string, List<Option>> Cache = new();
    private static readonly object Gate = new();

    private static string CacheFile(Game game) =>
        Path.Combine(AppPaths.DataDir, "Stop targets", $"{game.Id} - PKHeX {typeof(SaveFile).Assembly.GetName().Version}.json");

    /// <summary>
    /// The Pokémon the game offers (sorted by Pokédex number). Working it out asks PKHeX for every species' encounters,
    /// which takes a few seconds the first time: the result is kept in Data\Stop targets (per PKHeX version).
    /// </summary>
    public static List<Option> Available(Game game)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(game.Id, out var hit)) return hit;
            var file = CacheFile(game);
            List<Option>? list = null;
            try { if (File.Exists(file)) list = JsonSerializer.Deserialize<List<Option>>(File.ReadAllText(file)); } catch { list = null; }
            if (list == null || list.Count == 0)
            {
                list = Compute(game);
                try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, JsonSerializer.Serialize(list)); }
                catch (Exception e) { AppPaths.Log("Stop targets cache: " + e.Message); }
            }
            Cache[game.Id] = list;
            return list;
        }
    }

    private static List<Option> Compute(Game game)
    {
        var version = game.Versions[0];
        var context = DexData.Context(game);
        var personal = GameData.GetPersonal(version);
        var strings = GameInfo.GetStrings("en");
        var list = new List<Option>();
        for (ushort species = 1; species <= personal.MaxSpeciesID; species++)
        {
            if (!personal.IsSpeciesInGame(species)) continue;
            var info = personal[species];
            var names = FormConverter.GetFormList(species, strings.Types, strings.forms, GameInfo.GenderSymbolASCII, context);
            // Gen 2/3 Unown: the letter comes from the DVs / PID, not from data - every letter is there once Unown is
            var derived = species == (ushort)Species.Unown && game.Gen <= 3;
            var formCount = derived ? names.Length : Math.Max((byte)1, info.FormCount);
            var forms = new string[formCount];
            var anyEncounter = false;
            var shiny = false;
            for (byte form = 0; form < formCount; form++)
            {
                forms[form] = form < names.Length && names[form].Length > 0 ? names[form] : form == 0 ? "Normal" : $"Form {form}";
                if (!derived && !personal.IsPresentInGame(species, form)) { forms[form] = ""; continue; }
                // Event gifts don't count (a bot can't hunt them), and each encounter must give THIS form (or a random one).
                var encs = Encounters(game, species, form)
                    .Where(e => e is not MysteryGift && (derived || e.Form == form || e.Form >= formCount)).ToList();
                if (encs.Count == 0 && !derived) { forms[form] = ""; continue; }
                anyEncounter |= encs.Count > 0;
                shiny |= encs.Any(e => e.Shiny != Shiny.Never);
            }
            if (!anyEncounter) continue; // in the game's data, but no way to get it there (outside events)
            list.Add(new Option(species, SpeciesName.GetSpeciesName(species, 2), forms, info.Gender, shiny));
        }
        return list;
    }

    /// <summary>PKHeX's encounters that give this species / form in the game (any of its versions), evolutions included.</summary>
    private static IEnumerable<IEncounterable> Encounters(Game game, ushort species, byte form)
    {
        var pk = EntityBlank.GetBlank(DexData.Context(game), LanguageID.English);
        pk.Species = species;
        pk.Form = form;
        pk.SetGender(pk.GetSaneGender());
        try { return EncounterMovesetGenerator.GenerateEncounters(pk, ReadOnlyMemory<ushort>.Empty, game.Versions).Take(200).ToList(); }
        catch { return Array.Empty<IEncounterable>(); }
    }

    /// <summary>Why the target can't exist in this game (the message for the user), or null when it can.</summary>
    public static string? Problem(Game game, StopTarget t)
    {
        var o = Available(game).FirstOrDefault(x => x.Species == t.Species);
        if (o == null) return $"{SpeciesName.GetSpeciesName(t.Species, 2)} isn't in Pokémon {game.Title}. " + NotExisting;
        if (t.Form >= 0 && (t.Form >= o.Forms.Length || o.Forms[t.Form].Length == 0))
            return $"{o.Name} has no form #{t.Form} in Pokémon {game.Title}. " + NotExisting;
        var gender = t.Gender.ToLowerInvariant();
        if (gender == "male" && (o.OnlyFemale || o.Genderless)) return $"There is no male {o.Name} ({(o.Genderless ? "it's genderless" : "it's always female")}). " + NotExisting;
        if (gender == "female" && (o.OnlyMale || o.Genderless)) return $"There is no female {o.Name} ({(o.Genderless ? "it's genderless" : "it's always male")}). " + NotExisting;
        if (t.Shiny is "shiny" or "star" or "square" && !o.ShinyPossible) return $"{o.Name} can't be shiny in Pokémon {game.Title} (it's shiny-locked). " + NotExisting;
        if (t.Shiny is "star" or "square" && !HasSquareShinies(game)) return $"Pokémon {game.Title} has no star / square shinies - choose \"Shiny\". " + NotExisting;
        return null;
    }

    /// <summary>Does an encounter match the target? gender: 0 male, 1 female, 2 genderless; square = square shiny (Sword / Shield).</summary>
    public static bool Matches(StopTarget t, ushort species, byte form, int gender, bool shiny, bool square)
    {
        if (species != t.Species) return false;
        if (t.Form >= 0 && form != t.Form) return false;
        if (t.Gender == "male" && gender != 0) return false;
        if (t.Gender == "female" && gender != 1) return false;
        return t.Shiny switch
        {
            "shiny" => shiny,
            "star" => shiny && !square,
            "square" => shiny && square,
            "not" => !shiny,
            _ => true,
        };
    }

    public static bool Matches(StopTarget t, PKM pk) =>
        Matches(t, pk.Species, pk.Form, pk.Gender, pk.IsShiny, pk.IsShiny && pk.ShinyXor == 0);

    /// <summary>"shiny female Pikachu (Partner)" style text.</summary>
    public static string Describe(Game game, StopTarget t)
    {
        var o = Available(game).FirstOrDefault(x => x.Species == t.Species);
        var name = o?.Name ?? SpeciesName.GetSpeciesName(t.Species, 2);
        var form = t.Form >= 0 && o != null && t.Form < o.Forms.Length && o.Forms.Length > 1 ? $" ({o.Forms[t.Form]})" : "";
        var shiny = t.Shiny switch { "shiny" => "shiny ", "star" => "star-shiny ", "square" => "square-shiny ", "not" => "non-shiny ", _ => "" };
        var gender = t.Gender is "male" or "female" ? t.Gender + " " : "";
        return $"{shiny}{gender}{name}{form}";
    }
}
