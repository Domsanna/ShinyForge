using System.Text.Json;
using ShinyForge.Core;
using Btn = ShinyForge.Services.HomeSorter.PaBotBase2.Button;

namespace ShinyForge.Services.HomeSorter;

/// <summary>
/// Runs a HOME sort on the console: a PABotBase2 board is the controller, the capture card is the eyes.
/// 1. reads every box in the range (empty slots from the box picture, each Pokémon from its summary screen),
/// 2. plans the whole sort (<see cref="HomeSortPlanner"/>) and writes the catalogue + plan files,
/// 3. does the swaps (unless Dry Run), checking the cursor before each Y press and the moved Pokémon after it,
///    and saves where everything is after every swap so a stopped run never loses track.
/// Navigation follows Pokémon Automation's HOME programs (L/R for boxes, the d-pad wraps around the grid), but every
/// step is checked on screen and corrected instead of being counted blind.
/// </summary>
public sealed class HomeSorterRunner
{
    private readonly HomeSorterOptions _o;
    private readonly Action<string> _log;
    private readonly CancellationToken _ct;
    private PaBotBase2 _pad = null!;
    private HomeCapture _cam = null!;
    private (int Box, int Row, int Col) _cursor = (-1, -1, -1);
    /// <summary>Wait after each quick L / R press when crossing many boxes.</summary>
    private const int FastBoxMs = 230;
    /// <summary>Set when HOME missed too many quick presses: boxes are then changed one checked press at a time.</summary>
    private bool _slowBoxes;
    private StreamWriter? _logFile;

    public event Action<string>? Progress;
    public event Action<int, int, int>? Stats; // pokémon, empty, swaps
    public int Swaps { get; private set; }
    /// <summary>The log file of the last run (for error reports).</summary>
    public string? LogFile { get; private set; }
    /// <summary>Pokémon whose summary couldn't be read (they're left where they are).</summary>
    public int Unreadable { get; private set; }

    public static string OutputDir => Directory.CreateDirectory(OutputDirOverride ?? Path.Combine(OsInfo.DocumentsDir, "ShinyForge", "HOME Sorter")).FullName;
    /// <summary>Tests use their own folder, so they never touch a real run's saved progress (session.json, remembered boxes).</summary>
    public static string? OutputDirOverride { get; set; }
    public static string CatalogFile => Path.Combine(OutputDir, "remembered_boxes.json");

    private readonly bool _resume;
    private HomeSession _session = new();

