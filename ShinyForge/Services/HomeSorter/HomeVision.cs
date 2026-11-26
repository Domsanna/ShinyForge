using System.Text.RegularExpressions;
using ShinyForge.Core;

namespace ShinyForge.Services.HomeSorter;

/// <summary>
/// Reads Pokémon HOME's screens from capture-card pictures. Screen positions are fractions of the picture (any size), and
/// most of them are the ones Pokémon Automation measured for its HOME programs (github.com/PokemonAutomation/Arduino-Source,
/// MIT). Text is read with the OCR set in <see cref="Ocr"/> (Windows' own OCR on Windows); balls, types and origin marks
/// are matched against the pictures in the user's Pokémon Automation Resources folder.
/// ShinyForge additions: the six markings, the cursor position (so a dropped button press is noticed and corrected),
/// the box counter ("12/300") and the info bar under the box (to check every move).
/// </summary>
public static class HomeVision
{
    /// <summary>OCR: (picture part, BCP-47 language or "") → text, or null when no OCR is available.</summary>
    public static Func<RgbImage, string, string?>? Ocr { get; set; }
    /// <summary>OCR, every reading it can make (different sizes) - for text that must match a pattern, like "12/300".</summary>
    public static Func<RgbImage, string, IEnumerable<string>>? OcrAll { get; set; }
    /// <summary>PNG → RGBA pixels (alpha kept), for Pokémon Automation's sprite sheets.</summary>
    public static Func<byte[], (int W, int H, byte[] Rgba)?>? DecodeRgba { get; set; }

    public readonly record struct Box(double X, double Y, double W, double H);

    // ---- summary screen (Pokémon Automation's positions unless marked)
    private static readonly Box DexBox = new(0.42, 0.24, 0.08, 0.05);          // "No. 0854" (PA reads the digits only)
    private static readonly Box NameBox = new(0.515, 0.24, 0.105, 0.05);       // ShinyForge: the species name next to it
    private static readonly Box LevelBox = new(0.506141, 0.090805, 0.100194, 0.057471);
    private static readonly Box OtNameBox = new(0.492, 0.719, 0.165, 0.049);
    private static readonly Box OtIdBox = new(0.782, 0.719, 0.193, 0.046);
    private static readonly Box NatureBox = new(0.157, 0.783, 0.212, 0.042);
    private static readonly Box AbilityBox = new(0.158, 0.838, 0.213, 0.042);
    private static readonly Box LanguageBox = new(0.0265, 0.176, 0.056, 0.035);
    private static readonly Box GenderBox = new(0.417, 0.097, 0.031, 0.046);
    private static readonly Box ShinyBox = new(0.702, 0.09, 0.04, 0.06);
    private static readonly Box AlphaBox = new(0.787, 0.095, 0.024, 0.046);
    private static readonly Box GmaxBox = new(0.463, 0.09, 0.04, 0.06);
    private static readonly Box BallBox = new(0.228, 0.095, 0.030, 0.049);
    /// <summary>ShinyForge: centres of the first and second type circles next to the species name.</summary>
    private static readonly (double X, double Y) TypeSpot1 = (0.6339, 0.2685), TypeSpot2 = (0.6667, 0.2685);
    private static readonly Box OriginBox = new(0.617, 0.084, 0.044, 0.069);
    /// <summary>ShinyForge: the "Seems to have been first met in ..." line.</summary>
    private static readonly Box MetBox = new(0.40, 0.808, 0.58, 0.055);
    /// <summary>ShinyForge: centres of the six markings on the summary screen (circle → diamond).</summary>
    private static readonly double[] MarkX = { 0.7255, 0.7661, 0.8094, 0.8500, 0.8932, 0.9339 };
    private const double MarkY = 0.6731, MarkR = 0.0075;

    /// <summary>ShinyForge: the four moves (one OCR read, one line each).</summary>
    private static readonly Box MovesBox = new(0.475, 0.32, 0.19, 0.33);
    /// <summary>ShinyForge: the six stat numbers around the hexagon, in the order HP, Atk, Def, SpA, SpD, Spe.</summary>
    private static readonly Box[] StatBoxes =
    {
        new(0.178, 0.268, 0.046, 0.04), new(0.305, 0.38, 0.046, 0.04), new(0.305, 0.613, 0.046, 0.04),
        new(0.051, 0.38, 0.046, 0.04), new(0.051, 0.613, 0.046, 0.04), new(0.178, 0.64, 0.046, 0.04),
    };

    /// <summary>The six stats, read in one go. Windows OCR skips lone single digits (level 1 stats like "7"), so the six
    /// numbers are put together into one picture: one per line first, then side by side on one line, then per stat.</summary>
    private static int[] ReadStats(RgbImage img)
    {
        var r = new int[6];
        if (OcrAll == null) return r;
        var crops = StatBoxes.Select(b => Crop(img, b)).ToList();
        var bg = (crops[0].Pixels[0], crops[0].Pixels[1], crops[0].Pixels[2]);
        RgbImage Compose(bool sideBySide, int gap)
        {
            int cw = crops.Max(c => c.Width), ch = crops.Max(c => c.Height);
            int w = sideBySide ? 6 * (cw + gap) + gap : cw + 2 * gap, h = sideBySide ? ch + 2 * gap : 6 * (ch + gap) + gap;
            var px = new byte[w * h * 3];
            for (var i = 0; i < w * h; i++) { px[i * 3] = bg.Item1; px[i * 3 + 1] = bg.Item2; px[i * 3 + 2] = bg.Item3; }
            for (var k = 0; k < 6; k++)
            {
                var c = crops[k];
                int x0 = sideBySide ? gap + k * (cw + gap) : gap, y0 = sideBySide ? gap : gap + k * (ch + gap);
                for (var y = 0; y < c.Height; y++) Buffer.BlockCopy(c.Pixels, y * c.Width * 3, px, ((y0 + y) * w + x0) * 3, c.Width * 3);
            }
            return new RgbImage(w, h, px);
        }
        static string Digits(string t) => t.Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1').Replace('|', '1');
        bool Take(List<string> parts)
        {
            if (parts.Count != 6) return false;
            for (var i = 0; i < 6; i++) r[i] = int.TryParse(parts[i], out var v) && v is > 0 and < 1000 ? v : 0;
            if (!r.All(v => v > 0)) return false;
            for (var k = 0; k < 6; k++) StatDigits.Learn(crops[k], r[k].ToString());
            return true;
        }
        foreach (var text in OcrAll(Compose(false, 14), "en"))
            if (Take(text.Split('\n').Select(l => Regex.Replace(Digits(l), @"\D", "")).Where(l => l.Length > 0).ToList())) return r;
        foreach (var gap in new[] { 8, 16, 26, 40 })
            foreach (var text in OcrAll(Compose(true, gap), "en"))
                if (Take(Regex.Matches(Digits(text), @"\d+").Select(m => m.Value).ToList())) return r;
        // single digits (level 1 stats) defeat Windows OCR: read each number from learned digit shapes
        r = new int[6];
        for (var k = 0; k < 6; k++) r[k] = StatDigits.Read(crops[k]) ?? 0;
        return r;
    }

    /// <summary>Teaches the stat digit shapes from a summary whose six stats are known (SelfTest / OCR successes).</summary>
    public static void LearnStats(RgbImage img, int[] stats)
    {
        for (var k = 0; k < 6; k++) StatDigits.Learn(Crop(img, StatBoxes[k]), stats[k].ToString());
    }

    /// <summary>The stat numbers' digit shapes (dark grey on the light summary): seeded from real summaries, then learned
    /// from every summary whose stats OCR read in full, saved next to the run logs.</summary>
    public static readonly DigitBook StatDigits = new("stat_digits.json", StatDigitSeed.Shapes);
    /// <summary>The level's digit shapes ("Lv. 5"): learned from every level OCR reads, used when OCR drops the digits.</summary>
    public static readonly DigitBook LevelDigitShapes = new("level_digits.json", LevelDigitSeed.Shapes);

    /// <summary>Digit shapes of one font: Windows OCR skips lone or thin digits ("5", "11"), these don't.</summary>
    public sealed class DigitBook
    {
        private Dictionary<char, List<string>>? _t;
        private readonly object _gate = new();
        private readonly string _file;
        private readonly (char Digit, string Bits)[] _seed;
        public DigitBook(string file, (char, string)[] seed) { _file = file; _seed = seed; }
        private string FilePath => Path.Combine(HomeSorterRunner.OutputDir, _file);
        internal string Dump() { lock (_gate) return System.Text.Json.JsonSerializer.Serialize(All().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)); }
        public int Count { get { lock (_gate) return All().Sum(kv => kv.Value.Count); } }

