using System.Text;
using System.Text.Json;
using PKHeX.Core;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>One Pokédex slot: a species, a form (regional/cosmetic) and optionally its female look.</summary>
public sealed class DexEntry
{
    public ushort Species { get; init; }
    public byte Form { get; init; }
    public bool Female { get; init; }
    public string Name { get; init; } = "";
    public string FormName { get; init; } = "";
    public string Types { get; init; } = "";
    public string Sprite { get; init; } = "";        // path relative to Assets: "DexVersions\<set>\x.png" (this game's own sprite) or "Dex\x.png"
    public bool GameSprite { get; init; }            // true when the sprite is from this game's own sprite set
    public string Display => FormName.Length == 0 ? Name + (Female ? " (female)" : "") : $"{Name} ({FormName}){(Female ? " (female)" : "")}";

    /// <summary>"Nidoran♀" → "Nidoran (F)": the ♀/♂ signs don't exist in the UI font.</summary>
    public static string Plain(string s) => s.Replace("♀", " (F)").Replace("♂", " (M)");
}

/// <summary>
/// Builds the per-game Pokédex (every species, form, regional form and gender difference that exists in that game) from
/// PKHeX's game data, and maps each entry to a sprite from PokeAPI's open sprite set (Assets\Dex, see tools\make_dex_assets.py).
/// </summary>
public static class DexData
{
    private sealed class FormInfoJson { public string id { get; set; } = ""; public string file { get; set; } = ""; public bool battle { get; set; } public bool @default { get; set; } public bool has { get; set; } = true; }
    private sealed class SpeciesJson { public string name { get; set; } = ""; public bool female { get; set; } public List<FormInfoJson> forms { get; set; } = new(); public string? femaleFile { get; set; } }

    private static Dictionary<int, SpeciesJson>? _index;
    public static string DexDir => Path.Combine(AppPaths.AssetsDir, "Dex");