    /// <param name="resume">Continue the stopped run saved in session.json (same settings): boxes it read aren't read again.</param>
    public HomeSorterRunner(HomeSorterOptions o, Action<string> log, CancellationToken ct, bool resume = false)
    {
        _resume = resume;
        _o = o;
        _ct = ct;
        _log = s =>
        {
            log(s);
            try { _logFile?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); _logFile?.Flush(); } catch { }
        };
    }

    // ================================================================ checks before starting
    /// <summary>Pokémon Automation's start checks plus ShinyForge's; returns the problems (empty = fine).</summary>
    public static List<string> Check(HomeSorterOptions o, List<DexEntry>? template)
    {
        var p = new List<string>();
        if (HomeVision.Ocr == null) p.Add("No text reader (OCR) is available on this computer.");
        if (o.Mode == SortMode.BoxSorter)
        {
            if (o.Rules.Count == 0) p.Add("At least one sort rule is needed.");
            p.AddRange(DexTemplates.SortRangeProblems(o, HomeSortPlanner.Boxes));
        }
        else
        {
            if (template == null) p.Add("The living dex template couldn't be read.");
            else p.AddRange(DexTemplates.BoxProblems(template.Count, o, HomeSortPlanner.Boxes));
        }
        return p;
    }

    // ================================================================ run
    public async Task<SortPlan?> RunAsync() => await Task.Run(Run, _ct);

    /// <summary>Diagnostics: goes to box 1, presses L once and returns the box HOME then shows (300 = the boxes wrap around).</summary>
    public async Task<int> ProbeWrap() => await Task.Run(() =>
    {
        try
        {
            Connect();
            ExitMenus();
            NormalMode();
            GoToBox(0);
            Press(Btn.L, _o.GameDelayMs + 400);
            for (var i = 0; i < 5; i++)
                if (Frame() is { } f && HomeVision.ReadBoxCounter(f) is { } c) return c.Box + 1;
            return -1;
        }
        finally { Release(); }
    }, _ct);

    /// <summary>Diagnostics: goes to a box's first slot, then presses the buttons one by one, saving a picture after each.</summary>
    public async Task Explore(int box, string[] keys, string folder) => await Task.Run(() =>
    {
        try
        {
            Connect();
            ExitMenus();
            NormalMode();
            GoToBox(box);
            GoToSlot(0, 0);
            var n = 0;
            if (_cam.LastJpeg() is { } j0) File.WriteAllBytes(Path.Combine(folder, $"explore-{n:00}-start.jpg"), j0);
            foreach (var k in keys)
            {
                Press(Enum.Parse<Btn>(k, true), _o.GameDelayMs + 400);
                Frame();
                if (_cam.LastJpeg() is { } j) File.WriteAllBytes(Path.Combine(folder, $"explore-{++n:00}-{k}.jpg"), j);
                _log($"pressed {k}");
            }
        }
        finally { Release(); }
    }, _ct);

    /// <summary>Steps through boxes from <paramref name="start"/> (reading only) to the first one with at least two Pokémon.</summary>
    public async Task<int> FindBoxWithPokemon(int start, int count) => await Task.Run(() =>
    {
        try
        {
            Connect();
            ExitMenus();
            NormalMode();
            for (var b = start; b < Math.Min(Slot.MaxBoxes, start + count); b++)
            {
                _ct.ThrowIfCancellationRequested();
                GoToBox(b);
                GoToSlot(0, 0);
                var n = HomeVision.OccupiedSlots(Frame() ?? throw Fail("No picture.")).Count(x => x);
                _log($"Box {b + 1}: {n} Pokémon");
                if (n >= 2) return b;
            }
            return -1;
        }
        finally
        {
            Release();
        }
    }, _ct);

    /// <summary>
    /// Swaps the first two Pokémon of a box and swaps them back, each move checked in the info bar - a safe way to see that
    /// the controller, the capture card and the swap work (the box ends exactly as it started).
    /// </summary>
    public async Task<bool> SwapTest(int box) => await Task.Run(() =>
    {
        _logFile = new StreamWriter(Path.Combine(OutputDir, $"swap test {DateTime.Now:yyyy-MM-dd HH-mm-ss}.log"));
        try
        {
            Connect();
            ExitMenus();
            NormalMode();
            GoToBox(box);
            GoToSlot(0, 0);
            var occupied = HomeVision.OccupiedSlots(Frame() ?? throw Fail("No picture."));
            // the first and the last Pokémon: the carried one crosses rows and columns (where a misread cursor once dropped
            // Pokémon in the wrong slots)
            var all = Enumerable.Range(0, Slot.PerBox).Where(i => occupied[i]).ToList();
            var two = all.Count < 2 ? all : new List<int> { all[0], all[^1] };
            two = two.Select(i => box * Slot.PerBox + i).ToList();
            if (two.Count < 2) throw new InvalidOperationException($"Box {box + 1} needs at least two Pokémon for the swap test.");
            var slots = new Dictionary<int, HomeMon?>();
            foreach (var i in two)
            {
                GoTo(i);
                var name = HomeVision.BarSpecies(Frame() ?? throw Fail("No picture."));
                _log($"{Slot.FromIndex(i)}: {name}");
                slots[i] = new HomeMon { Species = name };
            }
            DoSwap(new SwapStep(two[0], two[1], "test"), slots);
            _log("Swapped - now swapping back.");
            DoSwap(new SwapStep(two[1], two[0], "test back"), slots);
            _log("Swap test done: the box is as it was.");
            return true;
        }
        finally
        {
            Release();
            _logFile?.Dispose();
            _logFile = null;
        }
    }, _ct);

    private SortPlan? Run()
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        LogFile = Path.Combine(OutputDir, $"run {stamp}.log");
        _logFile = new StreamWriter(LogFile);
        try
        {
            List<DexEntry>? template = null;
            if (_o.Mode == SortMode.LivingDex)
            {
                var path = DexTemplates.PathFor(_o) ?? throw new FileNotFoundException("Pokémon Automation's Resources folder wasn't found, so its living dex templates can't be read. Pick a Custom template file or set Pokémon Automation's folder in Settings.");
                var one = DexTemplates.Load(path);
                template = DexTemplates.Layout(one, _o);
                _log($"Living dex template: {Path.GetFileName(path)} ({one.Count} slots, {(one.Count + 29) / 30} boxes).");
                if (DexTemplates.ShinyStartBox(one, _o) is { } shinyBox)
                    _log($"Regular dex: boxes {_o.LivingDexStartBox}-{shinyBox - 1}; shiny dex right after it: boxes {shinyBox}-{_o.LivingDexStartBox + (template.Count + 29) / 30 - 1}.");
            }
            var problems = Check(_o, template);
            if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
            if (!HomeVision.References.Available)
                _log("Pokémon Automation's ball/type pictures weren't found - balls, types and origin marks won't be read (regional forms can't be told apart).");

            Connect();
            Status("Finding the box view...");
            ExitMenus();
            NormalMode();

            // ---- 1. read the boxes
            var boxes = _o.Mode == SortMode.BoxSorter
                ? Enumerable.Range(_o.SortStartBox - 1, _o.BoxCount).ToList()
                : Enumerable.Range(_o.LivingDexStartBox - 1, (template!.Count + 29) / 30)
                    .Concat(Enumerable.Range(_o.RejectBoxStart - 1, _o.RejectBoxEnd - _o.RejectBoxStart + 1)).ToList();
            // every scan starts at box 1 and goes up from there: the same starting point every time. Box 1 is read too (HOME
            // puts gifts and transfers there): a new arrival that fills a missing dex slot moves into the dex, the rest stays.
            Status("Starting at box 1 - " + HomeSorterOptions.Box1Rule);
            GoToBox(0);
            boxes = boxes.Where(b => b != 0).Distinct().OrderBy(b => b).ToList();
            if (_o.Mode == SortMode.LivingDex) boxes.Insert(0, 0);
            var slots = new Dictionary<int, HomeMon?>();
            // continue a stopped run: the boxes it read come from memory (box 1 is always read again - gifts land there)
            var saved = _resume ? HomeSession.Resumable(_o) : null;
            var remembered = saved != null ? LoadCatalog() : null;
            if (saved != null && remembered != null)
            {
                _session = saved;
                foreach (var b in saved.ReadBoxes.Where(b => b != 0 && boxes.Contains(b)))
                    for (var i = b * Slot.PerBox; i < (b + 1) * Slot.PerBox; i++) slots[i] = remembered.Slots.GetValueOrDefault(i);
                _log($"Continuing the run from {saved.Started:g}: {saved.ReadBoxes.Count(b => b != 0)} box(es) already read, {saved.SwapsDone} swap(s) done - " +
                     "their Pokémon come from memory (every swap still checks the Pokémon on screen first).");
            }
            else
            {
                _session = new HomeSession { Key = HomeSession.KeyFor(_o) };
                _session.Save();
            }
            var toRead = boxes.Where(b => b == 0 || !_session.ReadBoxes.Contains(b)).ToList();
            var n = 0;
            foreach (var box in toRead)
            {
                _ct.ThrowIfCancellationRequested();
                Status($"Reading box {box + 1} ({++n}/{toRead.Count})...");
                var read = ReadBox(box).ToList();
                foreach (var (i, m) in read) slots[i] = m;
                // remembered right away: a stopped run continues after this box
                SaveCatalog(read.ToDictionary(r => r.Item1, r => r.Item2));
                if (!_session.ReadBoxes.Contains(box)) _session.ReadBoxes.Add(box);
                _session.Save();
                Stats?.Invoke(slots.Values.Count(m => m != null), slots.Values.Count(m => m == null), 0);
            }
            _session.ScanDone = true;
            _session.Save();
            SaveCatalog(slots);
            Save($"{_o.OutputFile}.json", slots);

            // ---- 2. plan
            var cursor = _cursor.Box >= 0 ? new Slot(_cursor.Box, _cursor.Row, _cursor.Col).Index : 0;
            // box 1's Pokémon may fill dex slots, nothing is ever put into box 1
            var box1 = Enumerable.Range(0, Slot.PerBox).Where(i => slots.GetValueOrDefault(i) != null).ToList();
            var plan = _o.Mode == SortMode.BoxSorter ? HomeSortPlanner.PlanBoxSort(slots, _o, cursor) : HomeSortPlanner.PlanLivingDex(slots, template!, _o, cursor, box1);
            if (_o.Mode == SortMode.LivingDex && _o.SearchAllBoxes && plan.Unfilled > 0)
            {
                var outside = SearchOtherBoxes(boxes, template!, plan.MissingEntries, slots);
                cursor = _cursor.Box >= 0 && _cursor.Row >= 0 ? new Slot(_cursor.Box, _cursor.Row, _cursor.Col).Index : cursor;
                plan = HomeSortPlanner.PlanLivingDex(slots, template!, _o, cursor, box1.Concat(outside).ToList());
            }
            // safety: the plan must never put a Pokémon into box 1 that wasn't already there
            foreach (var i in Enumerable.Range(0, Slot.PerBox))
                if (plan.Target.TryGetValue(i, out var t) && t != null && !ReferenceEquals(t, slots.GetValueOrDefault(i)))
                    throw new InvalidOperationException("The plan would put a Pokémon into box 1, which is kept free for gifts and transfers - stopped before moving anything.");
            Save($"{_o.OutputFile}-sorted.json", plan.Target);
            File.WriteAllLines(Path.Combine(OutputDir, $"{_o.OutputFile}-plan.txt"),
                plan.Notes.Concat(plan.Steps.Select((s, i) => $"{i + 1}. {Slot.FromIndex(s.From)} ⇄ {Slot.FromIndex(s.To)}: {s.What}")));
            if (Unreadable > 0) _log($"{Unreadable} Pokémon couldn't be read and stay where they are - see the screenshots in the HOME Sorter folder.");
            _log($"Plan: {plan.Pokemon} Pokémon, {plan.Empty} empty slots, {plan.Excluded} excluded (markings / Pokémon GO), {plan.Steps.Count} swaps.");
            foreach (var note in plan.Notes) _log(note);
            Stats?.Invoke(plan.Pokemon, plan.Empty, 0);
            if (_o.DryRun)
            {
                _session.FromDryRun = true;
                _session.Save();
                RenameAfterSort(boxes, plan.Target.Keys.Union(slots.Keys).ToDictionary(k => k, k => plan.Target.TryGetValue(k, out var t) ? t : slots.GetValueOrDefault(k)),
                                _o.Mode == SortMode.LivingDex ? template : null, dryRun: true);
                Status($"Dry run done: {plan.Steps.Count} swaps planned. See {_o.OutputFile}.json, {_o.OutputFile}-sorted.json and {_o.OutputFile}-plan.txt.");
                return plan;
            }

            // ---- 3. swap
            var swapClock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                _ct.ThrowIfCancellationRequested();
                var step = plan.Steps[i];
                Status($"Swap {i + 1}/{plan.Steps.Count}: {step.What}");
                // how long the rest will take, from the swaps done so far in this run
                if (i > 0 && i % 10 == 0)
                {
                    var left = TimeSpan.FromTicks(swapClock.Elapsed.Ticks / i * (plan.Steps.Count - i));
                    _log($"  {i} swaps done in {swapClock.Elapsed:h\\:mm}; about {(left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min" : $"{left.Minutes} min")} left for the other {plan.Steps.Count - i}.");
                }
                DoSwap(step, slots);
                Swaps++;
                Stats?.Invoke(plan.Pokemon, plan.Empty, Swaps);
                SaveCatalog(slots);
                _session.SwapsDone++;
                _session.Save();
            }
            RenameAfterSort(boxes, slots, _o.Mode == SortMode.LivingDex ? template : null, dryRun: false);
            _session.Finished = true;
            _session.Save();
            Status($"Done: {Swaps} swaps.");
            return plan;
        }
        catch (OperationCanceledException) { _log($"Stopped by you after {Swaps} swaps."); PutBackCarried(); throw; }
        catch (Exception e) { _log("STOPPED: " + e.Message); _log(e.ToString()); PutBackCarried(); throw; } // the reason goes in the run log too
        finally
        {
            Release();
            _logFile?.Dispose();
            _logFile = null;
        }
    }

    private void Status(string s) { _log(s); Progress?.Invoke(s); }

    // ================================================================ box names
    /// <summary>"HOME 10" for an empty box 10 (0-based box 9).</summary>
    public static string EmptyName(int box) => $"HOME {box + 1}";

    /// <summary>
    /// The name a living dex box gets from the dex numbers planned in it: "1-30", "Shiny 31-60" (empty = no dex Pokémon there).
    /// </summary>
    private string DexBoxName(int box, List<DexEntry> layout)
    {
        var start = (_o.LivingDexStartBox - 1) * Slot.PerBox;
        var entries = Enumerable.Range(box * Slot.PerBox - start, Slot.PerBox).Where(i => i >= 0 && i < layout.Count && !layout[i].IsEmpty).Select(i => layout[i]).ToList();
        if (entries.Count == 0) return "";
        var shiny = entries[0].Shiny ?? _o.ShinyDex;
        var name = $"{(shiny ? "Shiny " : "")}{entries.Min(e => e.NatId)}-{entries.Max(e => e.NatId)}";
        return name.Length <= 16 ? name : name[..16];
    }

    /// <summary>
    /// After sorting: empty boxes are named "HOME n" (n = box number) and, if chosen, living dex boxes after their dex numbers.
    /// Only boxes whose name differs are renamed. A dry run only writes down what would be renamed.
    /// </summary>
    private void RenameAfterSort(List<int> boxes, Dictionary<int, HomeMon?> finalSlots, List<DexEntry>? layout, bool dryRun)
    {
        if (!_o.RenameEmptyBoxes && !(_o.NameDexBoxes && layout != null)) return;
        var wanted = new List<(int Box, string Name)>();
        foreach (var box in SweepOrder(boxes, Math.Max(0, _cursor.Box)))
        {
            var empty = Enumerable.Range(box * Slot.PerBox, Slot.PerBox).All(i => finalSlots.GetValueOrDefault(i) == null);
            var name = empty && _o.RenameEmptyBoxes ? EmptyName(box) : _o.NameDexBoxes && layout != null ? DexBoxName(box, layout) : "";
            if (name.Length > 0) wanted.Add((box, name));
        }
        if (dryRun)
        {
            if (wanted.Count > 0)
            {
                File.AppendAllLines(Path.Combine(OutputDir, $"{_o.OutputFile}-plan.txt"),
                    new[] { "", "Box names (only boxes whose name is different get renamed):" }.Concat(wanted.Select(w => $"box {w.Box + 1} -> \"{w.Name}\"")));
                _log($"After sorting, {wanted.Count} box(es) would get a name (empty boxes \"HOME n\"). See the plan file.");
            }
            return;
        }
        var renamed = 0;
        foreach (var (box, name) in wanted)
        {
            _ct.ThrowIfCancellationRequested();
            if (RenameBox(box, name)) renamed++;
        }
        if (wanted.Count > 0) _log($"Box names: {renamed} renamed, the rest already had the right name or couldn't be changed (see above).");
    }

    /// <summary>Renames the empty boxes in a range to "HOME n" (reads each box's slots, no summaries).</summary>
    public async Task<int> RenameEmptyBoxes(int firstBox, int lastBox) => await Task.Run(() =>
    {
        LogFile = Path.Combine(OutputDir, $"rename {DateTime.Now:yyyy-MM-dd HH-mm-ss}.log");
        _logFile = new StreamWriter(LogFile);
        try
        {
            Connect();
            ExitMenus();
            NormalMode();
            var renamed = 0;
            var boxes = SweepOrder(Enumerable.Range(firstBox, lastBox - firstBox + 1).ToList(), CurrentBox());
            var n = 0;
            foreach (var box in boxes)
            {
                _ct.ThrowIfCancellationRequested();
                Status($"Box {box + 1} ({++n}/{boxes.Count}): checking...");
                GoToBox(box);
                GoToSlot(0, 0);
                var f = Frame() ?? throw Fail("No picture.");
                if (HomeVision.OccupiedSlots(f).Any(x => x)) continue;
                if (RenameBox(box, EmptyName(box))) renamed++;
            }
            Status($"Done: {renamed} empty box(es) renamed.");
            return renamed;
        }
        catch (Exception e) when (e is not OperationCanceledException) { _log("STOPPED: " + e.Message); throw; }
        finally { Release(); _logFile?.Dispose(); _logFile = null; }
    }, _ct);

    /// <summary>The Switch keyboard's letter keys, row by row (row 0 = digits); the key under the cursor starts at "1".</summary>
    private static readonly string[] KeyRows = { "1234567890-", "qwertyuiop/", "asdfghjkl:'", "zxcvbnm,.?!" };

    /// <summary>
    /// Renames one box: Up to its name, A opens the Switch keyboard, B deletes the old name one character at a time, the new
    /// name is typed key by key (Shift = left stick click, Y = space), and only when the keyboard shows exactly the new name
    /// is it confirmed with +. Anything else: X cancels and the old name stays. Returns true when the name is right.
    /// </summary>
    public bool RenameBox(int box, string name)
    {
        if (name.Length > 16 || name.Any(c => c != ' ' && !KeyRows.Any(r => r.Contains(char.ToLowerInvariant(c)))))
        {
            _log($"  box {box + 1}: \"{name}\" can't be typed - skipped.");
            return false;
        }
        GoToBox(box);
        GoToSlot(0, 0);
        var current = Frame() is { } f0 ? HomeVision.ReadBoxName(f0) : "";
        if (current == name) return true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Status($"Box {box + 1}: renaming \"{current}\" to \"{name}\"...");
            Press(Btn.Up);                       // the box name above the grid
            Press(Btn.A, _o.GameDelayMs + 600);  // opens the keyboard
            int? len = null;
            for (var i = 0; i < 8 && len == null; i++) if (Frame() is { } f) len = HomeVision.KeyboardLength(f);
            if (len == null)
            {
                _log("  the keyboard didn't open - backing out.");
                LeaveKeyboard(cancel: true);
                continue;
            }
            var fast = Math.Min(_o.GameDelayMs, 200);
            for (var i = 0; i < len.Value; i++) Press(Btn.B, fast); // delete the old name
            if (Frame() is { } fe && HomeVision.KeyboardLength(fe) is > 0 and var left)
                for (var i = 0; i < left; i++) Press(Btn.B, fast);
            // type it
            int r = 0, c = 0;
            foreach (var ch in name)
            {
                if (ch == ' ') { Press(Btn.Y, fast); continue; }
                var lower = char.ToLowerInvariant(ch);
                var tr = Array.FindIndex(KeyRows, row => row.Contains(lower));
                var tc = KeyRows[tr].IndexOf(lower);
                for (; r < tr; r++) Press(Btn.Down, fast);
                for (; r > tr; r--) Press(Btn.Up, fast);
                for (; c < tc; c++) Press(Btn.Right, fast);
                for (; c > tc; c--) Press(Btn.Left, fast);
                if (char.IsUpper(ch)) Press(Btn.LStick, fast); // Shift for the next letter
                Press(Btn.A, fast);
            }
            var typed = "";
            for (var i = 0; i < 3 && typed != name; i++) if (Frame(i * 100) is { } ft) typed = HomeVision.KeyboardText(ft);
            if (typed == name)
            {
                LeaveKeyboard(cancel: false);
                var now = Frame() is { } fn ? HomeVision.ReadBoxName(fn) : "";
                _log($"  box {box + 1}: \"{current}\" → \"{name}\"{(now == name ? "" : $" (the box now reads \"{now}\")")}");
                return true;
            }
            _log($"  box {box + 1}: the keyboard shows \"{typed}\" instead of \"{name}\" - cancelled, the old name stays.");
            LeaveKeyboard(cancel: true);
        }
        return false;
    }

    /// <summary>Closes the keyboard (+ = OK, X = cancel) and makes sure the box view is back with the cursor in the grid.</summary>
    private void LeaveKeyboard(bool cancel)
    {
        Press(cancel ? Btn.X : Btn.Plus, _o.GameDelayMs + 600);
        for (var i = 0; i < 6; i++)
        {
            if (Frame() is { } f && HomeVision.IsBoxView(f) && HomeVision.KeyboardLength(f) == null) break;
            Press(Btn.B, 400);
        }
        _cursor = (_cursor.Box, -1, -1); // the cursor is on the box name: GoToSlot steps back into the grid
    }

    /// <summary>
    /// The order to read boxes in: one sweep from the current box, R or L - whichever reaches the farthest box with fewer
    /// presses (the boxes wrap). Reading box 103 then box 1 from box 104 is 1 + 102 L presses instead of 103 L + 102 R.
    /// </summary>
    private static List<int> SweepOrder(List<int> boxes, int here)
    {
        var total = HomeSortPlanner.Boxes;
        int Fwd(int b) => ((b - here) % total + total) % total;
        int Back(int b) => ((here - b) % total + total) % total;
        var distinct = boxes.Distinct().ToList();
        return distinct.Max(Fwd) <= distinct.Max(Back) ? distinct.OrderBy(Fwd).ToList() : distinct.OrderBy(Back).ToList();
    }

    /// <summary>
    /// Some living dex slots have no fitting Pokémon in the dex and reject boxes: read the other boxes of HOME (all of them
    /// if needed) for them before giving up. Boxes the remembered catalogue says hold a match go first, then the rest nearest
    /// first (the boxes wrap). Only Pokémon that fill a missing slot are added; the search stops when every slot is found.
    /// Returns the slots of the Pokémon found.
    /// </summary>
    private List<int> SearchOtherBoxes(List<int> readBoxes, List<DexEntry> layout, List<int> missingIdx, Dictionary<int, HomeMon?> slots)
    {
        var missing = missingIdx.Select(i => layout[i]).ToList();
        var total = HomeSortPlanner.Boxes;
        var read = readBoxes.ToHashSet();
        var here = Math.Max(0, _cursor.Box);
        var remembered = LoadCatalog();
        bool Promising(int box) => remembered != null && Enumerable.Range(box * Slot.PerBox, Slot.PerBox)
            .Any(i => remembered.Slots.GetValueOrDefault(i) is { } m && missing.Any(e => HomeSortPlanner.Fits(e, m, _o)));
        var others = Enumerable.Range(0, total).Where(b => !read.Contains(b))
            .OrderBy(b => Promising(b) ? 0 : 1)
            .ThenBy(b => ((b - here) % total + total) % total) // onwards from where the cursor is: one R press per box
            .ToList();
        Status($"{missing.Count} living dex slot(s) have no Pokémon in the dex / reject boxes - looking in the remembered boxes first.");
        var found = new List<int>();
        // 1) the remembered boxes: the nearest remembered Pokémon that fits, checked on screen (info bar) before it counts
        if (remembered != null)
        {
            var here0 = _cursor.Box >= 0 ? new Slot(_cursor.Box, Math.Max(0, _cursor.Row), Math.Max(0, _cursor.Col)).Index : 0;
            foreach (var e in missing.ToList())
            {
                _ct.ThrowIfCancellationRequested();
                var pick = remembered.Slots.Where(kv => kv.Value != null && !read.Contains(kv.Key / Slot.PerBox) && !slots.ContainsKey(kv.Key)
                                                        && HomeSortPlanner.Fits(e, kv.Value!, _o))
                    .OrderBy(kv => HomeSortPlanner.Distance(here0, kv.Key)).Select(kv => (int?)kv.Key).FirstOrDefault();
                if (pick is not { } pos) continue;
                var mon = remembered.Slots[pos]!;
                GoTo(pos);
                var bar = Frame() is { } fb ? HomeVision.BarSpecies(fb) : "";
                if (!SameName(bar, mon.Species))
                {
                    _log($"  remembered {mon.Species} at {Slot.FromIndex(pos)} isn't there any more (the box shows \"{bar}\") - that box will be read.");
                    continue;
                }
                _log($"  found {e.Slug} in memory at {Slot.FromIndex(pos)} (checked on screen).");
                slots[pos] = mon;
                found.Add(pos);
                missing.Remove(e);
                here0 = pos;
            }
        }
        if (missing.Count > 0) Status($"{missing.Count} living dex slot(s) still missing - searching the other {others.Count} boxes for them.");
        var searched = new Dictionary<int, HomeMon?>();
        var n = 0;
        foreach (var box in others)
        {
            if (missing.Count == 0) break;
            _ct.ThrowIfCancellationRequested();
            Status($"Searching box {box + 1} ({++n}/{others.Count}) for {missing.Count} missing Pokémon...");
            foreach (var (i, m) in ReadBox(box))
            {
                searched[i] = m;
                if (m == null) continue;
                var hit = missing.FindIndex(e => HomeSortPlanner.Fits(e, m, _o));
                if (hit < 0) continue;
                _log($"  found {missing[hit].Slug} ({m}) at {Slot.FromIndex(i)}");
                slots[i] = m;
                found.Add(i);
                missing.RemoveAt(hit);
            }
            SaveCatalog(searched); // remembered for next time (helps the next search start in the right box)
        }
        _log(missing.Count == 0 ? $"All missing Pokémon found ({found.Count})."
            : $"Searched {n} box(es): found {found.Count}; still missing: {string.Join(", ", missing.Take(20).Select(e => e.Slug))}{(missing.Count > 20 ? ", ..." : "")}.");
        return found;
    }

    private bool _gate;

    /// <summary>Lets go of the board, the capture card and the board lock (keep-awake may nudge again).</summary>
    private void Release()
    {
        try { _cam?.Dispose(); } catch { }
        try { _pad?.Dispose(); } catch { }
        _cam = null!;
        _pad = null!;
        if (_gate) { _gate = false; ConsoleKeepAwake.BoardGate.Release(); }
    }

    private void Connect()
    {
        if (!_gate)
        {
            if (!ConsoleKeepAwake.BoardGate.Wait(TimeSpan.FromSeconds(20))) throw new IOException("The controller board is busy (keep-awake or a setup check) - try again.");
            _gate = true;
        }
        Status("Connecting to the controller board...");
        var ports = _o.ComPort.Length > 0 ? new[] { _o.ComPort }
            : PokeldnService.FindBoards().Select(b => b.Port).Concat(PaBotBase2.Ports()).Distinct().ToArray();
        if (ports.Length == 0) throw new IOException("No serial port was found - plug in the ESP32-S3 / Pico W flashed with Pokémon Automation's PABotBase2 firmware.");
        Exception? last = null;
        foreach (var port in ports)
        {
            try
            {
                _pad = PaBotBase2.Connect(port, _log);
                _o.ComPort = port;
                _log($"Controller: {_pad.DeviceName} on {port}.");
                break;
            }
            catch (Exception e) { last = e; _log($"{port}: {e.Message}"); }
        }
        if (_pad == null) throw new IOException(last?.Message ?? "No controller board answered.", last);
        if (_pad.ConsoleConnected() == false)
            _log("The board says the Switch isn't talking to it yet - plug the board's USB cable into the dock / console.");

        var device = _o.Device.Length > 0 ? _o.Device : HomeCapture.DefaultDevice() ?? throw new IOException("No capture card was found.");
        _cam = HomeCapture.Start(device);
        _log($"Capture card: {device}.");
        // the card itself takes a few seconds to start (USB Video / MS2109 cards especially)
        if (_cam.Grab(_o.VideoDelayMs, 15000) is not { } first) throw new IOException(_cam.NoPictureReason());
        if (HomeVision.StdDevSum(first, new HomeVision.Box(0, 0, 1, 1)) < 3)
            throw new IOException($"The capture card \"{device}\" shows a black picture - wake the console and open Pokémon HOME's boxes.");
        // the first press of a new controller often only wakes the console up
        _pad.Press(Btn.None, 50, 200);
    }

    // ================================================================ screen + buttons
    private RgbImage? Frame(int extraDelay = 0) => _cam.Grab(_o.VideoDelayMs + extraDelay);

    private void Press(Btn b, int after = -1)
    {
        try { _pad.Press(b, 80, after < 0 ? _o.GameDelayMs : after); }
        catch (Exception e) when (e is TimeoutException or IOException && !_ct.IsCancellationRequested)
        {
            // the board stopped answering (a USB hiccup): connect again and press once more. Positions are checked on
            // screen afterwards (box counter, cursor, the Pokémon in the info bar), so a press that did happen is caught.
            _log($"  the controller board stopped answering ({e.Message}) - connecting to it again...");
            try { _pad?.Dispose(); } catch { }
            _pad = null!;
            Thread.Sleep(2000);
            ReconnectBoard();
            _pad.Press(b, 80, after < 0 ? _o.GameDelayMs : after);
        }
    }

    /// <summary>Opens the board again on the same port (after it stopped answering), up to 3 tries.</summary>
    private void ReconnectBoard()
    {
        Exception? last = null;
        for (var i = 0; i < 3; i++)
        {
            try
            {
                _pad = PaBotBase2.Connect(_o.ComPort, _log);
                _log($"  connected again: {_pad.DeviceName} on {_o.ComPort}.");
                _pad.Press(Btn.None, 50, 200);
                return;
            }
            catch (Exception e) { last = e; _log($"  {_o.ComPort}: {e.Message}"); Thread.Sleep(3000); }
        }
        throw new IOException("The controller board stopped answering and couldn't be connected again: " + last?.Message, last);
    }

    private RgbImage Need(Func<RgbImage, bool> test, string what, int tries = 8)
    {
        for (var i = 0; i < tries; i++)
        {
            _ct.ThrowIfCancellationRequested();
            if (Frame() is { } f && test(f)) return f;
            Thread.Sleep(120);
        }
        throw Fail($"{what} didn't show up on the capture card.");
    }

    private Exception Fail(string why)
    {
        try
        {
            if (_cam?.LastJpeg() is { } jpg)
            {
                var file = Path.Combine(OutputDir, $"problem {DateTime.Now:yyyy-MM-dd HH-mm-ss}.jpg");
                File.WriteAllBytes(file, jpg);
                why += $" (screenshot: {file})";
            }
        }
        catch { }
        return new InvalidOperationException(why);
    }

    /// <summary>B until the box view shows (Pokémon Automation's exit_menus; B only ever closes things in HOME's boxes).</summary>
    private void ExitMenus()
    {
        for (var i = 0; i < 20; i++)
        {
            if (Frame() is { } f && HomeVision.IsBoxView(f) && !HomeVision.IsSummary(f)) return;
            Press(Btn.B, 250);
        }
        throw Fail("HOME's box view wasn't found. Open Pokémon HOME, go to Pokémon (the boxes), and start again.");
    }

    /// <summary>Normal selection mode (red cursor). ZL cycles normal → relocate → multi, ZR the other way.</summary>
    private void NormalMode()
    {
        for (var i = 0; i < 4; i++)
        {
            var f = Frame();
            var c = f == null ? null : HomeVision.FindCursor(f);
            if (c == null) { Press(Btn.Down); continue; } // the cursor may be on the box name: step into the grid
            if (c.Value.Mode == "normal") return;
            if (c.Value.Mode == "glove")
            {
                // a Pokémon is still carried (a run stopped in the middle of a swap): B puts it back where it was picked up,
                // which is where ShinyForge's memory has it (a swap is only remembered once it's finished)
                _log("A Pokémon was still being carried from a stopped swap - B puts it back where it was picked up.");
                Press(Btn.B, _o.GameDelayMs + 400);
                continue;
            }
            Press(c.Value.Mode == "relocate" ? Btn.ZR : Btn.ZL, 300);
        }
        throw Fail("The box cursor (red arrow) wasn't found.");
    }

    /// <summary>Where the glove (carried Pokémon) is, from up to 3 pictures; null when it can't be seen.</summary>
    private (int Row, int Col)? GloveAt()
    {
        for (var i = 0; i < 3; i++)
            if (Frame(i * 120) is { } f && HomeVision.FindGlove(f) is { } g) return g;
        return null;
    }

    /// <summary>After a failed swap: if a Pokémon is still carried, B puts it back, so HOME matches what's remembered.</summary>
    private void PutBackCarried()
    {
        try
        {
            if (_pad == null || Frame() is not { } f || HomeVision.FindGlove(f) == null) return;
            _log("Putting the carried Pokémon back where it was picked up (B).");
            Press(Btn.B, _o.GameDelayMs + 400);
        }
        catch (Exception e) { _log("  couldn't put it back: " + e.Message); }
    }

    /// <summary>
    /// The box HOME shows. Once ShinyForge has found it, every L / R press is checked on screen and counted, and that count
    /// is trusted over the text reader (which can misread the first digit); a confident reading that disagrees stops the run.
    /// </summary>
    /// <summary>The box counter, read the same twice in a row (null when it can't be read).</summary>
    private int? CounterSure()
    {
        int? last = null;
        for (var i = 0; i < 10; i++)
        {
            if (Frame(i == 0 ? 0 : 150) is { } f && HomeVision.ReadBoxCounter(f) is { } c)
            {
                HomeSortPlanner.Boxes = c.Total;
                if (last == c.Box) return c.Box;
                last = c.Box;
            }
        }
        return null;
    }

    private int CurrentBox()
    {
        for (var i = 0; i < 4; i++)
            if (Frame() is { } f && HomeVision.ReadBoxCounter(f) is { } c)
            {
                HomeSortPlanner.Boxes = c.Total;
                if (_cursor.Box >= 0 && c.Box != _cursor.Box)
                {
                    // only believe a different number when a second picture says the same
                    if (Frame(200) is not { } f2 || HomeVision.ReadBoxCounter(f2) is not { } c2 || c2.Box != c.Box) return _cursor.Box;
                    _log($"  HOME's counter shows box {c.Box + 1} (twice), ShinyForge had counted box {_cursor.Box + 1} - going by the counter.");
                }
                _cursor = (c.Box, _cursor.Row, _cursor.Col);
                return c.Box;
            }
        if (_cursor.Box >= 0) return _cursor.Box;
        throw Fail("The box number under the box (like 12/300) couldn't be read - is HOME on the Pokémon boxes?");
    }

    /// <summary>
    /// L / R one box at a time; each press must visibly change the counter / box name (a dropped press is pressed again)
    /// and is counted. The counter is read back at the end.
    /// </summary>
    private void GoToBox(int box)
    {
        for (var round = 0; round < 6; round++)
        {
            var now = CurrentBox();
            if (now == box) return;
            // the shorter way round: HOME's boxes wrap (L on box 1 goes to the last box)
            var total = HomeSortPlanner.Boxes;
            var forward = ((box - now) % total + total) % total;
            var dir = forward <= total - forward ? Btn.R : Btn.L;
            var steps = Math.Min(forward, total - forward);
            _log($"  box {now + 1} -> {box + 1}: {steps} x {(dir == Btn.R ? "R" : "L")}");
            if (!_slowBoxes && steps > 2)
            {
                // quick: press L / R in a row (about 0.3 s a box instead of 2 s), then read the counter once and fix any
                // missed press on the next round. If HOME misses many quick presses, go back to one box at a time.
                for (var i = 0; i < steps; i++) Press(dir, FastBoxMs);
                Thread.Sleep(700); // the last box slides in
                // only a counter read twice the same counts here - never the count of presses
                if (CounterSure() is not { } seen)
                {
                    _slowBoxes = true;
                    _log("  the box counter couldn't be read after the quick presses - going one box at a time from here on.");
                    throw Fail($"The box number couldn't be read after moving towards box {box + 1}.");
                }
                _cursor = (seen, _cursor.Row, _cursor.Col);
                if (seen == box) return;
                var left = Math.Min(((box - seen) % total + total) % total, ((seen - box) % total + total) % total);
                if (left > steps / 2)
                {
                    _slowBoxes = true;
                    _log($"  HOME missed most of the quick presses (now box {seen + 1}) - going one box at a time from here on.");
                }
                continue;
            }
            while (now != box)
            {
                var before = Frame() is { } f0 ? HomeVision.CounterPrint(f0) : null;
                var moved = false;
                for (var tries = 0; tries < 3 && !moved; tries++)
                {
                    Press(dir, _o.GameDelayMs + 240);
                    // HOME slides the next box in: give it up to 2 s before deciding the press didn't register
                    var until = DateTime.UtcNow.AddSeconds(2);
                    while (!moved && DateTime.UtcNow < until)
                    {
                        moved = before == null || Frame() is not { } f1 || HomeVision.CounterChanged(before, HomeVision.CounterPrint(f1));
                        if (!moved) Thread.Sleep(80);
                    }
                    if (!moved) _log("  the box didn't change - pressing again");
                }
                if (!moved) throw Fail($"HOME doesn't change boxes (going to box {box + 1}).");
                now = ((now + (dir == Btn.R ? 1 : -1)) % total + total) % total;
                _cursor = (now, _cursor.Row, _cursor.Col);
            }
            // read back: a confident counter that disagrees wins, and the loop goes the rest of the way
        }
        if (CurrentBox() != box) throw Fail($"Couldn't get to box {box + 1}.");
    }

    /// <summary>Moves the cursor inside the box, checking where it really is first and after.</summary>
    private void GoToSlot(int row, int col)
    {
        for (var attempt = 0; ; attempt++)
        {
            var f = Frame();
            var c = f == null ? null : HomeVision.FindCursor(f);
            // while a Pokémon is picked up (after the first Y) the arrow looks different: go by the tracked position then
            if (c == null && _cursor.Row >= 0) c = (_cursor.Row, _cursor.Col, "tracked");
            if (c == null)
            {
                if (attempt >= 3) break;
                Press(Btn.Down); // off the grid (box name): step back in
                continue;
            }
            var (r, k, _) = c.Value;
            if (r == row && k == col) { _cursor = (_cursor.Box, row, col); return; }
            if (attempt >= 4) break;
            // rows: straight, or through the box name / bottom row when going between the first and last row (PA)
            if ((r == 0 && row == 4) || (r == 4 && row == 0))
                for (var i = 0; i < 3; i++) Press(r == 0 ? Btn.Up : Btn.Down);
            else
                for (var i = 0; i < Math.Abs(row - r); i++) Press(row > r ? Btn.Down : Btn.Up);
            var d = col - k;
            if (Math.Abs(d) <= 3) for (var i = 0; i < Math.Abs(d); i++) Press(d > 0 ? Btn.Right : Btn.Left);
            else for (var i = 0; i < Slot.Cols - Math.Abs(d); i++) Press(d > 0 ? Btn.Left : Btn.Right);
            _cursor = (_cursor.Box, row, col);
        }
        // last resort: up to the box name (a fixed place), down into row 1, and go from there
        for (var i = 0; i < 7; i++)
        {
            if (Frame() is { } fa && HomeVision.CursorOnBoxName(fa)) break;
            Press(Btn.Up);
        }
        Press(Btn.Down);
        var fb = Frame();
        var start = fb == null ? null : HomeVision.FindCursor(fb);
        var (r0, c0) = start is { } st ? (st.Row, st.Col) : (0, 0);
        for (var i = 0; i < Math.Abs(row - r0); i++) Press(row > r0 ? Btn.Down : Btn.Up);
        for (var i = 0; i < Math.Abs(col - c0); i++) Press(col > c0 ? Btn.Right : Btn.Left);
        if (Frame() is { } fc && HomeVision.FindCursor(fc) is { } end && (end.Row != row || end.Col != col))
            throw Fail($"The cursor didn't reach row {row + 1}, column {col + 1}.");
        _cursor = (_cursor.Box, row, col);
    }

    private void GoTo(int index)
    {
        var s = Slot.FromIndex(index);
        GoToBox(s.Box);
        GoToSlot(s.Row, s.Col);
    }

    // ================================================================ reading a box
    /// <summary>
    /// Reads a box. Every Pokémon is read - two that look exactly alike are still two Pokémon. HOME leaves the cursor on the
    /// last Pokémon shown, so if it isn't on the box's last Pokémon afterwards a press got lost: the box is read again, slower.
    /// </summary>
    private IEnumerable<(int, HomeMon?)> ReadBox(int box)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (result, ok) = ReadBoxOnce(box, slow: attempt > 0);
            if (ok) return result;
            _log(attempt < 2 ? $"  box {box + 1}: the cursor didn't end on the last Pokémon - a press was lost; reading the box again, slower."
                             : $"  box {box + 1}: still not sure every Pokémon was read once - its Pokémon stay where they are this time.");
            if (attempt == 2)
                return result.Select(r => (r.Item1, r.Item2 == null ? null : new HomeMon { Unread = true, Species = "(unsure)" })).ToList();
        }
        return Array.Empty<(int, HomeMon?)>();
    }

    private (List<(int, HomeMon?)> Result, bool Ok) ReadBoxOnce(int box, bool slow)
    {
        GoToBox(box);
        GoToSlot(0, 0);
        var f = Frame() ?? throw Fail("No picture from the capture card.");
        var occupied = HomeVision.OccupiedSlots(f);
        var result = new List<(int, HomeMon?)>();
        for (var i = 0; i < Slot.PerBox; i++) if (!occupied[i]) result.Add((box * Slot.PerBox + i, null));
        var mons = Enumerable.Range(0, Slot.PerBox).Where(i => occupied[i]).ToList();
        _log($"Box {box + 1}: {mons.Count} Pokémon, {Slot.PerBox - mons.Count} empty.");
        if (mons.Count == 0) return (result, true);

        // the box icons tell forms apart (HOME's summary doesn't): take them with the cursor on the box name, so no name
        // tag or arrow covers the grid
        Press(Btn.Up);
        var icons = Frame() ?? f;
        _cursor = (box, -1, -1);

        if (!OpenSummaryAt(mons[0])) throw Fail($"The summary of box {box + 1}'s first Pokémon didn't open.");
        byte[]? whole = null, neighbours = null;
        for (var k = 0; k < mons.Count; k++)
        {
            var moved = true;
            if (k > 0)
            {
                Press(Btn.R, slow ? 700 : 300);
                moved = WaitNext(whole, neighbours);
            }
            HomeMon? mon = null;
            for (var tries = 0; tries < 3 && mon == null; tries++)
            {
                var shot = Frame();
                if (shot == null) continue;
                mon = HomeVision.ReadSummary(shot, _o.HomeLanguage, _o.OtNameLanguage);
                if (Environment.GetEnvironmentVariable("SHINYFORGE_HOME_SHOTS") == "1" && _cam.LastJpeg() is { } jpg)
                    File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(OutputDir, "shots")).FullName, $"box{box + 1}-slot{mons[k] + 1}.jpg"), jpg);
                whole = HomeVision.Fingerprint(shot, SummaryWhole);
                neighbours = HomeVision.SummaryNeighbours(shot);
            }
            if (mon == null)
            {
                // don't stop a long run for one Pokémon: it stays where it is and is never moved
                var shot = Fail($"The summary of box {box + 1} slot {mons[k] + 1} couldn't be read").Message;
                _log($"  {Slot.FromIndex(box * Slot.PerBox + mons[k])}: couldn't be read - it stays where it is ({shot}).");
                Unreadable++;
                mon = new HomeMon { Unread = true, Species = "(unread)" };
            }
            else IdentifyForm(mon, icons, mons[k]);
            // two Pokémon that look alike are still two: only when every detail (stats, nature, ability, moves, ID) is the same
            // as the last one could it be the same summary again - the end-of-box check settles that
            var prevMon = result.LastOrDefault(r => r.Item2 != null).Item2;
            if (!moved && prevMon != null && !mon.Unread && prevMon.Identity == mon.Identity)
                _log("  (this summary shows exactly the same Pokémon as the last one, down to its stats and moves - a lost R or a perfect twin; the end-of-box check decides)");
            mon.Excluded = _o.IsExcluded(mon);
            _log($"  {Slot.FromIndex(box * Slot.PerBox + mons[k])}: {mon}{(mon.Excluded ? mon.Unread ? "  [unread: stays put]" : mon.FromGo && _o.ExcludeGo ? "  [excluded: Pokémon GO]" : "  [excluded by markings]" : "")}" +
                 (mon.Details.Length > 0 ? $"\n      {mon.Details}" : ""));
            result.Add((box * Slot.PerBox + mons[k], mon));
        }
        Press(Btn.B, 400);
        var back = Need(HomeVision.IsBoxView, "The box view (after the summaries)");
        // the summaries can run on into the next boxes (HOME's R doesn't stop at the box's end): the box must be the same
        if (HomeVision.ReadBoxCounter(back) is { } after && after.Box != box)
        {
            _log($"  the summaries ran on to box {after.Box + 1} - reading box {box + 1} again.");
            _cursor = (after.Box, -1, -1);
            return (result, false);
        }
        // HOME leaves the cursor on the last Pokémon shown: it must be the box's last Pokémon, else a press got lost
        var c = HomeVision.FindCursor(back);
        _cursor = c is { } cc ? (box, cc.Row, cc.Col) : (box, -1, -1);
        if (c == null) return (result, true); // can't tell - nothing pointed at a problem while reading
        var last = mons[^1];
        var ok = c.Value.Row * Slot.Cols + c.Value.Col == last;
        if (ok) _log($"  checked: the cursor ended on the box's last Pokémon (row {c.Value.Row + 1}, column {c.Value.Col + 1}) - all {mons.Count} read.");
        return (result, ok);
    }

    /// <summary>
    /// Which form the Pokémon is, from its icon in the box view, against Pokémon Automation's HOME sprites of its species.
    /// Only a clear win counts as sure; a look-alike that's too close stays "unsure" and won't fill a slot for a particular form.
    /// </summary>
    private static void IdentifyForm(HomeMon mon, RgbImage boxPicture, int slotInBox)
    {
        // the G-Max icon test can be fooled by pink colours: only species that can Gigantamax keep it
        var forms = HomeVision.References.FormsOf(HomeVision.References.SpeciesSlug(mon.Dex));
        if (mon.Gigantamax && forms.Count > 0 && !forms.Any(f => f.Contains("-gmax"))) mon.Gigantamax = false;
        if (HomeVision.References.Form(HomeVision.SlotIcon(boxPicture, slotInBox / Slot.Cols, slotInBox % Slot.Cols), mon.Dex, mon.Shiny, mon.Gender) is not { } f) return;
        mon.Form = f.Slug.EndsWith("-shiny") ? f.Slug[..^6] : f.Slug;
        mon.FormSure = f.Margin >= 6;
    }

    /// <summary>The stats, moves and OT part of the summary: differs between two Pokémon even of the same species.</summary>
    private static readonly HomeVision.Box SummaryWhole = new(0.02, 0.2, 0.96, 0.55);

    /// <summary>
    /// After R on a summary: waits until it shows the next Pokémon (its stats/moves/OT, or the previous/next icons at the top,
    /// changed) and has stopped sliding. False = nothing changed (an identical twin, or a lost press - the end-of-box check
    /// tells which).
    /// </summary>
    private bool WaitNext(byte[]? whole, byte[]? neighbours)
    {
        byte[]? a = null;
        var changed = whole == null;
        var until = DateTime.UtcNow.AddSeconds(6); // some summaries slide in slowly
        while (DateTime.UtcNow < until)
        {
            if (Frame() is not { } f) continue;
            var w = HomeVision.Fingerprint(f, SummaryWhole);
            if (!changed && (HomeVision.Difference(whole!, w) > 3 || neighbours != null && HomeVision.CounterChanged(neighbours, HomeVision.SummaryNeighbours(f))))
                changed = true;
            if (changed && a != null && HomeVision.Difference(a, w) < 1.5) return true;
            a = w;
            Thread.Sleep(60);
        }
        return changed;
    }

    /// <summary>
    /// A → (menu) → Down → A = "Check Summary" on the given slot. When the menu isn't ready the Down moves the cursor
    /// instead (Pokémon Automation's failure, and how a summary once opened on the wrong Pokémon), so before every try the
    /// cursor is put back on the slot, ShinyForge waits up to 3 s for the summary, and the summary must show the Pokémon
    /// the box view showed there. Anything else: back out with B (never moves or releases anything) and try again.
    /// </summary>
    private bool OpenSummaryAt(int slotInBox)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            GoToSlot(slotInBox / Slot.Cols, slotInBox % Slot.Cols);
            var expect = Frame() is { } fv ? HomeVision.BarSpecies(fv) : "";
            var wait = _o.GameDelayMs + 300 + 300 * attempt;
            Press(Btn.A, wait);
            Press(Btn.Down, wait);
            Press(Btn.A, 200);
            RgbImage? sum = null;
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until && sum == null)
            {
                if (Frame() is { } f && HomeVision.IsSummary(f)) sum = f;
                else Thread.Sleep(120);
            }
            if (sum != null)
            {
                var seen = HomeVision.SummarySpecies(sum);
                if (expect.Length == 0 || seen.Length == 0 || SameName(seen, expect)) return true;
                _log($"  the summary shows {seen}, but the slot holds {expect} - backing out and trying again.");
            }
            else _log($"  the summary didn't open (try {attempt + 1}) - backing out.");
            for (var i = 0; i < 6; i++)
            {
                Press(Btn.B, 350);
                if (Frame() is { } f && HomeVision.IsBoxView(f) && !HomeVision.IsSummary(f)) break;
            }
            _cursor = (_cursor.Box, -1, -1); // a lost Down may have moved it: look again
        }
        return false;
    }

    // ================================================================ swapping
    /// <summary>
    /// Y on a Pokémon, Y on the destination: HOME swaps them (or moves into an empty slot). The press always starts on the
    /// slot that holds a Pokémon. With "check every move" the info bar must then show the Pokémon that was supposed to land.
    /// </summary>
    private void DoSwap(SwapStep step, Dictionary<int, HomeMon?> slots)
    {
        var (from, to) = slots.GetValueOrDefault(step.From) != null ? (step.From, step.To) : (step.To, step.From);
        var moving = slots.GetValueOrDefault(from);
        if (moving == null) return; // both empty - nothing to do
        if (moving.Excluded && _o.Marked == MarkedBehaviour.StayPut) throw new InvalidOperationException("Refusing to move a marked Pokémon.");
        GoTo(from);
        if (_o.VerifyMoves) CheckBar(moving, Slot.FromIndex(from), "before the swap");
        Press(Btn.Y, _o.GameDelayMs + 240);
        GoTo(to);
        // the info bar only says WHICH Pokémon is under the cursor, not WHERE: a misread cursor while carrying once dropped
        // Pokémon in the wrong slots and the check still passed. The glove must be on the target slot before it's dropped.
        var target = Slot.FromIndex(to);
        var glove = GloveAt();
        if (glove is { } g && (g.Row != target.Row || g.Col != target.Col))
        {
            _log($"  the carried Pokémon is at row {g.Row + 1}, column {g.Col + 1}, not row {target.Row + 1}, column {target.Col + 1} - moving it again.");
            _cursor = (_cursor.Box, g.Row, g.Col);
            GoToSlot(target.Row, target.Col);
            glove = GloveAt();
            if (glove is { } g2 && (g2.Row != target.Row || g2.Col != target.Col))
                throw Fail($"The carried Pokémon couldn't be brought to {target} (it's at row {g2.Row + 1}, column {g2.Col + 1}).");
        }
        Press(Btn.Y, _o.GameDelayMs + 240);
        if (glove == null && Frame() is { } fd && HomeVision.FindCursor(fd) is { Mode: "normal" } at && (at.Row != target.Row || at.Col != target.Col))
            throw Fail($"{moving.Species} was put down at row {at.Row + 1}, column {at.Col + 1} instead of {target}. Stopped so nothing gets mixed up - " +
                       "read the boxes again (start over) before the next run.");
        (slots[from], slots[to]) = (slots.GetValueOrDefault(to), moving);
        if (_o.VerifyMoves) CheckBar(moving, Slot.FromIndex(to), "after the swap");
    }

    private void CheckBar(HomeMon expected, Slot where, string when)
    {
        if (expected.Species.Length == 0) return;
        string seen = "";
        for (var i = 0; i < 3; i++)
        {
            if (Frame(i * 150) is { } f) seen = HomeVision.BarSpecies(f);
            if (SameName(seen, expected.Species)) return;
        }
        throw Fail($"At {where} ({when}) HOME shows \"{seen}\", expected {expected.Species}. Stopped so nothing gets mixed up - " +
                   "the boxes as ShinyForge last knew them are saved; read the boxes again before the next run.");
    }

    /// <summary>Same species name, allowing two misread letters (the info bar is small).</summary>
    private static bool SameName(string a, string b)
    {
        a = new string(a.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        b = new string(b.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        if (a.Length == 0 || b.Length == 0) return false;
        if (a.Contains(b) || b.Contains(a)) return true;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length] <= 2;
    }

    // ================================================================ files
    private static void Save(string name, Dictionary<int, HomeMon?> slots)
    {
        var list = slots.OrderBy(kv => kv.Key).Select(kv => new
        {
            box = Slot.FromIndex(kv.Key).Box + 1, row = Slot.FromIndex(kv.Key).Row + 1, column = Slot.FromIndex(kv.Key).Col + 1,
            pokemon = kv.Value,
        });
        File.WriteAllText(Path.Combine(OutputDir, name), JsonSerializer.Serialize(list, HomeCatalog.Json));
    }

    /// <summary>Remembers where every Pokémon is (updated after every swap).</summary>
    private static void SaveCatalog(Dictionary<int, HomeMon?> slots)
    {
        var known = LoadCatalog() ?? new HomeCatalog();
        foreach (var (k, v) in slots) known.Slots[k] = v;
        known.Read = DateTime.Now;
        var tmp = CatalogFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(known, HomeCatalog.Json));
        File.Move(tmp, CatalogFile, true);
    }

    public static HomeCatalog? LoadCatalog()
    {
        try { return File.Exists(CatalogFile) ? JsonSerializer.Deserialize<HomeCatalog>(File.ReadAllText(CatalogFile), HomeCatalog.Json) : null; }
        catch { return null; }
    }
}