        private Dictionary<char, List<string>> All()
        {
            lock (_gate)
            {
                if (_t != null) return _t;
                _t = new();
                foreach (var (d, bits) in _seed)
                {
                    if (!_t.TryGetValue(d, out var l)) _t[d] = l = new();
                    l.Add(bits);
                }
                try
                {
                    if (File.Exists(FilePath))
                        foreach (var kv in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath))!)
                        {
                            if (!_t.TryGetValue(kv.Key[0], out var l)) _t[kv.Key[0]] = l = new();
                            foreach (var x in kv.Value) if (!l.Contains(x)) l.Add(x);
                        }
                }
                catch { }
                return _t;
            }
        }

        /// <summary>Glyphs left to right: runs of columns with dark pixels, with their top and bottom.</summary>
        public static List<(int X0, int X1, int Y0, int Y1)> Cols(RgbImage c)
        {
            var list = new List<(int, int, int, int)>();
            int start = -1, top = c.Height, bottom = -1;
            for (var x = 0; x <= c.Width; x++)
            {
                var on = false;
                if (x < c.Width)
                    for (var y = 0; y < c.Height; y++)
                        if (Dark(c, x, y)) { on = true; if (start >= 0 || true) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); } }
                if (on && start < 0) start = x;
                if (!on && start >= 0)
                {
                    if (x - start >= 2) list.Add((start, x, top, bottom));
                    start = -1; top = c.Height; bottom = -1;
                }
            }
            return list;
        }

        /// <summary>A glyph as a 10x16 grid, plus its width relative to its height (a "1" is narrow).</summary>
        private static string Bits(RgbImage c, int x0, int x1)
        {
            int y0 = c.Height, y1 = -1;
            for (var y = 0; y < c.Height; y++) for (var x = x0; x < x1; x++) if (Dark(c, x, y)) { y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
            if (y1 < y0) return "";
            var b = new char[10 * 16];
            for (var gy = 0; gy < 16; gy++)
                for (var gx = 0; gx < 10; gx++)
                    b[gy * 10 + gx] = Dark(c, x0 + (gx * 2 + 1) * (x1 - x0) / 20, y0 + (gy * 2 + 1) * (y1 - y0 + 1) / 32) ? '1' : '0';
            var aspect = Math.Min(9, (x1 - x0) * 10 / Math.Max(1, y1 - y0 + 1));
            return aspect + new string(b);
        }

        /// <summary>Learns <paramref name="digits"/> from the glyphs (all of them, or the given ones).</summary>
        public void Learn(RgbImage crop, string digits, List<(int X0, int X1, int Y0, int Y1)>? glyphs = null)
        {
            var cols = glyphs ?? Cols(crop);
            if (cols.Count != digits.Length) return;
            var changed = false;
            lock (_gate)
            {
                var all = All();
                for (var i = 0; i < digits.Length; i++)
                {
                    var bits = Bits(crop, cols[i].X0, cols[i].X1);
                    if (bits.Length == 0) continue;
                    if (!all.TryGetValue(digits[i], out var list)) all[digits[i]] = list = new();
                    if (list.Count < 8 && !list.Contains(bits)) { list.Add(bits); changed = true; }
                }
                if (changed) try { File.WriteAllText(FilePath, Dump()); } catch { }
            }
        }

        public int? Read(RgbImage crop, List<(int X0, int X1, int Y0, int Y1)>? glyphs = null)
        {
            var cols = glyphs ?? Cols(crop);
            if (cols.Count is 0 or > 3) return null;
            var all = All();
            if (all.Count == 0) return null;
            var text = "";
            foreach (var g in cols)
            {
                var bits = Bits(crop, g.X0, g.X1);
                if (bits.Length == 0) return null;
                var best = all.SelectMany(kv => kv.Value.Select(t => (Digit: kv.Key,
                        Diff: t.Skip(1).Zip(bits.Skip(1)).Count(z => z.First != z.Second) + 6 * Math.Abs(t[0] - bits[0]))))
                    .OrderBy(x => x.Diff).FirstOrDefault();
                if (best.Digit == default || best.Diff > 30) return null;
                text += best.Digit;
            }
            return int.TryParse(text, out var v) && v > 0 ? v : null;
        }
    }

    /// <summary>The digit glyphs of "Lv. 22": the full-height glyphs after the "." (L and v come first, the dot is short).</summary>
    public static List<(int X0, int X1, int Y0, int Y1)> LevelGlyphs(RgbImage crop)
    {
        var g = DigitBook.Cols(crop);
        if (g.Count < 2) return new();
        var h = g.Max(x => x.Y1 - x.Y0 + 1);
        // "Lv." comes first - one glyph when the letters touch, else L, v and a short dot sitting low
        var dot = g.FindIndex(x => x.Y1 - x.Y0 + 1 < h * 0.35 && x.Y0 > g.Min(y => y.Y0) + h / 2);
        var skip = dot >= 0 ? dot + 1 : 1;
        var digits = g.Skip(skip).Where(x => x.Y1 - x.Y0 + 1 >= h * 0.7).ToList();
        return digits.Count is >= 1 and <= 3 ? digits : new();
    }

    // ---- box view
    private static readonly Box CounterBox = new(0.07, 0.715, 0.08, 0.05);      // ShinyForge: "12/300" under the box (right-aligned)
    private static readonly Box BarNameBox = new(0.05, 0.79, 0.125, 0.05);       // ShinyForge: species in the info bar
    private static readonly Box BottomLeftLabel = new(0.12, 0.955, 0.09, 0.045); // "Quit" (box view) / "Back" (summary)
    /// <summary>ShinyForge: the six markings in the info bar under the box.</summary>
    private static readonly double[] BarMarkX = { 0.8068, 0.8380, 0.8708, 0.9026, 0.9359, 0.9667 };
    private const double BarMarkY = 0.8167;

    // ================================================================ screens
    /// <summary>The summary screen: the teal bar with "No. 0123" and the species name.</summary>
    public static bool IsSummary(RgbImage img)
    {
        // the teal "No. 0111  Name" bar of the summary (the box view has light green there) - a colour check, no OCR
        var b = new Box(0.405, 0.245, 0.035, 0.035);
        var n = Area(img, b);
        return Count(img, b, (r, g, bl) => r < 90 && g > 160 && bl > 140 && g > r + 80) > n * 0.6;
    }

    /// <summary>
    /// The box view: the box counter under the grid ("12/300") is there - recognised by its shape (1-3 characters, a
    /// slanted "/", 3 characters), so a misread digit can't make ShinyForge think the box view is gone.
    /// </summary>
    public static bool IsBoxView(RgbImage img)
    {
        var c = Crop(img, CounterBox);
        var g = Glyphs(c);
        if (g.Count > 0 && g[0].X0 == 0) g.RemoveAt(0);
        var lead = g.Count - 4;
        return lead is >= 1 and <= 3 && IsSlash(c, g[lead].X0, g[lead].X1) && !IsSummary(img);
    }

    /// <summary>The box HOME shows and how many boxes there are ("12/300" → (11, 300)), or null.</summary>
    public static (int Box, int Total)? ReadBoxCounter(RgbImage img)
    {
        // Windows' OCR keeps dropping or misreading the first digit ("1/300" → "/300", a cut-off "2" → "1"), so:
        // the characters are also counted on screen (everything before the last four - "/" and the 3-digit total - is the
        // box number, a narrow one is a "1"), and a number only counts when at least two readings agree.
        var crop = Crop(img, CounterBox);
        var glyphs = Glyphs(crop);
        if (glyphs.Count > 0 && glyphs[0].X0 == 0) glyphs.RemoveAt(0); // something cut off at the edge: not a character
        var lead = glyphs.Count - 4;
        if (lead >= 1 && !IsSlash(crop, glyphs[lead].X0, glyphs[lead].X1)) lead = -1; // something covers the counter
        if (lead is < 1 or > 3) return null;
        var votes = new Dictionary<(int, int), int>();
        int? onesOnly = Enumerable.Range(0, lead).All(i => glyphs[i].Narrow) ? int.Parse(new string('1', lead)) : null;
        var texts = OcrAll != null ? OcrAll(crop, "en") : Read(img, CounterBox) is { } one ? new[] { one } : Array.Empty<string>();
        foreach (var raw in texts)
        {
            var t = Regex.Replace(raw, @"[|lIi!\]\[]", "1").Replace('O', '0').Replace('o', '0').Replace(" ", "");
            var m = Regex.Match(t, @"(\d{0,3})/(\d{3})$");
            if (!m.Success) continue;
            var total = int.Parse(m.Groups[2].Value);
            var digits = m.Groups[1].Value;
            if (digits.Length < lead)
            {
                // put dropped 1s back where the narrow characters are
                var missing = lead - digits.Length;
                if (glyphs.Take(lead).Count(g => g.Narrow) < missing) continue;
                var rebuilt = new System.Text.StringBuilder();
                var k = 0;
                for (var i = 0; i < lead; i++)
                {
                    if (glyphs[i].Narrow && missing > 0 && (k >= digits.Length || lead - i > digits.Length - k)) { rebuilt.Append('1'); missing--; }
                    else if (k < digits.Length) rebuilt.Append(digits[k++]);
                }
                digits = rebuilt.ToString();
            }
            if (digits.Length != lead || !int.TryParse(digits, out var box)) continue;
            // a "1" must be one of the narrow characters and a narrow character must be a "1"
            if (Enumerable.Range(0, lead).Any(i => glyphs[i].Narrow != (digits[i] == '1'))) continue;
            if (box < 1 || box > total || total is < 100 or > 999) continue;
            // the reading matches the characters counted on screen (every "1" narrow, nothing else narrow): that's enough
            return Learned(crop, glyphs, lead, (box - 1, total));
        }
        // nothing readable, but every character before the "/" is a narrow "1": box 1, 11 or 111 (of the usual 300)
        if (onesOnly is { } ones) return (ones - 1, HomeSortPlanner.Boxes);
        // the box number's characters spaced apart and read again ("117" with two thin 1s defeats Windows' OCR in one piece)
        if (SpacedDigits(crop, glyphs, lead) is { } spaced) return Learned(crop, glyphs, lead, spaced);
        // the digit shapes ShinyForge has learned from this screen (every sure reading teaches them)
        if (DigitTemplates.Read(crop, glyphs.Take(lead).ToList()) is { } learned && learned is >= 1 and <= 999) return (learned - 1, HomeSortPlanner.Boxes);
        return null;
    }

    internal static IEnumerable<string> DebugGlyphs(RgbImage img)
    {
        var c = Crop(img, CounterBox);
        var ocr = OcrAll != null ? string.Join(" | ", OcrAll(c, "en").Select(t => "'" + t + "'")) : "";
        return Glyphs(c).Select(g => $"[{g.X0}-{g.X1}{(g.Narrow ? " narrow" : "")}{(IsSlash(c, g.X0, g.X1) ? " slash" : "")}]").Append("ocr: " + ocr);
    }

    /// <summary>A sure reading teaches the digit shapes (the box number and the "300"), then is passed on.</summary>
    private static (int Box, int Total) Learned(RgbImage crop, List<(int X0, int X1, bool Narrow)> glyphs, int lead, (int Box, int Total) r)
    {
        try
        {
            var digits = (r.Box + 1).ToString() + r.Total;
            var used = glyphs.Take(lead).Concat(glyphs.Skip(lead + 1).Take(3)).ToList();
            if (digits.Length == used.Count) DigitTemplates.Learn(crop, used, digits);
        }
        catch { }
        return r;
    }

    /// <summary>OCR of the box number's characters copied apart with gaps (and nothing else) - checked against the narrow 1s.</summary>
    private static (int Box, int Total)? SpacedDigits(RgbImage crop, List<(int X0, int X1, bool Narrow)> glyphs, int lead)
    {
        if (OcrAll == null) return null;
        const int gap = 10;
        var w = glyphs.Take(lead).Sum(g => g.X1 - g.X0) + gap * (lead + 1);
        var h = crop.Height + 20;
        var px = Enumerable.Repeat((byte)255, w * h * 3).ToArray();
        var x = gap;
        foreach (var (x0, x1, _) in glyphs.Take(lead))
        {
            for (var y = 0; y < crop.Height; y++)
                Buffer.BlockCopy(crop.Pixels, (y * crop.Width + x0) * 3, px, ((y + 10) * w + x) * 3, (x1 - x0) * 3);
            x += x1 - x0 + gap;
        }
        foreach (var raw in OcrAll(new RgbImage(w, h, px), "en"))
        {
            var t = Regex.Replace(raw, @"[|lIi!\]\[]", "1").Replace('O', '0').Replace('o', '0').Replace(" ", "");
            if (!Regex.IsMatch(t, @"^\d+$") || t.Length != lead) continue;
            if (Enumerable.Range(0, lead).Any(i => glyphs[i].Narrow != (t[i] == '1'))) continue;
            var box = int.Parse(t);
            if (box >= 1 && box <= HomeSortPlanner.Boxes) return (box - 1, HomeSortPlanner.Boxes);
        }
        return null;
    }

    /// <summary>
    /// Digit shapes learned from the user's own screen: each sure box counter reading stores its digits as 10x16 on/off
    /// pictures (Documents\ShinyForge\HOME Sorter\digits.json). When OCR can't read a box number, each digit is matched to
    /// the closest learned shape.
    /// </summary>
    public static class DigitTemplates
    {
        private static Dictionary<char, List<string>>? _t;
        private static readonly object Gate = new();
        private static string FilePath => Path.Combine(HomeSorterRunner.OutputDir, "digits.json");

        private static Dictionary<char, List<string>> All()
        {
            lock (Gate)
            {
                if (_t != null) return _t;
                try
                {
                    _t = File.Exists(FilePath)
                        ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath))!.ToDictionary(kv => kv.Key[0], kv => kv.Value)
                        : new();
                }
                catch { _t = new(); }
                return _t;
            }
        }

        private static string Bits(RgbImage c, int x0, int x1)
        {
            int y0 = c.Height, y1 = -1;
            for (var y = 0; y < c.Height; y++) for (var x = x0; x < x1; x++) if (Dark(c, x, y)) { y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
            if (y1 < y0) return "";
            var b = new char[10 * 16];
            for (var gy = 0; gy < 16; gy++)
                for (var gx = 0; gx < 10; gx++)
                    b[gy * 10 + gx] = Dark(c, x0 + (gx * 2 + 1) * (x1 - x0) / 20, y0 + (gy * 2 + 1) * (y1 - y0 + 1) / 32) ? '1' : '0';
            return new string(b);
        }

        public static void Learn(RgbImage crop, List<(int X0, int X1, bool Narrow)> glyphs, string digits)
        {
            var all = All();
            var changed = false;
            lock (Gate)
                for (var i = 0; i < digits.Length; i++)
                {
                    var bits = Bits(crop, glyphs[i].X0, glyphs[i].X1);
                    if (bits.Length == 0) continue;
                    if (!all.TryGetValue(digits[i], out var list)) all[digits[i]] = list = new();
                    if (list.Count < 6 && !list.Contains(bits)) { list.Add(bits); changed = true; }
                }
            if (!changed) return;
            try { File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(all.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value))); } catch { }
        }

        public static int? Read(RgbImage crop, List<(int X0, int X1, bool Narrow)> lead)
        {
            var all = All();
            if (all.Count == 0) return null;
            var text = new char[lead.Count];
            for (var i = 0; i < lead.Count; i++)
            {
                if (lead[i].Narrow) { text[i] = '1'; continue; }
                var bits = Bits(crop, lead[i].X0, lead[i].X1);
                if (bits.Length == 0) return null;
                var best = all.Where(kv => kv.Key != '1')
                    .SelectMany(kv => kv.Value.Select(t => (Digit: kv.Key, Diff: t.Zip(bits).Count(z => z.First != z.Second))))
                    .OrderBy(x => x.Diff).FirstOrDefault();
                if (best.Digit == default || best.Diff > 22) return null; // not close enough to any learned digit
                text[i] = best.Digit;
            }
            return int.Parse(new string(text));
        }
    }

    /// <summary>A "/": its dark pixels sit further right at the top than at the bottom.</summary>
    private static bool IsSlash(RgbImage c, int x0, int x1)
    {
        double top = 0, bottom = 0; int nt = 0, nb = 0;
        var rows = Enumerable.Range(0, c.Height).Where(y => Enumerable.Range(x0, x1 - x0).Any(x => Dark(c, x, y))).ToList();
        if (rows.Count < 6) return false;
        var third = rows.Count / 3;
        foreach (var y in rows.Take(third)) for (var x = x0; x < x1; x++) if (Dark(c, x, y)) { top += x; nt++; }
        foreach (var y in rows.Skip(rows.Count - third)) for (var x = x0; x < x1; x++) if (Dark(c, x, y)) { bottom += x; nb++; }
        return nt > 0 && nb > 0 && top / nt - bottom / nb > (x1 - x0) * 0.3;
    }

    private static bool Dark(RgbImage c, int x, int y) { var o = (y * c.Width + x) * 3; return c.Pixels[o] + c.Pixels[o + 1] + c.Pixels[o + 2] < 3 * 150; }

    /// <summary>Dark characters on the light counter, left to right (columns of dark pixels; a "1" is narrow).</summary>
    private static List<(int X0, int X1, bool Narrow)> Glyphs(RgbImage c)
    {
        var dark = new int[c.Width];
        int top = c.Height, bottom = 0;
        for (var x = 0; x < c.Width; x++)
            for (var y = 0; y < c.Height; y++)
                if (Dark(c, x, y)) { dark[x]++; top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        var height = Math.Max(1, bottom - top + 1);
        var list = new List<(int, int, bool)>();
        var start = -1;
        for (var x = 0; x <= c.Width; x++)
        {
            var on = x < c.Width && dark[x] > 0;
            if (on && start < 0) start = x;
            if (!on && start >= 0)
            {
                if (x - start >= 2) list.Add((start, x, (x - start) < height * 0.42));
                start = -1;
            }
        }
        return list;
    }

    // ---- box names and the Switch keyboard (ShinyForge)
    private static readonly Box KeyboardTextBox = new(0.19, 0.245, 0.62, 0.085);   // the name being typed
    private static readonly Box KeyboardCountBox = new(0.755, 0.325, 0.055, 0.042); // "6/16"

    /// <summary>The box's name in the pill above the grid ("Home 1"), or "".</summary>
    public static string ReadBoxName(RgbImage img) => Clean(Read(img, BoxNameBox) ?? "");

    /// <summary>
    /// The Switch keyboard is open for the box name: how many characters the name has ("6/16" → 6), or null when the
    /// keyboard isn't showing.
    /// </summary>
    public static int? KeyboardLength(RgbImage img)
    {
        var texts = OcrAll != null ? OcrAll(Crop(img, KeyboardCountBox), "en") : Read(img, KeyboardCountBox) is { } one ? new[] { one } : Array.Empty<string>();
        foreach (var t in texts)
            if (Regex.Match(t.Replace(" ", "").Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1').Replace('|', '1'), @"(\d{1,2})/16") is { Success: true } m)
                return int.Parse(m.Groups[1].Value);
        return null;
    }

    /// <summary>The name typed so far on the Switch keyboard.</summary>
    public static string KeyboardText(RgbImage img) => Regex.Replace(Read(img, KeyboardTextBox) ?? "", @"\s+", " ").Trim(' ', '|');

    /// <summary>The box counter's picture, to see that an L / R press changed the box even when its number can't be read.</summary>
    public static byte[] CounterPrint(RgbImage img) => Fingerprint(img, CounterBox, 40)
        .Concat(Fingerprint(img, BoxNameBox, 40)).ToArray();

    /// <summary>The box name in the pill above the grid.</summary>
    private static readonly Box BoxNameBox = new(0.13, 0.095, 0.25, 0.055);

    /// <summary>Did the counter / box name change? (one changed digit is enough: counts clearly different little cells)</summary>
    public static bool CounterChanged(byte[] a, byte[] b) => a.Length != b.Length || a.Zip(b).Count(z => Math.Abs(z.First - z.Second) > 40) >= 4;

    /// <summary>
    /// The console dimmed its screen after a while without input (HOME's light screens turn grey): text and colours can't
    /// be read until a button is pressed.
    /// </summary>
    public static bool IsDimmed(RgbImage img)
    {
        long sum = 0; var n = 0;
        for (var i = 0; i < img.Pixels.Length; i += 3 * 97) { sum += img.Pixels[i] + img.Pixels[i + 1] + img.Pixels[i + 2]; n++; }
        var avg = n == 0 ? 0 : sum / (3.0 * n);
        return avg is > 15 and < 170; // HOME itself averages ~220 (light green), dimmed about half
    }

    /// <summary>"Quit" or "Back" in the bottom-left corner (HOME in English), for logs.</summary>
    public static string BottomLeft(RgbImage img) => Read(img, BottomLeftLabel) ?? "";

    /// <summary>
    /// Which of the 30 slots hold a Pokémon: a slot is empty when its picture is flat (Pokémon Automation's test: the colour
    /// spread in the slot's middle is under 10). Read with the cursor on the first slot (its name tag is above the grid).
    /// </summary>
    public static bool[] OccupiedSlots(RgbImage img)
    {
        var r = new bool[Slot.PerBox];
        for (var row = 0; row < Slot.Rows; row++)
            for (var col = 0; col < Slot.Cols; col++)
                r[row * Slot.Cols + col] = StdDevSum(img, new Box(0.06 + 0.072 * col, 0.2 + 0.105 * row, 0.03, 0.057)) >= 10;
        return r;
    }

    /// <summary>
    /// ShinyForge: where the cursor is - the coloured arrow above the selected slot (red = normal mode, blue = relocate,
    /// green = multi-select). Each slot's arrow position is scored by how much of it is the arrow's colour; the best one wins
    /// if it clearly beats the rest.
    /// </summary>
    /// <summary>The white glove HOME shows instead of the arrow while a Pokémon is carried: lots of pure white with a
    /// black outline just above the slot's centre, in one slot only.</summary>
    public static (int Row, int Col)? FindGlove(RgbImage img)
    {
        var scores = new List<(int Row, int Col, int White, int Dark)>();
        for (var row = 0; row < Slot.Rows; row++)
            for (var col = 0; col < Slot.Cols; col++)
            {
                double cx = 0.0734 + 0.0724 * col, cy = 0.2315 + 0.1056 * row;
                int x0 = (int)((cx - 0.022) * img.Width), x1 = (int)((cx + 0.022) * img.Width);
                int y0 = (int)((cy - 0.10) * img.Height), y1 = (int)((cy - 0.02) * img.Height);
                int white = 0, dark = 0;
                for (var y = Math.Max(0, y0); y < Math.Min(img.Height, y1); y++)
                    for (var x = Math.Max(0, x0); x < Math.Min(img.Width, x1); x++)
                    {
                        var o = (y * img.Width + x) * 3;
                        int r = img.Pixels[o], g = img.Pixels[o + 1], b = img.Pixels[o + 2];
                        if (r >= 248 && g >= 248 && b >= 248) white++;
                        if (r < 60 && g < 60 && b < 60) dark++;
                    }
                scores.Add((row, col, white, dark));
            }
        // scale the thresholds with the frame size (measured at 1920x1080: about 1340 white / 650 black)
        var scale = img.Width * img.Height / (1920.0 * 1080.0);
        var best = scores.OrderByDescending(x => x.White).ToList();
        if (best[0].White < 450 * scale || best[0].Dark < 150 * scale || best[0].White < 3 * Math.Max(1, best[1].White)) return null;
        return (best[0].Row, best[0].Col);
    }

    public static (int Row, int Col, string Mode)? FindCursor(RgbImage img)
    {
        // the cursor's name tag has a long red underline nothing else on the box has (red Pokémon fooled the arrow test:
        // Scizor, Slugma, Magcargo): the arrow sits right under it
        if (FindByTag(img) is { } tagged) return tagged;
        // carrying a Pokémon (after the first Y) the cursor is a white glove with a black outline
        if (FindGlove(img) is { } glove) return (glove.Row, glove.Col, "glove");
        var scores = new List<(int Row, int Col, double Score, string Mode)>();
        for (var row = 0; row < Slot.Rows; row++)
            for (var col = 0; col < Slot.Cols; col++)
            {
                var b = new Box(0.075 + 0.072 * col - 0.006, 0.16 + 0.1055 * row - 0.008, 0.012, 0.012);
                var (red, blue, green) = ArrowColours(img, b);
                var best = Math.Max(red, Math.Max(blue, green));
                scores.Add((row, col, best, best == red ? "normal" : best == blue ? "relocate" : "multi"));
            }
        var top = scores.OrderByDescending(s => s.Score).ToList();
        if (top[0].Score < 0.45 || top[0].Score < top[1].Score * 1.6) return null;
        return (top[0].Row, top[0].Col, top[0].Mode);
    }

    /// <summary>The name tag's red underline (a long run of red in the box area) and the arrow under it.</summary>
    private static (int Row, int Col, string Mode)? FindByTag(RgbImage img)
    {
        int W = img.Width, H = img.Height;
        int x0 = (int)(0.035 * W), x1 = (int)(0.48 * W), y0 = (int)(0.15 * H), y1 = (int)(0.70 * H);
        bool Red(int x, int y) { var o = (y * W + x) * 3; return img.Pixels[o] > 180 && img.Pixels[o + 1] < 100 && img.Pixels[o + 2] < 90; }
        for (var y = y0; y < y1; y++)
        {
            int run = 0, best = 0;
            for (var x = x0; x < x1; x++) { run = Red(x, y) ? run + 1 : 0; best = Math.Max(best, run); }
            if (best < 0.1 * W) continue;
            // the underline is several pixels thick: the arrow starts where the long red run ends
            int LongestRun(int yy) { int r = 0, m = 0; for (var x = x0; x < x1; x++) { r = Red(x, yy) ? r + 1 : 0; m = Math.Max(m, r); } return m; }
            var bottom = y;
            while (bottom < y1 && LongestRun(bottom) >= 0.05 * W) bottom++;
            // the arrow: a red triangle pointing down - wide and solid right under the underline, narrow further down. A red
            // Pokémon under the tag (Slugma, Magcargo) is red too, but doesn't taper like that.
            int RunAt(int yy, int cx) { if (!Red(cx, yy)) return 0; int l = cx, r = cx; while (l > x0 && Red(l - 1, yy)) l--; while (r < x1 - 1 && Red(r + 1, yy)) r++; return r - l + 1; }
            int bestCol = -1, bestTop = 0;
            for (var c = 0; c < Slot.Cols; c++)
            {
                var cxp = (int)((0.075 + 0.072 * c) * W);
                var top = Enumerable.Range(bottom + 1, 4).Where(yy => yy < H).Select(yy => RunAt(yy, cxp)).DefaultIfEmpty(0).Max();
                var low = Enumerable.Range(bottom + (int)(0.016 * H), 4).Where(yy => yy < H).Select(yy => RunAt(yy, cxp)).DefaultIfEmpty(0).Min();
                if (top < 0.014 * W || top > 0.045 * W || low >= top * 0.7) continue; // not arrow-shaped
                if (top > bestTop) { bestTop = top; bestCol = c; }
            }
            if (bestCol < 0) continue;
            var cx = 0.075 + 0.072 * bestCol;
            var arrowY = (bottom + 0.012 * H) / H;
            var row = (int)Math.Round((arrowY - 0.16) / 0.1055);
            var col = (int)Math.Round((cx - 0.075) / 0.072);
            if (row is >= 0 and < Slot.Rows && col is >= 0 and < Slot.Cols) return (row, col, "normal");
        }
        return null;
    }

    /// <summary>The cursor is on the box name above the grid (the name pill turns orange).</summary>
    public static bool CursorOnBoxName(RgbImage img)
    {
        var b = new Box(0.17, 0.105, 0.18, 0.03);
        return Count(img, b, (r, g, bl) => r > 220 && g is > 130 and < 210 && bl < 80) > Area(img, b) * 0.5;
    }

    /// <summary>The species name on the summary screen (to check it's the Pokémon that was meant).</summary>
    public static string SummarySpecies(RgbImage img) => Clean(Read(img, NameBox) ?? "");

    private static (double Red, double Blue, double Green) ArrowColours(RgbImage img, Box b)
    {
        int red = 0, blue = 0, green = 0, n = 0;
        foreach (var (r, g, bl) in Pixels(img, b))
        {
            n++;
            if (r > 160 && g < 110 && bl < 110) red++;
            else if (bl > 150 && r < 90 && g < 190 && bl > g + 20) blue++;
            else if (g > 150 && r < 110 && bl < 140 && g > bl + 40) green++;
        }
        return n == 0 ? (0, 0, 0) : ((double)red / n, (double)blue / n, (double)green / n);
    }

    /// <summary>The species name in the info bar under the box (what the cursor is on), for checking moves.</summary>
    public static string BarSpecies(RgbImage img) => Clean(Read(img, BarNameBox) ?? "");

    /// <summary>The markings shown in the info bar (box view) - same reading as on the summary screen.</summary>
    public static MarkColor[] BarMarks(RgbImage img) => BarMarkX.Select(x => MarkAt(img, x, BarMarkY, 0.0075)).ToArray();

    // ================================================================ summary screen
    /// <summary>
    /// The national dex number on the summary ("No. 0111"). Windows' OCR mangles thin 1s ("No.Ol 11", "No. 01 7 1"), so:
    /// every reading is cleaned up (O→0, l/I/|/!→1) and must give 4 digits, and the number is checked against the species
    /// name next to it (PKHeX's species names): an exact name wins, otherwise a number needs the name or a second reading
    /// to agree. -1 = not readable.
    /// </summary>
    public static int ReadDex(RgbImage img, string homeLanguage = "en")
    {
        var votes = new Dictionary<int, int>();
        var texts = OcrAll != null ? OcrAll(Crop(img, DexBox), "en") : Read(img, DexBox) is { } one ? new[] { one } : Array.Empty<string>();
        foreach (var raw in texts)
        {
            var t = raw.Trim();
            var at = t.IndexOf('.');                       // after "No."
            if (at >= 0) t = t[(at + 1)..];
            else if (t.StartsWith("No", StringComparison.OrdinalIgnoreCase)) t = t[2..];
            t = Regex.Replace(t, @"[Oo]", "0");
            t = Regex.Replace(t, @"[lIi|!\]\[]", "1");
            t = Regex.Replace(t, @"\s+", "");
            var m = Regex.Match(t, @"^(\d{4})");
            if (!m.Success || int.Parse(m.Groups[1].Value) is not (> 0 and <= 1025) and var bad) continue;
            var d = int.Parse(m.Groups[1].Value);
            votes[d] = votes.GetValueOrDefault(d) + 1;
        }
        var (nameDex, exact) = SpeciesNames.Find(Clean(Read(img, NameBox, lang: homeLanguage) ?? ""), homeLanguage);
        if (nameDex > 0 && (exact || votes.ContainsKey(nameDex))) return nameDex;   // the name decides / confirms
        var best = votes.OrderByDescending(v => v.Value).FirstOrDefault();
        if (best.Value >= 2) return best.Key;                                        // two readings agree
        if (nameDex > 0 && votes.Count == 0) return nameDex;                         // digits unreadable, name close enough
        return votes.Count == 1 && nameDex <= 0 && best.Value == 1 && SpeciesNames.Count == 0 ? best.Key : -1;
    }

    /// <summary>Species names → national dex number, from PKHeX (per HOME language), with a fuzzy match for OCR slips.</summary>
    /// <summary>Natures, abilities and moves as the games spell them (PKHeX's lists): OCR slips like "H a Sty", "t Swim" or
    /// "• Fire Punch" become "Hasty", "Swift Swim", "Fire Punch", so the same Pokémon always gets the same details.</summary>
    public static class GameText
    {
        public enum Kind { Nature, Ability, Move }
        private static readonly Dictionary<(string, Kind), Dictionary<string, string>> Lists = new();
        private static string Key(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static Dictionary<string, string> For(string lang, Kind kind)
        {
            lock (Lists)
            {
                if (Lists.TryGetValue((lang, kind), out var d)) return d;
                d = new Dictionary<string, string>();
                try
                {
                    var code = lang switch { "zh-Hans" => "zh-Hans", "zh-Hant" => "zh-Hant", _ => lang.Split('-')[0] };
                    var s = PKHeX.Core.GameInfo.GetStrings(code);
                    var names = kind switch { Kind.Nature => s.natures, Kind.Ability => s.abilitylist, _ => s.movelist };
                    foreach (var n in names.Skip(kind == Kind.Nature ? 0 : 1)) if (Key(n).Length > 0) d.TryAdd(Key(n), n);
                }
                catch (Exception e) { AppPaths.Log($"HOME sorter: {kind} names for {lang}: {e.Message}"); }
                return Lists[(lang, kind)] = d;
            }
        }

        /// <summary>The real name for an OCR'd one: exact, else the one closest name (a few letters off), else the one name
        /// that ends with / contains what was read; the text as read when nothing fits for sure.</summary>
        public static string Canon(string text, Kind kind, string lang)
        {
            var key = Key(text);
            if (key.Length < 3) return text;
            var d = For(lang, kind);
            if (d.Count == 0) return text;
            if (d.TryGetValue(key, out var exact)) return exact;
            var limit = Math.Max(1, key.Length / 4);
            var near = d.Select(kv => (kv.Value, Dist: Math.Abs(kv.Key.Length - key.Length) > limit ? 99 : SpeciesNames.Distance(kv.Key, key)))
                .Where(x => x.Dist <= limit).OrderBy(x => x.Dist).ToList();
            if (near.Count > 0 && (near.Count == 1 || near[1].Dist > near[0].Dist)) return near[0].Value;
            if (key.Length >= 4)
            {
                var part = d.Where(kv => kv.Key.EndsWith(key) || kv.Key.StartsWith(key)).Select(kv => kv.Value).Distinct().ToList();
                if (part.Count == 1) return part[0];
            }
            return text.TrimStart('•', '·', '-', '*', ' ');
        }
    }

    public static class SpeciesNames
    {
        private static readonly Dictionary<string, Dictionary<string, int>> ByLang = new();
        public static int Count { get; private set; }

        private static string Key(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static Dictionary<string, int> For(string lang)
        {
            lock (ByLang)
            {
                if (ByLang.TryGetValue(lang, out var d)) return d;
                d = new Dictionary<string, int>();
                try
                {
                    var code = lang switch { "zh-Hans" => "zh-Hans", "zh-Hant" => "zh-Hant", _ => lang.Split('-')[0] };
                    var names = PKHeX.Core.GameInfo.GetStrings(code).specieslist;
                    for (var i = 1; i < names.Length && i <= 1025; i++) d.TryAdd(Key(names[i]), i);
                }
                catch (Exception e) { AppPaths.Log("HOME sorter: species names for " + lang + ": " + e.Message); }
                Count = Math.Max(Count, d.Count);
                return ByLang[lang] = d;
            }
        }

        /// <summary>(dex, exact) for an OCR'd name: exact match, else the only name at most 2 letters off; (-1, false) if none.</summary>
        public static (int Dex, bool Exact) Find(string name, string lang)
        {
            var key = Key(name);
            if (key.Length < 3) return (-1, false);
            var d = For(lang);
            if (d.TryGetValue(key, out var exact)) return (exact, true);
            var close = d.Where(kv => Math.Abs(kv.Key.Length - key.Length) <= 2 && Distance(kv.Key, key) <= 2).Select(kv => kv.Value).Distinct().ToList();
            return close.Count == 1 ? (close[0], false) : (-1, false);
        }

        internal static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
                for (var j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
    }

    /// <summary>Everything on the summary screen. Only the dex number is required; anything unreadable stays empty.</summary>
    public static HomeMon? ReadSummary(RgbImage img, string homeLanguage, string otLanguage)
    {
        var dex = ReadDex(img, homeLanguage);
        if (dex <= 0) return null;
        var mon = new HomeMon { Dex = dex };
        mon.Species = Clean(Read(img, NameBox, lang: homeLanguage) ?? "");
        mon.Level = ReadLevel(img);
        if (otLanguage.Length > 0) mon.OtName = Clean(Read(img, OtNameBox, lang: otLanguage) ?? "");
        if (Regex.Match((Read(img, OtIdBox, digits: true) ?? "").Replace(" ", ""), @"(\d{1,6})") is { Success: true } id) mon.OtId = int.Parse(id.Groups[1].Value);
        mon.Nature = GameText.Canon(Clean(Read(img, NatureBox, lang: homeLanguage) ?? ""), GameText.Kind.Nature, homeLanguage);
        mon.Stats = ReadStats(img);
        mon.Moves = (Read(img, MovesBox, lang: homeLanguage) ?? "").Split('\n').Select(Clean).Where(m => m.Length > 2).Take(4)
            .Select(m => GameText.Canon(m, GameText.Kind.Move, homeLanguage)).ToList();
        mon.Ability = GameText.Canon(Clean(Read(img, AbilityBox, lang: homeLanguage) ?? ""), GameText.Kind.Ability, homeLanguage);
        mon.Language = Clean(Read(img, LanguageBox) ?? "").ToUpperInvariant();
        mon.Gender = ReadGender(img);
        mon.Shiny = StdDevSum(img, ShinyBox) > 30;
        mon.Alpha = StdDevSum(img, AlphaBox) > 40;
        mon.Gigantamax = Count(img, GmaxBox, (r, g, b) => r >= 196 && g >= 32 && g <= 220 && b >= 88 && b <= 235 && r > g + 60) * 1080.0 * 1080 / (img.Height * img.Height) > 250;
        mon.Ball = References.Ball(img);
        (mon.PrimaryType, mon.SecondaryType) = References.Types(img);
        mon.OriginMark = References.Origin(img);
        // GO: its origin mark, else the "Seems to have been first met in Pokémon GO" line (HOME in English)
        mon.FromGo = mon.OriginMark == "go" || Regex.IsMatch(Read(img, MetBox, lang: homeLanguage) ?? "", @"Pok[eé]mon\s*G[O0]\b", RegexOptions.IgnoreCase);
        mon.Marks = MarkX.Select(x => MarkAt(img, x, MarkY, MarkR)).ToArray();
        return mon;
    }

    /// <summary>The part after "Lv." with letters OCR confuses with digits turned back into digits ("Lv. S" → 5).</summary>
    /// <summary>The level: OCR first (each good read teaches the digit shapes), the learned shapes when OCR drops the
    /// digits - Windows OCR skips a lone "5" or thin "1"s, which made level 5 and level 11 Pokémon "Lv. 0".</summary>
    public static int ReadLevel(RgbImage img)
    {
        var crop = Crop(img, LevelBox);
        var glyphs = LevelGlyphs(crop);
        foreach (var t in OcrAll != null ? OcrAll(crop, "en") : new[] { Read(img, LevelBox) ?? "" })
            if (Regex.Match(LevelDigits(t), @"(\d{1,3})\s*$") is { Success: true } lv
                && int.Parse(lv.Groups[1].Value) is var l and >= 1 and <= 100
                && (glyphs.Count == 0 || glyphs.Count == lv.Groups[1].Value.Length))
            {
                LevelDigitShapes.Learn(crop, l.ToString(), glyphs);
                return l;
            }
        return glyphs.Count > 0 && LevelDigitShapes.Read(crop, glyphs) is { } v and >= 1 and <= 100 ? v : 0;
    }

    private static string LevelDigits(string t)
    {
        var at = t.IndexOf('.');
        var tail = at >= 0 ? t[(at + 1)..] : Regex.Replace(t, @"^\s*L\s*[vV]", "");
        var map = new Dictionary<char, char> { ['O'] = '0', ['o'] = '0', ['D'] = '0', ['l'] = '1', ['I'] = '1', ['i'] = '1', ['|'] = '1', ['!'] = '1',
            ['S'] = '5', ['s'] = '5', ['B'] = '8', ['Z'] = '2', ['z'] = '2', ['G'] = '6', ['b'] = '6', ['q'] = '9', ['g'] = '9', ['A'] = '4', ['T'] = '7' };
        return new string(tail.Select(c => map.GetValueOrDefault(c, c)).ToArray()).Trim();
    }

    /// <summary>Female = enough red in the gender spot, male = enough blue (Pokémon Automation's 1% rule), else genderless.</summary>
    private static string ReadGender(RgbImage img)
    {
        var total = Area(img, GenderBox);
        var red = Count(img, GenderBox, (r, g, b) => r >= 150 && g <= 100 && b <= 100);
        var blue = Count(img, GenderBox, (r, g, b) => b >= 150 && r <= 100 && g <= 100);
        return red > total / 100 ? "female" : blue > total / 100 ? "male" : "genderless";
    }

    /// <summary>
    /// ShinyForge: one marking. Unset markings are white with a grey outline; set ones are blue or pink-red. The light green
    /// background is never "blue enough" or "red enough".
    /// </summary>
    private static MarkColor MarkAt(RgbImage img, double cx, double cy, double rad)
    {
        var b = new Box(cx - rad, cy - rad * 16 / 9.0, rad * 2, rad * 2 * 16 / 9.0);
        var n = Area(img, b);
        var blue = Count(img, b, (r, g, bl) => bl > 150 && bl > r + 60 && bl > g + 15);
        var red = Count(img, b, (r, g, bl) => r > 170 && r > g + 70 && r > bl + 20);
        if (blue > n * 0.12 && blue > red) return MarkColor.Blue;
        if (red > n * 0.12) return MarkColor.Red;
        return MarkColor.None;
    }

    // ================================================================ OCR + pixels
    private static string? Read(RgbImage img, Box b, bool digits = false, string lang = "en")
    {
        if (Ocr == null) return null;
        var t = Ocr(Crop(img, b), lang.Length == 0 ? "en" : lang);
        if (t == null) return null;
        t = t.Trim();
        if (digits) t = t.Replace('O', '0').Replace('o', '0').Replace('I', '1').Replace('l', '1');
        return t.Length == 0 ? null : t;
    }

    private static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim(' ', '.', ',', '\'', '"', '|');

    public static RgbImage Crop(RgbImage img, Box b)
    {
        var (x0, y0, w, h) = Px(img, b);
        var px = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            Buffer.BlockCopy(img.Pixels, ((y0 + y) * img.Width + x0) * 3, px, y * w * 3, w * 3);
        return new RgbImage(w, h, px);
    }

    private static (int X, int Y, int W, int H) Px(RgbImage img, Box b)
    {
        var x0 = Math.Clamp((int)(b.X * img.Width), 0, img.Width - 1);
        var y0 = Math.Clamp((int)(b.Y * img.Height), 0, img.Height - 1);
        var w = Math.Clamp((int)(b.W * img.Width), 1, img.Width - x0);
        var h = Math.Clamp((int)(b.H * img.Height), 1, img.Height - y0);
        return (x0, y0, w, h);
    }

    private static IEnumerable<(int R, int G, int B)> Pixels(RgbImage img, Box b)
    {
        var (x0, y0, w, h) = Px(img, b);
        for (var y = y0; y < y0 + h; y++)
            for (var x = x0; x < x0 + w; x++)
            {
                var o = (y * img.Width + x) * 3;
                yield return (img.Pixels[o], img.Pixels[o + 1], img.Pixels[o + 2]);
            }
    }

    private static int Area(RgbImage img, Box b) { var p = Px(img, b); return p.W * p.H; }
    private static int Count(RgbImage img, Box b, Func<int, int, int, bool> test) => Pixels(img, b).Count(p => test(p.R, p.G, p.B));

    /// <summary>Sum of the R, G and B standard deviations (Pokémon Automation's image_stddev().sum()).</summary>
    public static double StdDevSum(RgbImage img, Box b)
    {
        double sr = 0, sg = 0, sb = 0, qr = 0, qg = 0, qb = 0; var n = 0;
        foreach (var (r, g, bl) in Pixels(img, b)) { n++; sr += r; sg += g; sb += bl; qr += r * r; qg += g * g; qb += bl * bl; }
        double Sd(double s, double q) => Math.Sqrt(Math.Max(0, q / n - s / n * (s / n)));
        return n == 0 ? 0 : Sd(sr, qr) + Sd(sg, qg) + Sd(sb, qb);
    }

    /// <summary>A small grey fingerprint of a slot / screen part, to tell whether it changed.</summary>
    public static byte[] Fingerprint(RgbImage img, Box b, int cells = 12)
    {
        var (x0, y0, w, h) = Px(img, b);
        var f = new byte[cells * cells];
        for (var cy = 0; cy < cells; cy++)
            for (var cx = 0; cx < cells; cx++)
            {
                long sum = 0; var n = 0;
                for (var y = y0 + cy * h / cells; y < y0 + (cy + 1) * h / cells; y++)
                    for (var x = x0 + cx * w / cells; x < x0 + (cx + 1) * w / cells; x++)
                    {
                        var o = (y * img.Width + x) * 3;
                        sum += img.Pixels[o] + img.Pixels[o + 1] + img.Pixels[o + 2]; n++;
                    }
                f[cy * cells + cx] = (byte)(n == 0 ? 0 : sum / (3 * n));
            }
        return f;
    }

    public static double Difference(byte[] a, byte[] b) => a.Length != b.Length ? 255 : a.Zip(b, (x, y) => Math.Abs(x - y)).Average();

    /// <summary>
    /// The little icons of the previous and next Pokémon at the top of the summary (left of L, right of R). After R they
    /// always change (the old Pokémon becomes the previous one), even when two Pokémon in a row look exactly alike.
    /// </summary>
    public static byte[] SummaryNeighbours(RgbImage img) =>
        Fingerprint(img, new Box(0.05, 0.095, 0.04, 0.065), 16).Concat(Fingerprint(img, new Box(0.915, 0.095, 0.04, 0.065), 16)).ToArray();

    /// <summary>One slot's icon in the box view (taken while the cursor is on the box name, so nothing covers the grid).</summary>
    public static RgbImage SlotIcon(RgbImage img, int row, int col) =>
        Crop(img, new Box(0.0734 + 0.0724 * col - 0.031, 0.2315 + 0.1056 * row - 0.053, 0.062, 0.106)); // measured on the user's HOME

    /// <summary>The summary's Pokémon picture area, to see when the next Pokémon (R) finished sliding in.</summary>
    public static readonly Box SummaryChangeBox = new(0.388, 0.238, 0.109, 0.062);

    // ================================================================ reference pictures
    /// <summary>
    /// Balls, types and origin marks matched against Pokémon Automation's own reference pictures (Resources\PokemonHome\
    /// PokeballSprites.png + .json, Resources\Pokemon\Types\Gen9\*.png, Resources\PokemonHome\OriginMarks\*.png) read from
    /// the user's Pokémon Automation folder - nothing of theirs is copied into ShinyForge.
    /// </summary>
    public static class References
    {
        private sealed record Sprite(string Name, int W, int H, byte[] Rgba);
        private static List<Sprite>? _balls, _types, _origins;
        private static readonly object Gate = new();

        public static string? Folder { get; set; }

        private static void Load()
        {
            lock (Gate)
            {
                if (_balls != null) return;
                _balls = new(); _types = new(); _origins = new();
                var res = Folder ?? DexTemplates.ResourceFolder();
                if (res == null || DecodeRgba == null) return;
                try
                {
                    var sheet = Path.Combine(res, "PokemonHome", "PokeballSprites.png");
                    var map = Path.Combine(res, "PokemonHome", "PokeballSprites.json");
                    if (File.Exists(sheet) && File.Exists(map) && DecodeRgba(File.ReadAllBytes(sheet)) is { } s)
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(map));
                        var sw = doc.RootElement.GetProperty("spriteWidth").GetInt32();
                        var sh = doc.RootElement.GetProperty("spriteHeight").GetInt32();
                        foreach (var p in doc.RootElement.GetProperty("spriteLocations").EnumerateObject())
                        {
                            var top = p.Value.GetProperty("top").GetInt32();
                            var left = p.Value.GetProperty("left").GetInt32();
                            var px = new byte[sw * sh * 4];
                            for (var y = 0; y < sh; y++)
                                Buffer.BlockCopy(s.Rgba, ((top + y) * s.W + left) * 4, px, y * sw * 4, sw * 4);
                            _balls.Add(new Sprite(p.Name, sw, sh, px));
                        }
                    }
                    foreach (var (folder, list) in new[] { (Path.Combine(res, "Pokemon", "Types", "Gen9"), _types), (Path.Combine(res, "PokemonHome", "OriginMarks"), _origins) })
                        if (Directory.Exists(folder))
                            foreach (var f in Directory.GetFiles(folder, "*.png"))
                                if (DecodeRgba(File.ReadAllBytes(f)) is { } t)
                                    list.Add(new Sprite(Path.GetFileNameWithoutExtension(f), t.W, t.H, t.Rgba));
                }
                catch (Exception e) { AppPaths.Log("HOME sorter: couldn't read Pokémon Automation's reference pictures: " + e.Message); }
            }
        }

        public static bool Available { get { Load(); return _balls!.Count > 0; } }

        private static Dictionary<string, Sprite>? _home;
        private static List<string>? _national;

        /// <summary>
        /// Pokémon Automation's HOME sprites (Resources\Pokemon\AllHomeSprites.png + .json): every boxable Pokémon, form,
        /// gender difference and shiny as HOME draws it in the boxes (64x64), and the national dex list (slug per number).
        /// </summary>
        private static void LoadHome()
        {
            lock (Gate)
            {
                if (_home != null) return;
                _home = new(); _national = new();
                var res = Folder ?? DexTemplates.ResourceFolder();
                if (res == null || DecodeRgba == null) return;
                try
                {
                    var sheet = Path.Combine(res, "Pokemon", "AllHomeSprites.png");
                    var map = Path.Combine(res, "Pokemon", "AllHomeSprites.json");
                    if (File.Exists(sheet) && File.Exists(map) && DecodeRgba(File.ReadAllBytes(sheet)) is { } s)
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(map));
                        var sw = doc.RootElement.GetProperty("spriteWidth").GetInt32();
                        var sh = doc.RootElement.GetProperty("spriteHeight").GetInt32();
                        foreach (var p in doc.RootElement.GetProperty("spriteLocations").EnumerateObject())
                        {
                            var top = p.Value.GetProperty("top").GetInt32();
                            var left = p.Value.GetProperty("left").GetInt32();
                            var px = new byte[sw * sh * 4];
                            for (var y = 0; y < sh; y++) Buffer.BlockCopy(s.Rgba, ((top + y) * s.W + left) * 4, px, y * sw * 4, sw * 4);
                            _home[p.Name] = new Sprite(p.Name, sw, sh, px);
                        }
                    }
                    var nat = Path.Combine(res, "Pokemon", "Pokedex", "Pokedex-National.json");
                    if (File.Exists(nat))
                        _national = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(nat)) ?? new();
                }
                catch (Exception e) { AppPaths.Log("HOME sorter: couldn't read Pokémon Automation's HOME sprites: " + e.Message); }
            }
        }

        /// <summary>The species slug for a national dex number ("bulbasaur" for 1), or "".</summary>
        public static string SpeciesSlug(int dex) { LoadHome(); return dex >= 1 && dex <= _national!.Count ? _national[dex - 1] : ""; }

        /// <summary>The HOME sprites of a species (forms, genders; shiny ones end in "-shiny").</summary>
        public static List<string> FormsOf(string species)
        {
            LoadHome();
            return species.Length == 0 ? new() : _home!.Keys.Where(k => k == species || k == species + "-shiny" || k.StartsWith(species + "-")).ToList();
        }

        /// <summary>
        /// Which form a Pokémon is, from its box icon: HOME's summary doesn't name the form (Minior's core, Alcremie's cream
        /// and sweet, Vivillon's pattern, Unown's letter...). The icon is compared with every sprite of the species (shiny ones
        /// for a shiny): 8x8 cells of average colour where the sprite isn't see-through. Returns the best sprite slug and how
        /// clearly it won (the gap to the next best different form), or null when the species has one look only.
        /// </summary>
        public static (string Slug, double Margin)? Form(RgbImage icon, int dex, bool shiny, string gender = "") =>
            FormRanking(icon, dex, shiny, gender) is { Count: > 0 } r
                ? (r[0].Slug, r.Skip(1).FirstOrDefault(x => Core(x.Slug) != Core(r[0].Slug)).Slug is null ? 999
                    : r.Skip(1).First(x => Core(x.Slug) != Core(r[0].Slug)).Score - r[0].Score)
                : null;

        private static string Core(string slug) => string.Join("-", slug.Split('-').Where(t => t is not ("male" or "female" or "shiny")));

        /// <summary>Every candidate sprite with its score (lower = more alike), best first - for <see cref="Form"/> and tests.</summary>
        public static List<(string Slug, double Score)> FormRanking(RgbImage icon, int dex, bool shiny, string gender = "")
        {
            var species = SpeciesSlug(dex);
            var all = FormsOf(species);
            // shiny: the shiny sprites, plus forms that look the same when shiny (no "-shiny" sprite, e.g. Minior's Meteor form)
            var cands = shiny ? all.Where(k => k.EndsWith("-shiny") || (!k.EndsWith("-shiny") && !all.Contains(k + "-shiny") && !all.Any(o => o.EndsWith("-shiny") && o.StartsWith(k)))).ToList()
                              : all.Where(k => !k.EndsWith("-shiny")).ToList();
            if (cands.Count == 0) cands = all;
            // a gender difference is read from the summary's ♂ / ♀: only that gender's sprites
            if (gender is "male" or "female")
            {
                var other = gender == "male" ? "female" : "male";
                var same = cands.Where(k => !k.Split('-').Contains(other)).ToList();
                if (same.Count > 0) cands = same;
            }
            if (cands.Count <= 1) return cands.Select(c => (c, 0.0)).ToList();
            // the Pokémon in the icon: pixels unlike the pale slot background
            bool Bg((int R, int G, int B) p) => Math.Min(p.R, Math.Min(p.G, p.B)) > 200 && Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B)) < 50;
            int x0 = icon.Width, y0 = icon.Height, x1 = -1, y1 = -1;
            for (var y = 0; y < icon.Height; y++)
                for (var x = 0; x < icon.Width; x++)
                    if (!Bg(Rgb(icon, x, y))) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x + 1); y1 = Math.Max(y1, y + 1); }
            if (x1 - x0 < 8 || y1 - y0 < 8) return new();
            var shot = Cells(8, x0, y0, x1, y1, (x, y) => { var p = Rgb(icon, x, y); return Bg(p) ? null : p; });
            // the silhouette, finer: tells shapes apart (Unown letters, Squawkabilly crests) where colours are the same
            var shape = Shape(icon.Width, icon.Height, (x, y) => !Bg(Rgb(icon, x, y)));
            var scores = new List<(string Slug, double Score)>();
            foreach (var slug in cands)
            {
                var s = _home![slug];
                var (sx0, sy0, sx1, sy1) = OpaqueBounds(s);
                if (sx1 - sx0 < 4) continue;
                var cells = Cells(8, sx0, sy0, sx1, sy1, (x, y) => { var o = (y * s.W + x) * 4; return s.Rgba[o + 3] < 128 ? null : (s.Rgba[o], s.Rgba[o + 1], s.Rgba[o + 2]); });
                // cells where one has the Pokémon and the other doesn't count as a full mismatch (shape: Unown letters)
                var d = cells.Zip(shot).Select(z => z.First == null && z.Second == null ? (double?)null
                    : z.First == null || z.Second == null ? 160 : Dist(z.First.Value, z.Second.Value)).OfType<double>().ToList();
                var sshape = Shape(s.W, s.H, (x, y) => s.Rgba[(y * s.W + x) * 4 + 3] >= 128);
                var overlap = shape == null || sshape == null ? 0.5
                    : (double)shape.Zip(sshape, (a, b) => a && b).Count(v => v) / Math.Max(1, shape.Zip(sshape, (a, b) => a || b).Count(v => v));
                if (d.Count > 0) scores.Add((slug, d.Average() + 120 * (1 - overlap)));
            }
            scores.Sort((a, b) => a.Score.CompareTo(b.Score));
            return scores;
        }

        /// <summary>The ball: the screen part and every ball sprite brought to 24x24 and compared where the sprite isn't see-through.</summary>
        public static string Ball(RgbImage img)
        {
            Load();
            if (_balls!.Count == 0) return "";
            var crop = Crop(img, BallBox);
            // find the ball itself: pixels unlike the background (taken from the bottom corners); rows that are almost all
            // "different" are something covering the screen (a friend-online popup), not the ball
            var bg = Avg(Rgb(crop, 1, crop.Height - 2), Rgb(crop, crop.Width - 2, crop.Height - 2));
            bool Ball((int, int, int) p) => Dist(p, bg) > 60;
            int bx0 = crop.Width, by0 = crop.Height, bx1 = -1, by1 = -1;
            for (var y = 0; y < crop.Height; y++)
            {
                var row = Enumerable.Range(0, crop.Width).Count(x => Ball(Rgb(crop, x, y)));
                if (row == 0 || row > crop.Width * 0.9) continue;
                for (var x = 0; x < crop.Width; x++)
                    if (Ball(Rgb(crop, x, y))) { bx0 = Math.Min(bx0, x); by0 = Math.Min(by0, y); bx1 = Math.Max(bx1, x + 1); by1 = Math.Max(by1, y + 1); }
            }
            if (bx1 - bx0 < 10 || by1 - by0 < 10) return "";
            // 6x6 cells of average colour: the sprites are drawn a bit differently from HOME's, cells smooth that out
            var shot = Cells(6, bx0, by0, bx1, by1, (x, y) => Rgb(crop, x, y));
            string best = ""; var bestScore = double.MaxValue;
            foreach (var s in _balls)
            {
                var (x0, y0, x1, y1) = OpaqueBounds(s);
                if (x1 - x0 < 6) continue;
                var cells = Cells(6, x0, y0, x1, y1, (x, y) => { var o = (y * s.W + x) * 4; return s.Rgba[o + 3] < 200 ? null : (s.Rgba[o], s.Rgba[o + 1], s.Rgba[o + 2]); });
                var d = cells.Zip(shot).Where(z => z.First != null && z.Second != null).Select(z => Dist(z.First!.Value, z.Second!.Value)).ToList();
                if (d.Count < 20) continue;
                if (d.Average() < bestScore) { bestScore = d.Average(); best = s.Name; }
            }
            return bestScore < 110 ? best : "";
        }

        private static List<(double, double, double)?> Cells(int g, int x0, int y0, int x1, int y1, Func<int, int, (int R, int G, int B)?> at)
        {
            var r = new List<(double, double, double)?>();
            for (var cy = 0; cy < g; cy++)
                for (var cx = 0; cx < g; cx++)
                {
                    double sr = 0, sg = 0, sb = 0; var n = 0;
                    for (var y = y0 + cy * (y1 - y0) / g; y < y0 + (cy + 1) * (y1 - y0) / g; y++)
                        for (var x = x0 + cx * (x1 - x0) / g; x < x0 + (cx + 1) * (x1 - x0) / g; x++)
                            if (at(x, y) is { } p) { sr += p.R; sg += p.G; sb += p.B; n++; }
                    r.Add(n == 0 ? null : (sr / n, sg / n, sb / n));
                }
            return r;
        }


        /// <summary>
        /// The one or two type circles: coloured blobs side by side in the type box. Each blob's main colour is compared with
        /// each type icon's main colour.
        /// </summary>
        public static (string, string) Types(RgbImage img)
        {
            Load();
            if (_types!.Count == 0) return ("", "");
            // the first type's circle always sits at the same spot, the second (if any) right next to it - they can touch, so
            // each spot is sampled on its own instead of looking for separate blobs
            var names = new[] { TypeSpot1, TypeSpot2 }.Select(spot =>
            {
                var px = Disc(img, spot.X, spot.Y, 0.011).Where(IconPixel).ToList();
                if (px.Count < 80) return (Name: "", D: 999.0); // only teal header: no second type
                var c = MainColour(px);
                return _types.Select(t => (t.Name, D: Dist(c, MainColour(Opaque(t))))).OrderBy(t => t.D).First();
            }).Where(t => t.D < 60).Select(t => t.Name).ToList();
            return (names.ElementAtOrDefault(0) ?? "", names.ElementAtOrDefault(1) ?? "");
        }

        /// <summary>The origin mark: the dark glyph's shape (16x16 on/off) against each mark's opaque shape.</summary>
        public static string Origin(RgbImage img)
        {
            Load();
            if (_origins!.Count == 0) return "";
            var crop = Crop(img, OriginBox);
            var dark = Shape(crop.Width, crop.Height, (x, y) => { var (r, g, b) = Rgb(crop, x, y); return r + g + b < 3 * 110; });
            if (dark == null) return "";
            string best = ""; var bestScore = 0.0;
            foreach (var o in _origins)
            {
                var shape = Shape(o.W, o.H, (x, y) => o.Rgba[(y * o.W + x) * 4 + 3] > 128);
                if (shape == null) continue;
                var both = dark.Zip(shape, (a, b) => a && b).Count(v => v);
                var either = dark.Zip(shape, (a, b) => a || b).Count(v => v);
                var iou = either == 0 ? 0 : (double)both / either;
                if (iou > bestScore) { bestScore = iou; best = o.Name; }
            }
            return bestScore > 0.55 ? best : "";
        }

        private static bool[]? Shape(int w, int h, Func<int, int, bool> on)
        {
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) if (on(x, y)) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            if (x1 - x0 < 6 || y1 - y0 < 6) return null;
            var r = new bool[16 * 16];
            for (var y = 0; y < 16; y++)
                for (var x = 0; x < 16; x++)
                    r[y * 16 + x] = on(x0 + (x * 2 + 1) * (x1 - x0 + 1) / 32, y0 + (y * 2 + 1) * (y1 - y0 + 1) / 32);
            return r;
        }

        private static (int, int, int, int) OpaqueBounds(Sprite s)
        {
            int x0 = s.W, y0 = s.H, x1 = 0, y1 = 0;
            for (var y = 0; y < s.H; y++) for (var x = 0; x < s.W; x++)
                    if (s.Rgba[(y * s.W + x) * 4 + 3] > 128) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x + 1); y1 = Math.Max(y1, y + 1); }
            return (x0, y0, x1, y1);
        }

        private static IEnumerable<(int, int, int)> Opaque(Sprite s)
        {
            for (var i = 0; i < s.W * s.H; i++)
                if (s.Rgba[i * 4 + 3] > 200) yield return (s.Rgba[i * 4], s.Rgba[i * 4 + 1], s.Rgba[i * 4 + 2]);
        }

        /// <summary>HOME's teal header behind the type icons.</summary>
        private static readonly (double, double, double) Teal = (24, 206, 184);

        /// <summary>Part of a type icon's coloured disc: not the teal header and not the white glyph.</summary>
        private static bool IconPixel((int R, int G, int B) p) => Math.Min(p.R, Math.Min(p.G, p.B)) <= 190 && Dist(p, Teal) >= 70;

        private static (double, double, double) Avg((int R, int G, int B) a, (int R, int G, int B) b) => ((a.R + b.R) / 2.0, (a.G + b.G) / 2.0, (a.B + b.B) / 2.0);

        /// <summary>Average colour of a type icon's disc (white glyph and teal header left out).</summary>
        private static (double, double, double) MainColour(IEnumerable<(int R, int G, int B)> px)
        {
            double r = 0, g = 0, b = 0; var n = 0;
            foreach (var p in px.Where(IconPixel)) { r += p.R; g += p.G; b += p.B; n++; }
            return n == 0 ? (0, 0, 0) : (r / n, g / n, b / n);
        }

        private static double Dist((int R, int G, int B) a, (double R, double G, double B) b) => Dist(((double)a.R, (double)a.G, (double)a.B), b);

        private static double Dist((double R, double G, double B) a, (double R, double G, double B) b) =>
            Math.Sqrt((a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B));

        /// <summary>The pixels in a circle (radius as a fraction of the picture height... width-corrected).</summary>
        private static IEnumerable<(int R, int G, int B)> Disc(RgbImage img, double cx, double cy, double r)
        {
            var rx = r * img.Width; var ry = r * img.Width; // round on screen: same radius in pixels both ways
            int x0 = (int)(cx * img.Width - rx), x1 = (int)(cx * img.Width + rx), y0 = (int)(cy * img.Height - ry), y1 = (int)(cy * img.Height + ry);
            for (var y = Math.Max(0, y0); y <= Math.Min(img.Height - 1, y1); y++)
                for (var x = Math.Max(0, x0); x <= Math.Min(img.Width - 1, x1); x++)
                {
                    var dx = (x - cx * img.Width) / rx; var dy = (y - cy * img.Height) / ry;
                    if (dx * dx + dy * dy <= 1) yield return Rgb(img, x, y);
                }
        }

        private static (int, int, int) Rgb(RgbImage i, int x, int y) { var o = (y * i.Width + x) * 3; return (i.Pixels[o], i.Pixels[o + 1], i.Pixels[o + 2]); }
    }
}
