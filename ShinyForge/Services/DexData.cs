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
    public string ShinySprite { get; init; } = "";   // Living Dex: the shiny Pokémon HOME picture ("Home\s\x.png")
    public int Sweet { get; init; } = -1;            // Living Dex: Alcremie's sweet (0 Strawberry ... 6 Ribbon), -1 = any
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
    /// <summary>
    /// The whole evolution family in this game, one line per branch, read left to right:
    ///   "Pidgey at Lv. 18 > [Pidgeotto] at Lv. 36 > Pidgeot"   ([ ] = the Pokémon you're looking at)
    ///   "Eevee with a Water Stone > Vaporeon", "Eevee with high friendship during the day > Espeon"...
    /// Every method (level, item, friendship, trade, held item, move, time of day, place, party, gender...) is written out,
    /// with this game's own places (e.g. Magnezone: Mt. Coronet in Diamond/Pearl, Chargestone Cave in Black/White).
    /// </summary>
    public static string Evolution(Game game, DexEntry e)
    {
        try
        {
            var context = Context(game);
            var tree = EvolutionTree.GetEvolutionTree(context);
            var table = GameData.GetPersonal(MainVersion(game));
            var pre = tree.Reverse.GetPreEvolutions(e.Species, e.Form).ToList();
            var (rootSp, rootForm) = pre.Count > 0 ? pre[0] : (e.Species, e.Form);
            var lines = new List<string>();

            bool InGame(ushort sp, byte form)
            {
                if (sp > table.MaxSpeciesID) return false;
                if (game.Id is "sw_lgp" or "sw_lge") return sp <= 151 || sp is 808 or 809;
                return game.Gen < 8 || table.IsPresentInGame(sp, form);
            }

            void Walk(ushort sp, byte form, string sofar, int depth)
            {
                var name = Name(game, sp, form);
                if (sp == e.Species && form == e.Form) name = $"[{name}]";
                // several methods to the same Pokémon (Feebas: Beauty or trade with a Prism Scale) = one step with "or"
                var steps = tree.Forward.GetForward(sp, form).ToArray()
                    .Select(m => (m, To: m.Species, Form: m.GetDestinationForm(form)))
                    .Where(x => x.m.Method != EvolutionType.None && InGame(x.To, x.Form))
                    .GroupBy(x => (x.To, x.Form)).ToList();
                if (steps.Count == 0 || depth > 4) { lines.Add(sofar + name); return; }
                foreach (var g in steps)
                {
                    var how = string.Join(" or ", g.Select(x => Describe(game, x.m)).Distinct());
                    Walk(g.Key.To, g.Key.Form, $"{sofar}{name} {how} > ", depth + 1);
                }
            }

            Walk(rootSp, rootForm, "", 0);
            if (lines.Count == 1 && !lines[0].Contains(" > ")) return "Does not evolve.";
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) { return "Evolution data unavailable: " + ex.Message; }
    }

    /// <summary>"Raichu", "Raichu (Alola)", "Meowstic" - a species with its form name in this game.</summary>
    private static string Name(Game game, ushort species, byte form)
    {
        var n = DexEntry.Plain(SpeciesName.GetSpeciesName(species, 2));
        try
        {
            var names = FormConverter.GetFormList(species, GameInfo.Strings.types, GameInfo.Strings.forms, new[] { "♂", "♀", "-" }, Context(game));
            // form 0 is named too when the species has other forms (Maushold (Family of Three) vs (Family of Four))
            if (form == 0 && names.Length < 2) return n;
            return form < names.Length && !string.IsNullOrWhiteSpace(names[form]) && names[form] is not ("♂" or "♀" or "Normal" or "-") ? $"{n} ({DexEntry.Plain(names[form])})" : n;
        }
        catch { return n; }
    }

    private static string A(string word) => (word.Length > 0 && "AEIOUaeiou".Contains(word[0]) ? "an " : "a ") + word;

    /// <summary>Where a place-based evolution happens in this game.</summary>
    private static string Place(Game game, string kind) => (kind, game.Id) switch
    {
        ("magnetic", "nds_heartgold" or "nds_soulsilver") => "at Mt. Coronet in Diamond, Pearl or Platinum (not possible in HeartGold / SoulSilver)",
        ("magnetic", _) when game.Gen == 4 => "at Mt. Coronet",
        ("magnetic", _) when game.Gen == 5 => "in Chargestone Cave",
        ("magnetic", "3ds_x" or "3ds_y") => "on Route 13",
        ("magnetic", "3ds_or" or "3ds_as") => "in New Mauville",
        ("magnetic", _) when game.Gen == 7 => "at Vast Poni Canyon or Blush Mountain",
        ("magnetic", _) => "in a special magnetic field",
        ("moss", "nds_heartgold" or "nds_soulsilver") => "near the Moss Rock in Eterna Forest in Diamond, Pearl or Platinum (not possible in HeartGold / SoulSilver)",
        ("moss", "sw_bd" or "sw_sp") => "near the Moss Rock in Eterna Forest",
        ("moss", _) when game.Gen == 4 => "near the Moss Rock in Eterna Forest",
        ("moss", _) when game.Gen == 5 => "near the Moss Rock in Pinwheel Forest",
        ("moss", "3ds_x" or "3ds_y") => "near the Moss Rock on Route 20",
        ("moss", "3ds_or" or "3ds_as") => "near the Moss Rock in Petalburg Woods",
        ("moss", _) when game.Gen == 7 => "near the Moss Rock in Lush Jungle",
        ("moss", _) => "near a Moss Rock",
        ("ice", "nds_heartgold" or "nds_soulsilver") => "near the Ice Rock on Route 217 in Diamond, Pearl or Platinum (not possible in HeartGold / SoulSilver)",
        ("ice", "sw_bd" or "sw_sp") => "near the Ice Rock on Route 217",
        ("ice", _) when game.Gen == 4 => "near the Ice Rock on Route 217",
        ("ice", _) when game.Gen == 5 => "near the Ice Rock in Twist Mountain",
        ("ice", "3ds_x" or "3ds_y") => "near the Ice Rock in Frost Cavern",
        ("ice", "3ds_or" or "3ds_as") => "near the Ice Rock in Shoal Cave",
        ("ice", _) when game.Gen == 7 => "near the Ice Rock on Mount Lanakila",
        ("ice", _) => "near an Ice Rock",
        _ => "",
    };

    /// <summary>One evolution step in plain words, written to follow the Pokémon's name ("Pidgey at Lv. 18 > Pidgeotto").</summary>
    private static string Describe(Game game, EvolutionMethod m)
    {
        var s = GameInfo.Strings;
        var items = s.GetItemStrings(Context(game), MainVersion(game));
        string Item() => m.Argument > 0 && m.Argument < items.Length ? items[m.Argument] : "an item";
        string Move() => m.Argument > 0 && m.Argument < s.movelist.Length ? s.movelist[m.Argument] : "a certain move";
        string Type() => m.Argument < s.types.Length ? s.types[m.Argument] : "?";
        string Mon() => m.Argument > 0 ? DexEntry.Plain(SpeciesName.GetSpeciesName(m.Argument, 2)) : "a certain Pokémon";
        var lv = m.Level > 0 ? $"at Lv. {m.Level}" : "when it levels up";
        var lvPlus = m.Level > 0 ? $" at Lv. {m.Level}+" : "";
        var noClock = game.Id is "gba_firered" or "gba_leafgreen" or "sw_frlg_fr" or "sw_frlg_lg"
            ? " - not possible in FireRed / LeafGreen (no clock): evolve it in Ruby, Sapphire or Emerald" : "";
        // Gen 2's data doesn't store Tyrogue's stat rule
        if (m.Method == EvolutionType.LevelUp && m.Species is 106 or 107 or 237)
            return lv + (m.Species == 106 ? " with Attack higher than Defense" : m.Species == 107 ? " with Defense higher than Attack" : " with Attack equal to Defense");
        // Legends: Arceus uses these two for "use an item during the day / at night" (Razor Claw: Sneasler, Weavile)
        if (Context(game) == EntityContext.Gen8a && m.Argument > 0 && m.Method is EvolutionType.TowerOfDarkness or EvolutionType.TowerOfWaters)
            return $"with {A(Item())} {(m.Method == EvolutionType.TowerOfDarkness ? "during the day" : "at night")}";
        return m.Method switch
        {
            EvolutionType.LevelUp => lv,
            EvolutionType.LevelUpFriendship => $"with high friendship (level up){lvPlus}",
            EvolutionType.LevelUpFriendshipMorning => "with high friendship during the day (level up)" + noClock,
            EvolutionType.LevelUpFriendshipNight => "with high friendship at night (level up)" + noClock,
            EvolutionType.LevelUpMorning => $"{lv} during the day",
            EvolutionType.LevelUpNight => $"{lv} at night",
            EvolutionType.LevelUpDusk => $"{lv} at dusk (5-6 PM)",
            EvolutionType.Trade => "when traded",
            EvolutionType.TradeHeldItem => $"when traded holding {A(Item())}",
            EvolutionType.TradeShelmetKarrablast => $"when traded for {A(Mon())}",
            EvolutionType.UseItem => $"with {A(Item())}",
            EvolutionType.UseItemMale => $"with {A(Item())} (male only)",
            EvolutionType.UseItemFemale => $"with {A(Item())} (female only)",
            EvolutionType.UseItemWormhole => $"with {A(Item())} in Ultra Space",
            EvolutionType.UseItemFullMoon => $"with {A(Item())} on a full moon night",
            EvolutionType.LevelUpWormhole => $"{lv} in Ultra Space",
            EvolutionType.LevelUpATK => $"{lv} with Attack higher than Defense",
            EvolutionType.LevelUpAeqD => $"{lv} with Attack equal to Defense",
            EvolutionType.LevelUpDEF => $"{lv} with Defense higher than Attack",
            EvolutionType.LevelUpECl5 or EvolutionType.LevelUpECgeq5 => $"{lv} (Silcoon or Cascoon is decided at random)",
            EvolutionType.LevelUpNinjask => lv,
            EvolutionType.LevelUpShedinja => $"{lv} with a free party slot and a spare Poké Ball (Shedinja appears next to Ninjask)",
            EvolutionType.LevelUpBeauty => "with high Beauty (level up)",
            EvolutionType.LevelUpHeldItemDay => $"{lv} during the day holding {A(Item())}",
            EvolutionType.LevelUpHeldItemNight => $"{lv} at night holding {A(Item())}",
            EvolutionType.LevelUpKnowMove => $"{lv} knowing {Move()}",
            EvolutionType.LevelUpKnowMoveEC100 => $"{lv} knowing {Move()} (rare - about 1 in 100)",
            EvolutionType.LevelUpKnowMoveECElse => $"{lv} knowing {Move()} (most of the time)",
            EvolutionType.LevelUpWithTeammate => $"{lv} with {A(Mon())} in the party",
            EvolutionType.LevelUpMale => $"{lv} (male only)",
            EvolutionType.LevelUpFemale => $"{lv} (female only)",
            EvolutionType.LevelUpFormFemale1 => $"{lv} (female)",
            EvolutionType.LevelUpElectric => $"when it levels up {Place(game, "magnetic")}",
            EvolutionType.LevelUpForest => $"when it levels up {Place(game, "moss")}",
            EvolutionType.LevelUpCold => $"when it levels up {Place(game, "ice")}",
            EvolutionType.LevelUpSummit => "when it levels up at Mount Lanakila",
            EvolutionType.LevelUpInverted => $"{lv} with the console held upside down",
            EvolutionType.LevelUpAffection50MoveType => game.Gen >= 8
                ? $"with high friendship (level up) knowing {A(Type())}-type move"
                : $"with two or more hearts of affection (level up) knowing {A(Type())}-type move",
            EvolutionType.LevelUpMoveType => $"{lv} with {A(Type())}-type Pokémon in the party",
            EvolutionType.LevelUpWeather => $"{lv} while it's raining (or foggy) outside",
            EvolutionType.LevelUpVersion => $"{lv} (which one depends on the game version)",
            EvolutionType.LevelUpVersionDay => $"{lv} during the day (which one depends on the game version)",
            EvolutionType.LevelUpVersionNight => $"{lv} at night (which one depends on the game version)",
            EvolutionType.CriticalHitsInBattle => "after landing 3 critical hits in one battle",
            EvolutionType.HitPointsLostInBattle => $"after losing at least {Math.Max(49, (int)m.Argument)} HP in battle without fainting, then passing under the stone arch (Dusty Bowl)",
            EvolutionType.Spin => "when you spin around while it holds a Sweet",
            EvolutionType.LevelUpNatureAmped => $"{lv} with a Hardy, Brave, Adamant, Naughty, Docile, Impish, Lax, Hasty, Jolly, Naive, Rash, Sassy or Quirky nature",
            EvolutionType.LevelUpNatureLowKey => $"{lv} with a Lonely, Bold, Relaxed, Timid, Serious, Modest, Mild, Quiet, Bashful, Calm, Gentle or Careful nature",
            EvolutionType.TowerOfDarkness => "after clearing the Tower of Darkness (Scroll of Darkness)",
            EvolutionType.TowerOfWaters => "after clearing the Tower of Waters (Scroll of Waters)",
            EvolutionType.LevelUpWalkStepsWith => $"{lv} after walking 1,000 steps with it in Let's Go mode",
            EvolutionType.LevelUpUnionCircle => $"{lv} while in a Union Circle with another player",
            EvolutionType.LevelUpInBattleEC100 => $"{lv} (rare - about 1 in 100)",
            EvolutionType.LevelUpInBattleECElse => $"{lv} (most of the time)",
            EvolutionType.LevelUpCollect999 => "when it levels up with 999 Gimmighoul Coins in the bag",
            EvolutionType.LevelUpDefeatEquals => "after defeating 3 Bisharp that hold a Leader's Crest (level up)",
            EvolutionType.LevelUpUseMoveSpecial => $"after using {Move()} 20 times (level up)",
            // Legends: Arceus doesn't store the move: Wyrdeer = Psyshield Bash, Overqwil = Barb Barrage
            EvolutionType.UseMoveAgileStyle => $"after using {(m.Argument > 0 ? Move() : m.Species == 899 ? "Psyshield Bash" : "a certain move")} 20 times in the Agile Style (level up)",
            EvolutionType.UseMoveStrongStyle => $"after using {(m.Argument > 0 ? Move() : m.Species == 904 ? "Barb Barrage" : "a certain move")} 20 times in the Strong Style (level up)",
            EvolutionType.UseMoveBarbBarrage => "after using Barb Barrage 20 times in the Strong Style (level up)",
            EvolutionType.LevelUpRecoilDamageMale => "after losing 294 HP to recoil damage without fainting (male, level up)",
            EvolutionType.LevelUpRecoilDamageFemale => "after losing 294 HP to recoil damage without fainting (female, level up)",
            _ => lv,
        };
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
    /// <summary>
    /// Serebii's Pokédex page for THIS game's generation (checked against serebii.net): each generation has its own Pokédex,
    /// and Let's Go is on the Gen 7 page, BD/SP and Legends: Arceus on the Sword/Shield page, Legends: Z-A on the
    /// Scarlet/Violet page. Gen 8/9 number links redirect to the name page (/pokedex-sv/pidgeot/).
    /// </summary>
    public static string SerebiiUrl(Game game, DexEntry e)
    {
        var dex = game.Id switch
        {
            "sw_lgp" or "sw_lge" => "pokedex-sm",
            "sw_bd" or "sw_sp" or "sw_pla" or "sw_sword" or "sw_shield" => "pokedex-swsh",
            "sw_scarlet" or "sw_violet" or "sw_za" => "pokedex-sv",
            _ => game.Gen switch
            {
                1 => "pokedex", 2 => "pokedex-gs", 3 => "pokedex-rs", 4 => "pokedex-dp", 5 => "pokedex-bw",
                6 => "pokedex-xy", 7 => "pokedex-sm", 8 => "pokedex-swsh", _ => "pokedex-sv",
            },
        };
        return $"https://www.serebii.net/{dex}/{e.Species:000}.shtml";
    }
}