    private static Dictionary<int, SpeciesJson> Index
    {
        get
        {
            if (_index != null) return _index;
            try
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, SpeciesJson>>(File.ReadAllText(Path.Combine(DexDir, "dex_index.json")));
                _index = raw!.ToDictionary(k => int.Parse(k.Key), v => v.Value);
            }
            catch (Exception e)
            {
                AppPaths.Log("dex_index.json: " + e.Message);
                _index = new();
            }
            return _index;
        }
    }

    /// <summary>
    /// The sprite set (Assets\DexVersions\&lt;set&gt;, made by tools\make_version_sprites.py from PokeAPI/sprites "versions")
    /// with the sprites from that very game. Gen 7 Sun/Moon use the Ultra Sun/Ultra Moon models (same art); Black 2/White 2
    /// use Black/White's.
    /// </summary>
    public static string? SpriteSet(Game game) => game.Id switch
    {
        "gb_red" or "gb_blue" => "red-blue",
        "gb_green" => "red-green-japan",
        "gb_yellow" => "yellow",
        "gbc_gold" => "gold",
        "gbc_silver" => "silver",
        "gbc_crystal" => "crystal",
        "gba_ruby" or "gba_sapphire" => "ruby-sapphire",
        "gba_emerald" => "emerald",
        "gba_firered" or "gba_leafgreen" or "sw_frlg_fr" or "sw_frlg_lg" => "firered-leafgreen",
        "nds_diamond" or "nds_pearl" => "diamond-pearl",
        "nds_platinum" => "platinum",
        "nds_heartgold" or "nds_soulsilver" => "heartgold-soulsilver",
        "nds_black" or "nds_white" or "nds_black2" or "nds_white2" => "black-white",
        "3ds_x" or "3ds_y" => "x-y",
        "3ds_or" or "3ds_as" => "omega-ruby-alpha-sapphire",
        "3ds_sun" or "3ds_moon" or "3ds_usun" or "3ds_umoon" => "ultra-sun-ultra-moon",
        "sw_lgp" or "sw_lge" => "lets-go-pikachu-lets-go-eevee",
        "sw_sword" or "sw_shield" => "sword-shield",
        "sw_bd" or "sw_sp" => "brilliant-diamond-shining-pearl",
        "sw_pla" => "legends-arceus",
        "sw_scarlet" or "sw_violet" => "scarlet-violet",
        "sw_za" => "legends-za",
        _ => null,
    };

    public static string VersionsDir => Path.Combine(AppPaths.AssetsDir, "DexVersions");

    /// <summary>This game's own sprite for <paramref name="file"/> if the set has it, else the generic one.</summary>
    private static (string Rel, bool Game) Resolve(string? set, string file)
    {
        if (file.Length == 0) return ("", false);
        if (set != null)
        {
            var rel = Path.Combine("DexVersions", set, file);
            if (File.Exists(Path.Combine(AppPaths.AssetsDir, rel))) return (rel, true);
        }
        return (Path.Combine("Dex", file), false);
    }

    public static GameVersion MainVersion(Game game) => game.Versions[0];
    public static EntityContext Context(Game game) => MainVersion(game).Context;

    private static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>All Pokédex entries for a game. <paramref name="includeForms"/> adds alternate/regional forms and female looks.</summary>
    public static List<DexEntry> Build(Game game, bool includeForms)
    {
        var version = MainVersion(game);
        var context = Context(game);
        var table = GameData.GetPersonal(version);
        var strings = GameInfo.Strings;
        var genders = new[] { "♂", "♀", "-" };
        var list = new List<DexEntry>();
        var modern = game.Gen >= 8; // these games list exactly which species/forms exist
        var set = SpriteSet(game);
        for (ushort sp = 1; sp <= table.MaxSpeciesID; sp++)
        {
            if (modern && !table.IsSpeciesInGame(sp)) continue;
            // Let's Go only has the original 151 + Meltan/Melmetal (its data tables list every species).
            if (game.Id is "sw_lgp" or "sw_lge" && sp > 151 && sp is not (808 or 809)) continue;
            var baseInfo = table.GetFormEntry(sp, 0);
            var formNames = FormConverter.GetFormList(sp, strings.types, strings.forms, genders, context);
            var formCount = includeForms ? Math.Max((int)baseInfo.FormCount, 1) : 1;
            // Gen 2-3 tables don't list Unown's letters as forms, but they are separate Pokédex entries.
            if (includeForms && sp == (ushort)Species.Unown && game.Gen is 2 or 3)
                formCount = Math.Max(formCount, formNames.Length);
            Index.TryGetValue(sp, out var idx);
            for (byte f = 0; f < formCount; f++)
            {
                if (f > 0)
                {
                    if (FormInfo.IsBattleOnlyForm(sp, f, (byte)game.Gen) || FormInfo.IsTotemForm(sp, f, context)) continue;
                    if (modern && !table.IsPresentInGame(sp, f)) continue;
                    if (sp is (ushort)Species.Pikachu && game.Gen is 6 && f > 0) continue; // cosplay Pikachu aren't separate dex entries
                }
                var pi = table.GetFormEntry(sp, f);
                var types = TypeText(game.Gen <= 2 ? PersonalTable.B2W2.GetFormEntry(sp, 0) : pi);
                var formName = f == 0 || formNames.Length <= f ? "" : formNames[f];
                if (f == 0 && formNames.Length > 0 && baseInfo.FormCount > 1 && includeForms && !string.IsNullOrWhiteSpace(formNames[0]) && formNames[0] != "Normal")
                    formName = formNames[0];
                var name = DexEntry.Plain(SpeciesName.GetSpeciesName(sp, (int)LanguageID.English));
                formName = DexEntry.Plain(formName).Trim();
                if (f > 0 && formName is "Lord" or "Lady") continue; // Legends: Arceus noble bosses can't be caught
                var (rel, own) = Resolve(set, SpriteFor(sp, f, formName, false, idx));
                // Let's Go: only forms the game really has (Alolan forms, partner Pikachu/Eevee) - its set has exactly those sprites.
                if (f > 0 && !own && game.Id is "sw_lgp" or "sw_lge") continue;
                // Legends: Arceus: e.g. Growlithe only exists as Hisuian Growlithe - drop a base form the game has no sprite for
                // when it does have one of the species' other forms.
                if (f == 0 && !own && game.Id == "sw_pla" && baseInfo.FormCount > 1 && set != null &&
                    idx?.forms.FirstOrDefault(x => x.file != idx.forms[0].file && File.Exists(Path.Combine(VersionsDir, set, x.file))) is { } other)
                {
                    if (includeForms) continue;                          // the Hisuian form is listed as its own entry
                    (rel, own) = (Path.Combine("DexVersions", set, other.file), true); // one entry per species: show the Hisuian look
                }
                list.Add(new DexEntry { Species = sp, Form = f, Name = name, FormName = formName, Types = types, Sprite = rel, GameSprite = own });

                // Visible gender differences exist from Generation 4 on.
                if (includeForms && f == 0 && game.Gen >= 4 && idx?.female == true && !pi.Genderless && pi.Gender is not (PersonalInfo.RatioMagicMale or PersonalInfo.RatioMagicFemale))
                {
                    var (frel, fown) = Resolve(set, SpriteFor(sp, 0, "", true, idx));
                    // Only when THIS game shows the female difference (e.g. Eevee's tail only from Sword/Shield on).
                    if (set != null && !fown) continue;
                    list.Add(new DexEntry { Species = sp, Form = 0, Female = true, Name = name, FormName = "", Types = types, Sprite = frel, GameSprite = fown });
                }
            }
        }
        return list;
    }

    /// <summary>Type names. Gen 1/2 tables store the games' internal type ids, so those are read from the modern table instead.</summary>
    private static string TypeText(IPersonalInfo pi)
    {
        var names = GameInfo.Strings.types;
        string N(int t) => t >= 0 && t < names.Length ? names[t] : "?";
        return pi.Type1 == pi.Type2 ? N(pi.Type1) : $"{N(pi.Type1)} / {N(pi.Type2)}";
    }

    private static string SpriteFor(ushort sp, byte form, string formName, bool female, SpeciesJson? idx)
    {
        if (idx == null) return "";
        if (female && idx.femaleFile != null) return idx.femaleFile;
        var forms = idx.forms.Where(f => f.has).ToList();
        if (form == 0 || forms.Count == 0)
            return forms.FirstOrDefault(f => f.@default)?.file ?? forms.FirstOrDefault()?.file ?? "";
        // PKHeX calls Battle Bond Greninja "Ash" - its look is the normal Greninja (the Ash form itself only appears in battle).
        if (sp == 658 && form == 1) return forms.FirstOrDefault(f => f.id == "battle-bond")?.file ?? forms[0].file;
        // Legends: Arceus' "Legend" Arceus (Legend Plate) looks like Normal Arceus.
        if (sp == 493 && formName == "Legend") return forms[0].file;
        var want = Norm(formName);
        if (want.Length > 0)
        {
            var exact = forms.FirstOrDefault(f => Norm(f.id) == want)
                        ?? forms.FirstOrDefault(f => Norm(f.id).Contains(want) || (want.Length > 3 && want.Contains(Norm(f.id)) && Norm(f.id).Length > 2));
            if (exact != null) return exact.file;
        }
        // Fall back to the n-th non-battle form, then the default sprite.
        var ordered = forms.Where(f => !f.battle).ToList();
        return form < ordered.Count ? ordered[form].file : forms.First().file;
    }

    // ------------------------------------------------------------------ info for a selected Pokémon
    public static string Evolution(Game game, DexEntry e)
    {
        var sb = new StringBuilder();
        try
        {
            var tree = EvolutionTree.GetEvolutionTree(Context(game));
            var pre = tree.Reverse.GetPreEvolutions(e.Species, e.Form).ToList();
            if (pre.Count > 0)
                sb.AppendLine("Evolves from: " + string.Join(" ← ", pre.Select(p => SpeciesName.GetSpeciesName(p.Item1, 2))));
            foreach (var m in tree.Forward.GetForward(e.Species, e.Form).Span)
                sb.AppendLine($"Evolves into {SpeciesName.GetSpeciesName(m.Species, 2)}{FormSuffix(game, m.Species, m.Form)}: {Describe(game, m)}");
            if (sb.Length == 0) sb.AppendLine("Does not evolve.");
        }
        catch (Exception ex) { sb.AppendLine("Evolution data unavailable: " + ex.Message); }
        return sb.ToString().TrimEnd();
    }

    private static string FormSuffix(Game game, ushort species, byte form)
    {
        if (form == 0) return "";
        try
        {
            var names = FormConverter.GetFormList(species, GameInfo.Strings.types, GameInfo.Strings.forms, new[] { "♂", "♀", "-" }, Context(game));
            return form < names.Length && !string.IsNullOrWhiteSpace(names[form]) ? $" ({names[form]})" : "";
        }
        catch { return ""; }
    }

    private static string Describe(Game game, EvolutionMethod m)
    {
        var s = GameInfo.Strings;
        var items = s.GetItemStrings(Context(game), MainVersion(game));
        var method = m.Method.ToString();
        var text = SplitWords(method);
        if (method.Contains("Item") && m.Argument > 0 && m.Argument < items.Length) text += $" ({items[m.Argument]})";
        else if (method.Contains("Move") && m.Argument > 0 && m.Argument < s.movelist.Length) text += $" ({s.movelist[m.Argument]})";
        else if (method.Contains("Species") && m.Argument > 0) text += $" ({SpeciesName.GetSpeciesName(m.Argument, 2)})";
        if (m.Level > 0) text += $", level {m.Level}+";
        return text;
    }

    private static string SplitWords(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Where/how to get this Pokémon in this game (wild areas, static/gift, trades, eggs, events), from PKHeX's encounter data.</summary>
    public static List<string> WhereToFind(Game game, DexEntry e, int max = 40)
    {
        var lines = new List<string>();
        try
        {
            var context = Context(game);
            var pk = EntityBlank.GetBlank(context, LanguageID.English);
            pk.Species = e.Species;
            pk.Form = e.Form;
            if (e.Female) pk.Gender = 1;
            // Same kind + species + place + game → one line with the combined level range.
            var groups = new Dictionary<string, (int Min, int Max, string Head, string Tail)>();
            var order = new List<string>();
            foreach (var enc in EncounterMovesetGenerator.GenerateEncounters(pk, ReadOnlyMemory<ushort>.Empty, game.Versions))
            {
                var (head, tail) = DescribeEncounter(enc);
                var key = head + "|" + tail;
                if (groups.TryGetValue(key, out var g))
                    groups[key] = (Math.Min(g.Min, enc.LevelMin), Math.Max(g.Max, enc.LevelMax), head, tail);
                else
                {
                    if (order.Count >= max) break;
                    groups[key] = (enc.LevelMin, enc.LevelMax, head, tail);
                    order.Add(key);
                }
            }
            foreach (var k in order)
            {
                var g = groups[k];
                lines.Add($"{g.Head} {(g.Min == g.Max ? $"Lv.{g.Min}" : $"Lv.{g.Min}-{g.Max}")}{g.Tail}");
            }
            if (order.Count >= max) lines.Add("...");
        }
        catch (Exception ex) { lines.Add("Encounter data unavailable: " + ex.Message); }
        if (lines.Count == 0)
            lines.Add("Not obtainable in this game by itself - trade or transfer it from another game.");
        return lines;
    }

    private static (string Head, string Tail) DescribeEncounter(IEncounterable enc)
    {
        var type = enc switch
        {
            MysteryGift => "Event",
            IEncounterEgg => "Egg (breed / hatch)",
            _ when enc.GetType().Name.Contains("Slot") => "Wild",
            _ when enc.GetType().Name.Contains("Trade") => "In-game trade",
            _ when enc.GetType().Name.Contains("Static") => "Static / gift",
            _ => "Encounter",
        };
        string where = "";
        try
        {
            if (enc is ILocation loc && loc.Location != 0)
                where = GameInfo.GetLocationName(false, loc.Location, enc.Generation, enc.Generation, enc.Version);
        }
        catch { }
        var name = enc is MysteryGift mg ? $"\"{mg.CardTitle}\"" : "";
        var species = SpeciesName.GetSpeciesName(enc.Species, 2);
        if (type == "Egg (breed / hatch)") where = ""; // the "location" of an egg is just where it hatched in PKHeX's template
        return ($"{type}: {species}", $"{(where.Length > 0 ? " - " + where : "")}{(name.Length > 0 ? " " + name : "")} ({enc.Version})");
    }

    /// <summary>Known event distributions (mystery gifts) of this species for the game's generation.</summary>
    public static List<string> Events(Game game, DexEntry e, int max = 25)
    {
        var lines = new List<string>();
        try
        {
            foreach (var mg in EncounterEvent.GetAllEvents(false))
            {
                if (mg.Species != e.Species || mg.Generation != game.Gen) continue;
                if (mg.Form != e.Form && (e.Form != 0 || e.FormName.Length > 0)) continue; // regional/alternate forms: only their own events
                lines.Add($"{mg.CardTitle} - {SpeciesName.GetSpeciesName(mg.Species, 2)} Lv.{mg.Level}{(mg.IsShiny ? " (shiny)" : "")} [{mg.Type}]");
                if (lines.Count >= max) { lines.Add("..."); break; }
            }
        }
        catch (Exception ex) { lines.Add("Event data unavailable: " + ex.Message); }
        return lines;
    }

    public static string BulbapediaUrl(DexEntry e) =>
        $"https://bulbapedia.bulbagarden.net/wiki/{Uri.EscapeDataString(SpeciesName.GetSpeciesName(e.Species, 2).Replace(' ', '_'))}_(Pok%C3%A9mon)";
    public static string SerebiiUrl(DexEntry e) => $"https://www.serebii.net/pokedex-sv/{e.Species:000}.shtml";
}
