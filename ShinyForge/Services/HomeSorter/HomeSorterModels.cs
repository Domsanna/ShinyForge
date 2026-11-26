using System.Text.Json;
using ShinyForge.Core;
using System.Text.Json.Serialization;

namespace ShinyForge.Services.HomeSorter;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortMode { BoxSorter, LivingDex }

/// <summary>What a Pokémon's six markings (circle, triangle, square, heart, star, diamond) can be in HOME.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarkColor { None, Blue, Red }

/// <summary>Which colour of one marking excludes a Pokémon: not used, blue, red, or either colour.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarkRule { Off, Blue, Red, Any }

/// <summary>What happens to Pokémon excluded by their markings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarkedBehaviour
{
    /// <summary>Never moved: they keep their slot and everything is sorted around them.</summary>
    StayPut,
    /// <summary>Never used for the sort / living dex, but may be moved aside (to the reject boxes) to make room.</summary>
    MoveAside,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortCriterion { DexNumber, Level, Shiny, Gigantamax, Alpha, Ball, Gender, Nature, Ability, OriginMark, LanguageOfOrigin, Marked, OtName }

public sealed record SortRule(SortCriterion Criterion, bool Reverse);

/// <summary>The six marking shapes in HOME's order.</summary>
public static class Marks
{
    public static readonly string[] Names = { "Circle", "Triangle", "Square", "Heart", "Star", "Diamond" };
    public static readonly string[] Glyphs = { "●", "▲", "■", "♥", "★", "◆" };
}

/// <summary>
/// Everything the HOME sorter is set to. All of Pokémon Automation's Box Sorter and Box Sorter Living Dex options are here
/// (same names and defaults), plus ShinyForge's own: marking exclusions, a box range, move checking and the controller port.
/// Saved in settings.json.
/// </summary>
public sealed class HomeSorterOptions
{
    public SortMode Mode { get; set; } = SortMode.LivingDex;

    // ---- Box Sorter (Pokémon Automation: "Number of Boxes to Sort", "Sort Order Rules")
    /// <summary>First box to sort (1-based). Pokémon Automation always starts at the box HOME shows; ShinyForge goes there itself.</summary>
    public int SortStartBox { get; set; } = 2;
    public int BoxCount { get; set; } = 1;
    public List<SortRule> Rules { get; set; } = new() { new(SortCriterion.DexNumber, false) };
    /// <summary>"Home Language" - the language HOME shows (natures and abilities are read in it).</summary>
    public string HomeLanguage { get; set; } = "en";

    // ---- Box Sorter Living Dex
    public int LivingDexStartBox { get; set; } = 2;
    public int RejectBoxStart { get; set; } = 250;
    public int RejectBoxEnd { get; set; } = 300;

    /// <summary>
    /// Box 1 is never sorted into: HOME puts gifts, transfers and newly sent Pokémon there. Scans always start at box 1
    /// (it's read too - a new arrival that fills a missing living dex slot moves into the dex, the rest stays).
    /// </summary>
    /// <summary>Shown when the HOME sorter is opened: a full sort is a long job.</summary>
    public const string LongRunWarning =
        "Organizing Pokémon HOME can take a very long time, depending on how many Pokémon it needs to read and swap.\n\n" +
        "Reading the boxes means opening every Pokémon's summary: about 3,500 Pokémon in 300 boxes took about 3½ hours. " +
        "Each swap then moves one Pokémon, and the farther apart its two boxes are, the longer it takes - a living dex built " +
        "from a full HOME can need thousands of swaps, which can take a day or more.\n\n" +
        "Keep the Switch / Switch 2 on its dock or charger and leave HOME on the boxes. You can press Stop at any time and " +
        "continue later from where it stopped - everything read and moved so far is remembered.";

    public const string Box1Rule = "Box 1 is kept free: HOME puts Pokémon gifts and transfers in box 1, so nothing is ever sorted into it. " +
                                   "Every scan starts at box 1, so where each Pokémon is always counts from the same place.";
    /// <summary>A template file name from Pokémon Automation's Resources\PokemonHome\DexTemplates ("ajp_living_form") or "custom".</summary>
    public string LivingDexStyle { get; set; } = "living_dex_by_forms";
    public string CustomDexFile { get; set; } = "";
    public bool ShinyDex { get; set; }
    /// <summary>
    /// With Shiny Dex on: first the regular living dex from the starting box, then the shiny living dex starting in the box
    /// right after the regular dex's last box (regular dex ends in box 84, shiny dex starts in box 85). Off = only a shiny
    /// dex, like Pokémon Automation's Shiny Dex.
    /// </summary>
    public bool ShinyAfterRegular { get; set; } = true;
    /// <summary>
    /// When a living dex slot has no fitting Pokémon in the dex and reject boxes, search every other box in HOME for one
    /// before leaving it empty.
    /// </summary>
    public bool SearchAllBoxes { get; set; } = true;
    /// <summary>
    /// Living dex of one origin mark ("go", "galar"... see <see cref="OriginMarks"/>; "" = any): only Pokémon that can have
    /// the mark get a slot (the template is closed up), only Pokémon with the mark are used, all others go to the reject boxes.
    /// </summary>
    public string OriginMarkFilter { get; set; } = "";
    /// <summary>After sorting, rename every empty box in the range to "HOME n" (n = its box number).</summary>
    public bool RenameEmptyBoxes { get; set; } = true;
    /// <summary>After sorting, name each living dex box after the dex numbers in it ("1-30", "Shiny 31-60").</summary>
    public bool NameDexBoxes { get; set; }
    /// <summary>"OT Name Language" - "" = None (OT names aren't read).</summary>
    public string OtNameLanguage { get; set; } = "";
    public string OtName { get; set; } = "";

    // ---- shared setup
    /// <summary>"Capture Card Delay" (ms): how late the capture card shows what the console does.</summary>
    public int VideoDelayMs { get; set; } = 400;
    /// <summary>"Pokémon Home App Delay" (ms): the wait after each button press.</summary>
    public int GameDelayMs { get; set; } = 240;
    public string OutputFile { get; set; } = "living_dex_order";
    public bool DryRun { get; set; }
    /// <summary>Phone/Discord notification when the program finishes (ShinyForge's ntfy notifications).</summary>
    public bool NotifyWhenFinished { get; set; } = true;

    // ---- ShinyForge extras
    /// <summary>Per marking shape (Marks.Names order): which colour of it excludes a Pokémon.</summary>
    public MarkRule[] MarkRules { get; set; } = new MarkRule[6];
    /// <summary>Exclude only when ALL the set markings match (false = any one of them is enough).</summary>
    public bool MarksMustAllMatch { get; set; }
    /// <summary>Leave out Pokémon that came from Pokémon GO (GO origin mark, or "first met in Pokémon GO").</summary>
    public bool ExcludeGo { get; set; }
    /// <summary>Keep the console awake: the board gives a harmless input every few minutes while ShinyForge runs.</summary>
    public bool KeepAwake { get; set; }
    public int KeepAwakeMinutes { get; set; } = 4;
    public MarkedBehaviour Marked { get; set; } = MarkedBehaviour.StayPut;
    /// <summary>After every move, read HOME's info bar to make sure the right Pokémon landed in the right slot.</summary>
    public bool VerifyMoves { get; set; } = true;
    /// <summary>Choose among duplicates: prefer the one that matches the OT name, then a shiny for a shiny dex, then the lowest level.</summary>
    public bool PickBestDuplicate { get; set; } = true;
    public string ComPort { get; set; } = "";
    /// <summary>"Past Runs" counters, like Pokémon Automation's stats line.</summary>
    public long PastPokemon { get; set; }
    public long PastEmpty { get; set; }
    public long PastSwaps { get; set; }
    public string Device { get; set; } = "";

    /// <summary>The options were filled in once from Pokémon Automation's own settings (never again after that).</summary>
    public bool ImportedFromPa { get; set; }

    /// <summary>
    /// Fills the options in from Pokémon Automation's UserSettings\SerialPrograms-Settings.json (its Box Sorter and Box
    /// Sorter Living Dex panels and their serial port), so a user coming from it doesn't type everything again.
    /// Returns what was taken over, or null when there was nothing to read.
    /// </summary>
    public static string? ImportFromPokemonAutomation(HomeSorterOptions o)
    {
        try
        {
            if (DexTemplates.ResourceFolder() is not { } res) return null;
            var file = Path.Combine(Path.GetDirectoryName(res)!, "UserSettings", "SerialPrograms-Settings.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file).TrimStart((char)0xFEFF));
            if (!doc.RootElement.TryGetProperty("99-Panels", out var panels)) return null;
            int Ms(JsonElement p, string k, int d) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String &&
                int.TryParse(new string(v.GetString()!.TakeWhile(char.IsDigit).ToArray()), out var ms) ? ms : d;
            int I(JsonElement p, string k, int d) => p.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : d;
            bool B(JsonElement p, string k, bool d) => p.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : d;
            string S(JsonElement p, string k, string d) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : d;
            var took = false;
            if (panels.TryGetProperty("PokemonHome:BoxSorterLivingDex", out var ld))
            {
                o.LivingDexStartBox = I(ld, "LIVING_DEX_START_BOX", o.LivingDexStartBox);
                o.RejectBoxStart = I(ld, "REJECT_BOX_START", o.RejectBoxStart);
                o.RejectBoxEnd = I(ld, "REJECT_BOX_END", o.RejectBoxEnd);
                o.LivingDexStyle = S(ld, "LIVING_DEX_STYLE", o.LivingDexStyle);
                o.CustomDexFile = S(ld, "CUSTOM_DEX_FILE", o.CustomDexFile);
                o.ShinyDex = B(ld, "SHINY_DEX", o.ShinyDex);
                o.OtName = S(ld, "OT_NAME", o.OtName);
                o.VideoDelayMs = Ms(ld, "VIDEO_DELAY", o.VideoDelayMs);
                o.GameDelayMs = Ms(ld, "GAME_DELAY", o.GameDelayMs);
                o.OutputFile = S(ld, "OUTPUT_FILE", o.OutputFile);
                o.DryRun = B(ld, "DRY_RUN", o.DryRun);
                if (ld.TryGetProperty("ConsoleSetup", out var cs) && cs.TryGetProperty("Controller", out var c) && S(c, "Serial: PABotBase2", "") is { Length: > 0 } port)
                    o.ComPort = port;
                took = true;
            }
            if (panels.TryGetProperty("PokemonHome:BoxSorter", out var bs))
            {
                o.BoxCount = I(bs, "BOX_NUMBER", o.BoxCount);
                if (bs.TryGetProperty("SORT_TABLE", out var table) && table.ValueKind == JsonValueKind.Array)
                {
                    var map = new Dictionary<string, SortCriterion>
                    {
                        ["dex"] = SortCriterion.DexNumber, ["level"] = SortCriterion.Level, ["shiny"] = SortCriterion.Shiny,
                        ["gigantamax"] = SortCriterion.Gigantamax, ["alpha"] = SortCriterion.Alpha, ["ball_slug"] = SortCriterion.Ball,
                        ["gender"] = SortCriterion.Gender, ["nature"] = SortCriterion.Nature, ["ability"] = SortCriterion.Ability,
                        ["origin_mark"] = SortCriterion.OriginMark, ["language_of_origin"] = SortCriterion.LanguageOfOrigin,
                    };
                    var rules = table.EnumerateArray().Where(r => map.ContainsKey(S(r, "sort_type", "")))
                        .Select(r => new SortRule(map[S(r, "sort_type", "")], B(r, "reverse", false))).ToList();
                    if (rules.Count > 0) o.Rules = rules;
                }
                took = true;
            }
            o.ImportedFromPa = true;
            return took ? "Filled in from your Pokémon Automation settings (its Box Sorter panels and COM port). Check them, then press Start." : null;
        }
        catch (Exception e) { AppPaths.Log("HOME sorter: couldn't read Pokémon Automation's settings: " + e.Message); return null; }
    }

    public bool AnyMarkRule => MarkRules.Any(r => r != MarkRule.Off);

    /// <summary>Do this Pokémon's markings exclude it from sorting?</summary>
    public bool IsExcluded(HomeMon m)
    {
        if (m.Unread) return true;
        if (ExcludeGo && m.FromGo) return true;
        if (!AnyMarkRule || m.Marks.Length < 6) return false;
        var results = MarkRules.Select((rule, i) => rule switch
        {
            MarkRule.Off => (bool?)null,
            MarkRule.Blue => m.Marks[i] == MarkColor.Blue,
            MarkRule.Red => m.Marks[i] == MarkColor.Red,
            _ => m.Marks[i] != MarkColor.None,
        }).Where(r => r != null).Select(r => r!.Value).ToList();
        return MarksMustAllMatch ? results.All(r => r) : results.Any(r => r);
    }
}

/// <summary>One Pokémon as read from HOME's summary screen.</summary>
public sealed class HomeMon
{
    public int Dex { get; set; }
    public string Species { get; set; } = "";
    public int Level { get; set; }
    public bool Shiny { get; set; }
    public bool Gigantamax { get; set; }
    public bool Alpha { get; set; }
    public string Ball { get; set; } = "";
    /// <summary>"male", "female" or "genderless".</summary>
    public string Gender { get; set; } = "genderless";
    public string OtName { get; set; } = "";
    public int OtId { get; set; } = -1;
    public string PrimaryType { get; set; } = "";
    public string SecondaryType { get; set; } = "";
    public string Nature { get; set; } = "";
    /// <summary>HP, Attack, Defense, Sp. Atk, Sp. Def, Speed from the summary (0 = unread) - with the moves, what makes
    /// each Pokémon its own even when two look alike in the box.</summary>
    public int[] Stats { get; set; } = new int[6];
    public List<string> Moves { get; set; } = new();
    public string Ability { get; set; } = "";
    public string OriginMark { get; set; } = "";
    public string Language { get; set; } = "";
    public MarkColor[] Marks { get; set; } = new MarkColor[6];
    /// <summary>
    /// The HOME sprite its box icon matches best (Pokémon Automation's sprite names, e.g. "minior-red-core",
    /// "alcremie-ruby-cream-berry", "unown-q", "vivillon-polar-shiny"); "" = not identified. HOME's summary never names the
    /// form, so this is how look-alike forms are told apart.
    /// </summary>
    public string Form { get; set; } = "";
    /// <summary>The form was identified clearly (the next best different form looked clearly less alike).</summary>
    public bool FormSure { get; set; }
    /// <summary>
    /// Its summary couldn't be read: it's never moved and never used (it keeps its slot), and the run carries on.
    /// </summary>
    public bool Unread { get; set; }
    /// <summary>Came from Pokémon GO: the GO origin mark, or the summary says it was first met in Pokémon GO.</summary>
    public bool FromGo { get; set; }
    /// <summary>Set by the planner from the options: never moved / never used.</summary>
    [JsonIgnore] public bool Excluded { get; set; }

    public string MarksText => string.Concat(Marks.Select((m, i) => m switch
    {
        MarkColor.Blue => "b" + HomeSorter.Marks.Glyphs[i],
        MarkColor.Red => "r" + HomeSorter.Marks.Glyphs[i],
        _ => "",
    }));

    /// <summary>Everything that tells this individual Pokémon apart from a look-alike.</summary>
    public string Identity => $"{Dex}|{Shiny}|{Level}|{Gender}|{OtId}|{Nature}|{Ability}|{string.Join(",", Stats)}|{string.Join(",", Moves)}";

    /// <summary>The details that make it this Pokémon: nature, ability, stats, moves.</summary>
    public string Details => string.Join(" | ", new[]
    {
        string.Join(", ", new[] { Nature, Ability }.Where(x => x.Length > 0)),
        Stats.Any(x => x > 0) ? $"HP {Stats[0]} Atk {Stats[1]} Def {Stats[2]} SpA {Stats[3]} SpD {Stats[4]} Spe {Stats[5]}" : "",
        string.Join(" / ", Moves),
    }.Where(x => x.Length > 0));

    public override string ToString() =>
        $"#{Dex:0000} {Species}{(Shiny ? " ★" : "")}{(Gigantamax ? " G-Max" : "")}{(Alpha ? " Alpha" : "")} Lv.{Level} {Gender} {Ball} " +
        $"{PrimaryType}{(SecondaryType.Length > 0 ? "/" + SecondaryType : "")} OT {OtName} ({OtId}){(MarksText.Length > 0 ? " marks " + MarksText : "")}{(FromGo ? " [GO]" : "")}{(Form.Length > 0 ? $" form {Form}{(FormSure ? "" : "?")}" : "")}";
}

/// <summary>A box slot: box (0-based), row 0-4, column 0-5.</summary>
public readonly record struct Slot(int Box, int Row, int Col)
{
    public const int Rows = 5, Cols = 6, PerBox = 30, MaxBoxes = 300;
    public int Index => Box * PerBox + Row * Cols + Col;
    public static Slot FromIndex(int i) => new(i / PerBox, i % PerBox / Cols, i % Cols);
    public override string ToString() => $"box {Box + 1} row {Row + 1} col {Col + 1}";
}

/// <summary>One slot of a living dex template (Pokémon Automation's DexTemplates format).</summary>
public sealed record DexEntry(string Species, string Form, int NatId, string PrimaryType, string SecondaryType, string Slug, bool GenderDifference)
{
    public bool IsEmpty => NatId == 0;
    /// <summary>Set in a combined layout: this slot wants a shiny (true) or a regular (false) Pokémon; null = the Shiny Dex option.</summary>
    public bool? Shiny { get; init; }
    public static readonly DexEntry Empty = new("empty", "", 0, "", "", "", false);
}

public static class DexTemplates
{
    /// <summary>The styles Pokémon Automation ships, in its order (file name, label).</summary>
    public static readonly (string File, string Label)[] Styles =
    {
        ("living_dex_by_forms", "Sorted by Forms"), ("living_dex_by_species", "Sorted by Species"),
        ("ajp_final_form", "AJP Final Form"), ("ajp_final_form_form", "AJP Final Form Form"), ("ajp_living", "AJP Living"),
        ("ajp_living_form", "AJP Living Form"), ("ajp_living_form_lite", "AJP Living Form Lite"),
        ("pokepc_fully_sorted", "PokePC Fully Sorted"), ("pokepc_fully_sorted_minimal", "PokePC Fully Sorted (Minimal)"),
        ("pokepc_grouped_balanced", "PokePC Grouped Balanced"), ("pokepc_grouped_region", "PokePC Grouped by Region"),
        ("pokepc_sorted_species", "PokePC Sorted by Species"), ("pokepc_sorted_species_minimal", "PokePC Sorted by Species (Minimal)"),
        ("pokepc_species_first", "PokePC Species First"), ("custom", "Custom"),
    };

    /// <summary>Pokémon Automation's Resources folder (next to SerialPrograms-Windows.cmd), or null.</summary>
    public static string? ResourceFolder()
    {
        if (PokemonAutomation.Find() is not { } cmd) return null;
        var res = Path.Combine(Path.GetDirectoryName(cmd)!, "Resources");
        return Directory.Exists(res) ? res : null;
    }

    public static string? PathFor(HomeSorterOptions o) =>
        o.LivingDexStyle == "custom" ? o.CustomDexFile
        : ResourceFolder() is { } r ? Path.Combine(r, "PokemonHome", "DexTemplates", o.LivingDexStyle + ".json") : null;

    /// <summary>
    /// The slots from the living dex's starting box on. With Shiny Dex and "after the regular dex": the template as a regular
    /// dex, empty slots up to the end of its last box, then the template again as a shiny dex - so the shiny dex starts in
    /// the box after the regular dex's last box.
    /// </summary>
    public static List<DexEntry> Layout(List<DexEntry> template, HomeSorterOptions o)
    {
        // an origin mark dex only has slots for Pokémon that can carry the mark, one after another
        if (o.OriginMarkFilter.Length > 0)
            template = template.Where(e => !e.IsEmpty && OriginMarks.CanHave(o.OriginMarkFilter, e)).ToList();
        if (!o.ShinyDex || !o.ShinyAfterRegular) return template;
        var list = template.Select(e => e with { Shiny = false }).ToList();
        while (list.Count % Slot.PerBox != 0) list.Add(DexEntry.Empty);
        list.AddRange(template.Select(e => e with { Shiny = true }));
        return list;
    }

    /// <summary>
    /// Do the box settings fit the chosen style? The style (and Shiny Dex after the regular dex) decides how many boxes the
    /// living dex needs; they must fit in HOME's boxes, the reject range must be the right way round and must not overlap
    /// them. Each problem says what to change the setting to. Empty = fine.
    /// </summary>
    public static List<string> BoxProblems(int layoutCount, HomeSorterOptions o, int total = Slot.MaxBoxes)
    {
        var p = new List<string>();
        var need = (layoutCount + Slot.PerBox - 1) / Slot.PerBox;
        var start = o.LivingDexStartBox;
        var end = start + need - 1;
        var what = (o.ShinyDex && o.ShinyAfterRegular ? $"regular + shiny dex: {need} boxes" : $"{need} boxes")
                   + (o.OriginMarkFilter.Length > 0 ? $" for the Pokémon that can have the {OriginMarks.Label(o.OriginMarkFilter)} origin mark" : "");
        if (need + 1 > total)
        {
            p.Add($"This style needs {need} boxes ({what}) plus at least one reject box, but HOME has {total}. Pick a smaller style or turn off the shiny dex.");
            return p;
        }
        if (start <= 1)
            p.Add($"Box 1 is kept free (HOME puts gifts and transfers there): change Living Dex Starting Box to 2 - the living dex then uses boxes 2-{need + 1}.");
        if (Math.Min(o.RejectBoxStart, o.RejectBoxEnd) <= 1)
            p.Add("Box 1 is kept free (HOME puts gifts and transfers there): change Reject Box Range Start to 2 or higher.");
        if (o.RejectBoxStart > o.RejectBoxEnd)
            p.Add($"Reject Box Range Start ({o.RejectBoxStart}) is after its End ({o.RejectBoxEnd}): change them to Start {o.RejectBoxEnd}, End {o.RejectBoxStart}.");
        if (end > total)
        {
            var latest = total - need + 1;
            p.Add($"The living dex needs {what}: starting in box {start} it would end in box {end}, past box {total}. " +
                  $"Change Living Dex Starting Box to {latest} or lower (and keep the reject boxes before it, e.g. Reject Box Range 1-{latest - 1}).");
            return p;
        }
        var rs = Math.Min(o.RejectBoxStart, o.RejectBoxEnd);
        var re = Math.Max(o.RejectBoxStart, o.RejectBoxEnd);
        if (start <= re && rs <= end)
        {
            string fix;
            if (end + 1 <= total)
                fix = $"change Reject Box Range Start to {end + 1}" + (re < end + 1 ? $" and Reject Box Range End to {total}" : "");
            else
                fix = $"change Reject Box Range to 1-{start - 1}";
            p.Add($"The living dex needs {what}: boxes {start}-{end}, but the reject boxes are {rs}-{re} - they overlap. To make it work, {fix}.");
        }
        return p;
    }

    /// <summary>The Box Sorter's range: must end by HOME's last box.</summary>
    public static List<string> SortRangeProblems(HomeSorterOptions o, int total = Slot.MaxBoxes)
    {
        var end = o.SortStartBox + o.BoxCount - 1;
        var p = new List<string>();
        if (o.SortStartBox <= 1)
            p.Add("Box 1 is kept free (HOME puts gifts and transfers there): change First Box to Sort to 2.");
        if (end > total)
            p.Add($"Boxes {o.SortStartBox}-{end} go past box {total}: change Number of Boxes to Sort to {total - o.SortStartBox + 1} or lower.");
        return p;
    }

    /// <summary>First box (1-based) of the shiny dex in a combined layout, or null.</summary>
    public static int? ShinyStartBox(List<DexEntry> template, HomeSorterOptions o)
    {
        if (!o.ShinyDex || !o.ShinyAfterRegular) return null;
        var one = o.OriginMarkFilter.Length > 0 ? template.Count(e => !e.IsEmpty && OriginMarks.CanHave(o.OriginMarkFilter, e)) : template.Count;
        return o.LivingDexStartBox + (one + Slot.PerBox - 1) / Slot.PerBox;
    }

    /// <summary>Loads and checks a template (same rules as Pokémon Automation's validate_living_dex_template).</summary>
    public static List<DexEntry> Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{Path.GetFileName(path)}: expected a JSON array of living dex entries.");
        var list = new List<DexEntry>();
        var n = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            n++;
            string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var nat = e.TryGetProperty("natID", out var id) && id.TryGetInt32(out var i) ? i
                : throw new InvalidDataException($"{Path.GetFileName(path)} entry {n}: 'natID' is missing.");
            var gd = e.TryGetProperty("has_gender_difference", out var g) && g.ValueKind == JsonValueKind.True;
            var second = S("secondaryType");
            list.Add(new DexEntry(S("species_slug"), S("form_slug"), nat, S("primaryType"), second == "none" ? "" : second, S("slug"), gd));
        }
        if (list.Count == 0) throw new InvalidDataException($"{Path.GetFileName(path)}: the template has no entries.");
        if (list.Count > Slot.MaxBoxes * Slot.PerBox) throw new InvalidDataException($"{Path.GetFileName(path)}: more entries than HOME has slots.");
        return list;
    }
}

/// <summary>
/// A run that was stopped part-way, so the next Start can continue it (HOME Sorter\session.json). The Pokémon themselves
/// are in the remembered boxes (updated after every box read and every swap); this says how far the run got and with
/// which settings.
/// </summary>
public sealed class HomeSession
{
    public string Key { get; set; } = "";
    public DateTime Started { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    /// <summary>Boxes fully read in this run (box 1 is always read again).</summary>
    public List<int> ReadBoxes { get; set; } = new();
    public bool ScanDone { get; set; }
    /// <summary>The scan came from a dry run (nothing was moved): the next real run can use it instead of reading again.</summary>
    public bool FromDryRun { get; set; }
    public int SwapsDone { get; set; }
    public bool Finished { get; set; }

    public static string FilePath => Path.Combine(HomeSorterRunner.OutputDir, "session.json");

    /// <summary>The settings that decide what a run does: a saved run only continues with the same ones.</summary>
    public static string KeyFor(HomeSorterOptions o) => JsonSerializer.Serialize(new object[]
    {
        o.Mode, o.SortStartBox, o.BoxCount, o.Rules, o.LivingDexStartBox, o.RejectBoxStart, o.RejectBoxEnd, o.LivingDexStyle, o.CustomDexFile,
        o.ShinyDex, o.ShinyAfterRegular, o.OriginMarkFilter, o.OtNameLanguage, o.OtName, o.MarkRules, o.MarksMustAllMatch, o.Marked, o.ExcludeGo,
    }, HomeCatalog.Json);

    public static HomeSession? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<HomeSession>(File.ReadAllText(FilePath), HomeCatalog.Json) : null; }
        catch { return null; }
    }

    /// <summary>A stopped run that can be continued with these settings, or null.</summary>
    public static HomeSession? Resumable(HomeSorterOptions o) => Load() is { Finished: false } s && s.Key == KeyFor(o) && (s.ReadBoxes.Count > 0 || s.SwapsDone > 0) ? s : null;

    public void Save()
    {
        Updated = DateTime.Now;
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, HomeCatalog.Json));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    public static void Forget() { try { File.Delete(FilePath); } catch { } }
}

/// <summary>Box contents remembered between runs and written as the output files (null = empty slot).</summary>
public sealed class HomeCatalog
{
    public DateTime Read { get; set; } = DateTime.Now;
    public Dictionary<int, HomeMon?> Slots { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
