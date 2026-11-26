using ShinyForge.Core;
using ShinyForge.Services;
using ShinyForge.Services.HomeSorter;

namespace ShinyForge.UI;

/// <summary>
/// ShinyForge's Pokémon HOME box sorter: every option of Pokémon Automation's "Box Sorter" and "Box Sorter Living Dex"
/// (same names, same defaults), plus marking exclusions, a box range, move checking and the hardware choice. Runs on an
/// unmodded Switch / Switch 2 through a PABotBase2 board (wired Pro Controller) and the capture card.
/// </summary>
public sealed class HomeSorterForm : Form
{
    private static readonly (string Tag, string Name)[] Languages =
    {
        ("en", "English"), ("ja", "Japanese"), ("es", "Spanish"), ("fr", "French"), ("de", "German"), ("it", "Italian"),
        ("ko", "Korean"), ("zh-Hans", "Chinese (Simplified)"), ("zh-Hant", "Chinese (Traditional)"),
    };

    private readonly HomeSorterOptions _o = AppSettings.Current.HomeSorter;
    private readonly TableLayoutPanel _grid = new() { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, BackColor = Theme.Bg, Padding = new Padding(8) };
    private readonly Dictionary<string, List<Control>> _groups = new();
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Theme.Panel, ForeColor = Theme.Text, Font = Theme.Mono, BorderStyle = BorderStyle.None };
    private readonly Label _status = Theme.Label("Open Pokémon HOME on the console and go to Pokémon (the boxes), then press Start.", Theme.Body, Theme.Muted, false);
    private readonly Label _past = Theme.Label("", Theme.Body, Theme.Text, false);
    private readonly FancyButton _start = new("Start", Theme.GlyphPlay, Theme.Accent);
    private readonly FancyButton _stop = new("Stop", Theme.GlyphStop, Color.FromArgb(150, 40, 60)) { Enabled = false };
    private readonly DataGridView _rules = new()
    {
        AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = true, Height = 130, Width = 420,
        BackgroundColor = Theme.PanelHi, GridColor = Theme.Border, BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
    };
    private CancellationTokenSource? _cts;
    private bool _forgetOnStop;
    private readonly Label _dexInfo = Theme.Label("", Theme.Small, Theme.Accent2);

    /// <summary>"Regular dex: boxes 2-52, then shiny dex: boxes 53-103" for the chosen style.</summary>
    private string DexInfo()
    {
        try
        {
            if (DexTemplates.PathFor(_o) is not { } path || !File.Exists(path)) return "The template isn't found (Pokémon Automation's Resources folder).";
            var one = DexTemplates.Load(path);
            var origin = "";
            if (_o.OriginMarkFilter.Length > 0)
            {
                var all = one.Count(e => !e.IsEmpty);
                one = one.Where(e => !e.IsEmpty && OriginMarks.CanHave(_o.OriginMarkFilter, e)).ToList();
                origin = $"{OriginMarks.Label(_o.OriginMarkFilter)}: {one.Count} of the {all} Pokémon in this style can have the mark" +
                         (_o.OriginMarkFilter == "go" && !OriginMarks.GoListKnown ? " (GO list not found - counting all)" : "") + ". ";
            }
            var boxes = (one.Count + Slot.PerBox - 1) / Slot.PerBox;
            var a = _o.LivingDexStartBox;
            var noFilter = new HomeSorterOptions { ShinyDex = _o.ShinyDex, ShinyAfterRegular = _o.ShinyAfterRegular }; // "one" is already filtered
            var problems = DexTemplates.BoxProblems(DexTemplates.Layout(one, noFilter).Count, _o);
            _dexInfo.ForeColor = problems.Count > 0 ? Theme.Warn : Theme.Accent2;
            var warn = problems.Count > 0 ? "\n⚠ " + string.Join("\n⚠ ", problems) : "";
            if (DexTemplates.ShinyStartBox(one, _o) is { } s)
                return $"{origin}{one.Count} Pokémon per dex = {boxes} boxes each. Regular dex: boxes {a}-{s - 1}, then shiny dex: boxes {s}-{s + boxes - 1}. Rejects: {_o.RejectBoxStart}-{_o.RejectBoxEnd}.{warn}";
            return $"{origin}{one.Count} Pokémon = {boxes} boxes. {(_o.ShinyDex ? "Shiny" : "Regular")} dex: boxes {a}-{a + boxes - 1}. Rejects: {_o.RejectBoxStart}-{_o.RejectBoxEnd}.{warn}";
        }
        catch (Exception e) { return e.Message; }
    }
    private int _row;

    public HomeSorterForm()
    {
        Text = "ShinyForge - Pokémon HOME Box Sorter";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Size = new Size(1400, 900);
        MinimumSize = new Size(1000, 600);
        StartPosition = FormStartPosition.CenterParent;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        if (!_o.ImportedFromPa && HomeSorterOptions.ImportFromPokemonAutomation(_o) is { } imported)
            _status.Text = imported;
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 440));

        BuildOptions();

        var left = new Panel { Dock = DockStyle.Left, Width = 850, AutoScroll = true, BackColor = Theme.Bg };
        left.Controls.Add(_grid);
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Theme.Bg };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 100, BackColor = Theme.Bg, WrapContents = true };
        var test = new FancyButton("Check setup", Theme.GlyphInfo, Theme.PanelHi);
        var folder = new FancyButton("Files", Theme.GlyphFolder, Theme.PanelHi);
        var rename = new FancyButton("Rename empty boxes", Theme.GlyphSync, Theme.PanelHi);
        buttons.Controls.AddRange(new Control[] { _start, _stop, test, rename, folder });
        rename.Click += async (_, _) => await RenameEmptyRun();
        foreach (Control c in buttons.Controls) c.Margin = new Padding(0, 0, 8, 0);
        _status.Dock = DockStyle.Top; _status.Height = 48;
        _past.Dock = DockStyle.Top; _past.Height = 26;
        right.Controls.Add(_log);
        right.Controls.Add(_past);
        right.Controls.Add(_status);
        right.Controls.Add(new HomeScreenView { Dock = DockStyle.Top, Height = 290 }); // what the console shows, live
        right.Controls.Add(buttons);
        Controls.Add(right);
        Controls.Add(left);
        ShowPast();
        UpdateVisibility();

        _start.Click += async (_, _) => await StartRun();
        _stop.Click += (_, _) =>
        {
            if (_cts == null) return;
            var r = MessageBox.Show(this,
                "You're stopping the run.\n\nSave what it has done so far (the boxes it read and the swaps it made), so the next Start can continue " +
                "where it left off until the sort is complete?\n\nYes = save it and continue next time\nNo = forget it - the next Start begins all over with a fresh scan\n" +
                "Cancel = keep running",
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            _forgetOnStop = r == DialogResult.No;
            _cts.Cancel();
            _status.Text = "Stopping after the current step...";
        };
        test.Click += async (_, _) => await CheckSetup();
        folder.Click += (_, _) => Browser.Open(HomeSorterRunner.OutputDir);
        FormClosing += (_, e) =>
        {
            if (_cts != null)
            {
                if (MessageBox.Show(this, "The sorter is running. Stop it and close?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { e.Cancel = true; return; }
                _cts.Cancel();
            }
            Save();
        };
    }

    // ================================================================ options
    private void BuildOptions()
    {
        Heading("Program", null);
        var rule = Theme.Label("⚠ " + HomeSorterOptions.Box1Rule, Theme.Small, Theme.Warn, false);
        rule.Width = 820; rule.Height = 36; rule.Margin = new Padding(0, 0, 0, 6);
        _grid.Controls.Add(rule, 0, _row);
        _grid.SetColumnSpan(rule, 2);
        _row++;
        var mode = Combo(new[] { "Box Sorter Living Dex", "Box Sorter" }, _o.Mode == SortMode.LivingDex ? 0 : 1, i => { _o.Mode = i == 0 ? SortMode.LivingDex : SortMode.BoxSorter; UpdateVisibility(); });
        Row("Program:", "Living dex = put one of each Pokémon in a template's order. Box Sorter = sort boxes by your own rules.", mode, null);

        // ---- Living dex (Pokémon Automation's options, in its order)
        Heading("Box Sorter Living Dex", "dex");
        Row("Living Dex Starting Box:", "Box number where the living dex placement begins (1-indexed).", Num(_o.LivingDexStartBox, 2, Slot.MaxBoxes, v => { _o.LivingDexStartBox = v; UpdateVisibility(); }), "dex");
        Row("Reject Box Range Start:", "First box of the range reserved for rejected or overflow Pokémon.", Num(_o.RejectBoxStart, 2, Slot.MaxBoxes, v => { _o.RejectBoxStart = v; UpdateVisibility(); }), "dex");
        Row("Reject Box Range End:", "Last box of the range reserved for rejected or overflow Pokémon.", Num(_o.RejectBoxEnd, 2, Slot.MaxBoxes, v => { _o.RejectBoxEnd = v; UpdateVisibility(); }), "dex");
        var styles = DexTemplates.Styles;
        var style = Combo(styles.Select(s => s.Label).ToArray(), Math.Max(0, Array.FindIndex(styles, s => s.File == _o.LivingDexStyle)),
            i => { _o.LivingDexStyle = styles[i].File; UpdateVisibility(); });
        Row("Living Dex Style:", "The templates Pokémon Automation ships (read from its Resources folder). See its wiki for each style.", style, "dex");
        var custom = new TextBox { Text = _o.CustomDexFile, Width = 340 };
        Theme.StyleInput(custom);
        custom.TextChanged += (_, _) => _o.CustomDexFile = custom.Text;
        var browse = new Button { Text = "...", Width = 40, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text };
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "JSON Files (*.json)|*.json", Title = "Select a living dex JSON file..." };
            if (d.ShowDialog(this) == DialogResult.OK) custom.Text = d.FileName;
        };
        Row("Custom Living Dex File:", "A JSON template in the same format as the files in Resources/PokemonHome/DexTemplates.", Flow(custom, browse), "custom");
        Row("Shiny Dex:", "Enable or disable shiny dex mode.", Check(_o.ShinyDex, v => { _o.ShinyDex = v; UpdateVisibility(); }), "dex");
        Row("Shiny dex placement:", "After the regular dex = first complete the regular living dex, then start the shiny dex in the box right after " +
            "the regular dex's last box (regular ends in box 84, shiny starts in box 85). Shiny only = only shinies, like Pokémon Automation.",
            Combo(new[] { "After the regular dex (next box)", "Shiny only" }, _o.ShinyAfterRegular ? 0 : 1, i => { _o.ShinyAfterRegular = i == 0; UpdateVisibility(); }), "shiny");
        var marks = OriginMarks.All;
        Row("Origin mark:", "Build the living dex from Pokémon with this origin mark only. Only Pokémon that can have the mark get a slot " +
            "(so it needs fewer boxes - see Where it goes); Pokémon with any other origin are moved to the reject boxes.",
            Combo(marks.Select(m => m.Label).ToArray(), Math.Max(0, Array.FindIndex(marks, m => m.Mark == _o.OriginMarkFilter)),
                i => { _o.OriginMarkFilter = marks[i].Mark; UpdateVisibility(); }), "dex");
        Row("Search all boxes for missing Pokémon:", "When a living dex slot has no fitting Pokémon in the dex and reject boxes, read every other box " +
            "in HOME (all 300 if needed) for one before leaving it empty. Boxes it remembers holding a match go first; it stops when everything is found.",
            Check(_o.SearchAllBoxes, v => _o.SearchAllBoxes = v), "dex");
        _dexInfo.AutoSize = false; _dexInfo.Width = 440; _dexInfo.Height = 110;
        Row("Where it goes:", "", _dexInfo, "dex");
        var otLangs = new[] { "None" }.Concat(Languages.Select(l => l.Name)).ToArray();
        var otLang = Combo(otLangs, _o.OtNameLanguage.Length == 0 ? 0 : 1 + Math.Max(0, Array.FindIndex(Languages, l => l.Tag == _o.OtNameLanguage)),
            i => { _o.OtNameLanguage = i == 0 ? "" : Languages[i - 1].Tag; UpdateVisibility(); });
        Row("OT Name Language:", "Language of the OT name you are filtering for. If set to None, OT names will not be read.", otLang, "dex");
        var ot = new TextBox { Text = _o.OtName, Width = 380, PlaceholderText = "Enter OT name here..." };
        Theme.StyleInput(ot);
        ot.TextChanged += (_, _) => _o.OtName = ot.Text;
        Row("OT Name Filter:", "Only keep Pokémon with this OT name in the living dex. Leave blank to accept any OT name.", ot, "ot");

        // ---- Box Sorter (Pokémon Automation's options + the first box)
        Heading("Box Sorter", "sort");
        Row("First Box to Sort:", "ShinyForge goes to this box by itself (Pokémon Automation starts at the box HOME shows).", Num(_o.SortStartBox, 2, Slot.MaxBoxes, v => _o.SortStartBox = v), "sort");
        Row("Number of Boxes to Sort:", "", Num(_o.BoxCount, 1, Slot.MaxBoxes, v => _o.BoxCount = v), "sort");
        Row("Home Language:", "The language HOME shows (natures, abilities and names are read in it).",
            Combo(Languages.Select(l => l.Name).ToArray(), Math.Max(0, Array.FindIndex(Languages, l => l.Tag == _o.HomeLanguage)), i => _o.HomeLanguage = Languages[i].Tag), "sort");
        BuildRules();
        Row("Sort Order Rules:", "Sort order rules will be applied top to bottom.", _rules, "sort");

        // ---- shared setup (Pokémon Automation's)
        Heading("Setup", null);
        Row("Capture Card Delay:", "milliseconds - how late the capture card shows what the console does.", Num(_o.VideoDelayMs, 0, 5000, v => _o.VideoDelayMs = v), null);
        Row("Pokémon Home App Delay:", "milliseconds - the wait after each button press. Raise it if HOME misses presses.", Num(_o.GameDelayMs, 0, 5000, v => _o.GameDelayMs = v), null);
        var output = new TextBox { Text = _o.OutputFile, Width = 380 };
        Theme.StyleInput(output);
        output.TextChanged += (_, _) => _o.OutputFile = string.Concat(output.Text.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        Row("Output File:", "JSON file basename to catalogue box info found by the program.", output, null);
        Row("Dry Run:", "Catalogue and make the sorting plan without moving anything. Check <output file>.json, -sorted.json and -plan.txt.", Check(_o.DryRun, v => _o.DryRun = v), null);
        Row("Notify when finished:", "Phone / Discord notification (ShinyForge's notifications in Settings).", Check(_o.NotifyWhenFinished, v => _o.NotifyWhenFinished = v), null);

        // ---- ShinyForge: markings
        Heading("Exclude Pokémon GO Pokémon (ShinyForge)", null);
        Row("Exclude Pokémon GO Pokémon:", "Leave out Pokémon that came from Pokémon GO (the GO origin mark on the summary). They're treated like excluded marked Pokémon below.",
            Check(_o.ExcludeGo, v => _o.ExcludeGo = v), null);

        Heading("Exclude by markings (ShinyForge)", null);
        Row("", "Pokémon with these markings are left alone. Set each marking to the colour that should exclude a Pokémon.", new Label { Width = 1 }, null);
        for (var i = 0; i < 6; i++)
        {
            var k = i;
            Row($"{Marks.Glyphs[i]}  {Marks.Names[i]}:", "",
                Combo(new[] { "Not used", "Blue", "Red", "Either colour" }, (int)_o.MarkRules[i], v => _o.MarkRules[k] = (MarkRule)v), null);
        }
        Row("Must have all of them:", "On = only Pokémon with ALL the markings set above are excluded. Off = any one of them is enough.", Check(_o.MarksMustAllMatch, v => _o.MarksMustAllMatch = v), null);
        Row("Excluded Pokémon:", "Marked and (if set) Pokémon GO Pokémon.", Combo(new[] { "Stay in their slot (never moved)", "Aren't used, may be moved to the reject boxes" }, (int)_o.Marked, v => _o.Marked = (MarkedBehaviour)v), null);

        // ---- ShinyForge: box names
        Heading("Box names (ShinyForge)", null);
        Row("Rename empty boxes:", "After sorting, every empty box gets the name \"HOME n\" (n = its box number), e.g. an empty box 10 becomes \"HOME 10\". " +
            "Only boxes with a different name are renamed. The name is typed on the Switch keyboard and checked before it's confirmed.",
            Check(_o.RenameEmptyBoxes, v => _o.RenameEmptyBoxes = v), null);
        Row("Name living dex boxes:", "After sorting, name each living dex box after the dex numbers in it, like \"1-30\" or \"Shiny 31-60\".",
            Check(_o.NameDexBoxes, v => _o.NameDexBoxes = v), "dex");

        // ---- ShinyForge: smarter
        Heading("Smarter sorting (ShinyForge)", null);
        Row("Check every move:", "After each swap, read HOME's info bar to make sure the right Pokémon landed. Stops at the first surprise.", Check(_o.VerifyMoves, v => _o.VerifyMoves = v), null);
        Row("Pick the best duplicate:", "Living dex: among several fitting Pokémon prefer your OT name, then the lowest level.", Check(_o.PickBestDuplicate, v => _o.PickBestDuplicate = v), "dex");

        // ---- hardware
        Heading("Hardware", null);
        var ports = new[] { "Find it" }.Concat(PaBotBase2.Ports()).ToArray();
        Row("Controller board port:", "ESP32-S3 / Pico W with Pokémon Automation's PABotBase2 firmware, plugged into the console as a wired Pro Controller.",
            Combo(ports, Math.Max(0, Array.IndexOf(ports, _o.ComPort)), i => _o.ComPort = i == 0 ? "" : ports[i]), null);
        var cams = new[] { "Find it" }.Concat(Equipment.VideoSources()).ToArray();
        Row("Capture card:", "The card that shows the console running HOME.",
            Combo(cams, Math.Max(0, Array.IndexOf(cams, _o.Device)), i => _o.Device = i == 0 ? "" : cams[i]), null);
        Row("Keep the console awake:", "The board nudges the right stick a little every few minutes so the Switch / Switch 2 doesn't dim or sleep " +
            "(changes nothing in HOME; in a game the camera may move a tiny bit). Runs while ShinyForge is open.",
            Check(_o.KeepAwake, v => { _o.KeepAwake = v; AppSettings.Current.Save(); ConsoleKeepAwake.Apply(); }), null);
        Row("Nudge every (minutes):", "Shorter than the console's dimming time (a few minutes).",
            Num(_o.KeepAwakeMinutes, 1, 30, v => { _o.KeepAwakeMinutes = v; AppSettings.Current.Save(); ConsoleKeepAwake.Apply(); }), null);
    }

    private void BuildRules()
    {
        var names = Enum.GetValues<SortCriterion>().Select(Label).ToArray();
        var crit = new DataGridViewComboBoxColumn { HeaderText = "Criteria", DataSource = names, FlatStyle = FlatStyle.Flat, FillWeight = 75 };
        var rev = new DataGridViewCheckBoxColumn { HeaderText = "Reverse", FillWeight = 25 };
        _rules.Columns.Add(crit);
        _rules.Columns.Add(rev);
        _rules.DefaultCellStyle.BackColor = Theme.PanelHi;
        _rules.DefaultCellStyle.ForeColor = Theme.Text;
        _rules.DefaultCellStyle.SelectionBackColor = Theme.Accent;
        _rules.ColumnHeadersDefaultCellStyle.BackColor = Theme.Panel;
        _rules.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
        _rules.RowHeadersDefaultCellStyle.BackColor = Theme.Panel;
        foreach (var r in _o.Rules) _rules.Rows.Add(Label(r.Criterion), r.Reverse);
        _rules.DefaultValuesNeeded += (_, e) => { e.Row.Cells[0].Value = names[0]; e.Row.Cells[1].Value = false; };
    }

    private static string Label(SortCriterion c) => c switch
    {
        SortCriterion.DexNumber => "Dex Number",
        SortCriterion.Ball => "Ball Type",
        SortCriterion.Gender => "Gender (Male, Female, Genderless)",
        SortCriterion.OriginMark => "Origin Mark",
        SortCriterion.LanguageOfOrigin => "Language of Origin",
        SortCriterion.Marked => "Has markings (ShinyForge)",
        SortCriterion.OtName => "OT Name (ShinyForge)",
        _ => c.ToString(),
    };

    private void ReadRules()
    {
        var all = Enum.GetValues<SortCriterion>();
        _o.Rules = _rules.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && r.Cells[0].Value is string)
            .Select(r => new SortRule(all.First(c => Label(c) == r.Cells[0].Value as string), r.Cells[1].Value is true)).ToList();
    }

    private void Heading(string text, string? group)
    {
        var l = Theme.Label(text, Theme.H2, Theme.Accent2);
        l.Margin = new Padding(0, 14, 0, 4);
        _grid.Controls.Add(l, 0, _row);
        _grid.SetColumnSpan(l, 2);
        Track(group, l);
        _row++;
    }

    private void Row(string title, string description, Control input, string? group)
    {
        var label = new Label
        {
            Text = title + (description.Length > 0 ? "\n" + description : ""), AutoSize = false, Width = 370,
            Height = description.Length == 0 ? 28 : 28 + 15 * (int)Math.Ceiling(description.Length / 58.0), ForeColor = Theme.Text, Font = Theme.Small, UseMnemonic = false,
        };
        input.Margin = new Padding(3, 6, 3, 6);
        _grid.Controls.Add(label, 0, _row);
        _grid.Controls.Add(input, 1, _row);
        Track(group, label, input);
        _row++;
    }

    private void Track(string? group, params Control[] cs)
    {
        if (group == null) return;
        if (!_groups.TryGetValue(group, out var list)) _groups[group] = list = new();
        list.AddRange(cs);
    }

    private void UpdateVisibility()
    {
        void Show(string g, bool on) { if (_groups.TryGetValue(g, out var l)) foreach (var c in l) c.Visible = on; }
        var dex = _o.Mode == SortMode.LivingDex;
        Show("dex", dex);
        Show("sort", !dex);
        Show("custom", dex && _o.LivingDexStyle == "custom");
        Show("ot", dex && _o.OtNameLanguage.Length > 0);
        Show("shiny", dex && _o.ShinyDex);
        _dexInfo.Text = DexInfo();
    }

    private static ComboBox Combo(string[] items, int selected, Action<int> changed)
    {
        var c = new ComboBox { Width = 380 };
        Theme.StyleInput(c);
        c.Items.AddRange(items);
        c.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1);
        c.SelectedIndexChanged += (_, _) => changed(c.SelectedIndex);
        return c;
    }

    private static NumericUpDown Num(int value, int min, int max, Action<int> changed)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 120 };
        Theme.StyleInput(n);
        n.ValueChanged += (_, _) => changed((int)n.Value);
        return n;
    }

    private static CheckBox Check(bool value, Action<bool> changed)
    {
        var c = new CheckBox { Checked = value, AutoSize = true, ForeColor = Theme.Text };
        c.CheckedChanged += (_, _) => changed(c.Checked);
        return c;
    }

    private static FlowLayoutPanel Flow(params Control[] cs)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Bg };
        f.Controls.AddRange(cs);
        return f;
    }

    private void Save()
    {
        ReadRules();
        AppSettings.Current.Save();
    }

    private void ShowPast() => _past.Text = $"Past Runs - Pokémon: {_o.PastPokemon:N0} - Empty Slots: {_o.PastEmpty:N0} - Swaps: {_o.PastSwaps:N0}";

    private void Log(string s)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { OnUi(() => Log(s)); return; }
        _log.AppendText(s + Environment.NewLine);
    }

    /// <summary>Runs <paramref name="a"/> on the window's thread - quietly skipped once the window is closed (the sorter
    /// keeps logging while it stops, and BeginInvoke on a closed window threw inside the sorter's clean-up).</summary>
    private void OnUi(Action a)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(a); }
        catch (InvalidOperationException) { }
    }

    // ================================================================ running
    private async Task StartRun()
    {
        Save();
        if (!_o.DryRun && _o.Mode == SortMode.LivingDex &&
            MessageBox.Show(this,
                $"The living dex goes in boxes {_o.LivingDexStartBox} and on; Pokémon that don't fit go to boxes {_o.RejectBoxStart}-{_o.RejectBoxEnd}. " +
                "Pokémon in those boxes will be moved.\n\nTip: try Dry Run first to see the plan.\n\nStart sorting?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        // a stopped run with these settings: continue it, or start over with a fresh scan
        var resume = false;
        if (HomeSession.Resumable(_o) is { } saved)
        {
            var what = saved.FromDryRun && saved.ScanDone
                ? $"A dry run on {saved.Updated:g} read all {saved.ReadBoxes.Count(b => b != 0)} box(es) with these settings.\n\n" +
                  "Yes = use that scan and skip reading the boxes again (every swap still checks the Pokémon on screen first)\n"
                : $"A run started {saved.Started:g} was stopped before it finished: {saved.ReadBoxes.Count(b => b != 0)} box(es) read" +
                  (saved.SwapsDone > 0 ? $", {saved.SwapsDone} swap(s) done" : "") + ".\n\n" +
                  "Yes = continue where it left off (the boxes it read come from memory)\n";
            var r = MessageBox.Show(this, what + "No = start all over with a fresh scan\nCancel = don't start",
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            resume = r == DialogResult.Yes;
            if (!resume) HomeSession.Forget();
        }
        else if (HomeSession.Load() is { Finished: false } other && other.Key != HomeSession.KeyFor(_o))
        {
            MessageBox.Show(this, "A stopped run was saved with different settings, so it can't be continued - this run starts with a fresh scan.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            HomeSession.Forget();
        }
        _forgetOnStop = false;
        _cts = new CancellationTokenSource();
        _start.Enabled = false;
        _stop.Enabled = true;
        _log.Clear();
        var runner = new HomeSorterRunner(_o, Log, _cts.Token, resume);
        runner.Progress += s => OnUi(() => _status.Text = s);
        int pokemon = 0, empty = 0, swaps = 0;
        runner.Stats += (p, e, s) => { pokemon = p; empty = e; swaps = s; OnUi(() => _past.Text = $"This run - Pokémon: {p:N0} - Empty Slots: {e:N0} - Swaps: {s:N0}"); };
        string result;
        try
        {
            var plan = await runner.RunAsync();
            result = _o.DryRun ? $"Dry run finished: {plan?.Steps.Count} swaps planned." : $"Sorting finished: {runner.Swaps} swaps.";
        }
        catch (OperationCanceledException)
        {
            if (_forgetOnStop) { HomeSession.Forget(); result = $"Stopped after {runner.Swaps} swaps. The next Start begins with a fresh scan."; }
            else result = $"Stopped after {runner.Swaps} swaps. Saved - the next Start can continue where it left off.";
        }
        catch (Exception e)
        {
            result = "Stopped: " + e.Message;
            Log(e.ToString());
            if (!IsDisposed) HomeSorterReport.Offer(this, e.Message, runner.LogFile); // log kept; ask to send it to GitHub
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            if (!IsDisposed) { _start.Enabled = true; _stop.Enabled = false; }
        }
        _o.PastPokemon += pokemon; _o.PastEmpty += empty; _o.PastSwaps += swaps;
        AppSettings.Current.Save();
        if (IsDisposed) return;
        _status.Text = result;
        Log(result);
        ShowPast();
        if (_o.NotifyWhenFinished) NotifyService.Send("Pokémon HOME sorter", result);
    }

    /// <summary>Renames every empty box in HOME (all boxes) to "HOME n" - nothing is sorted or moved.</summary>
    private async Task RenameEmptyRun()
    {
        Save();
        if (MessageBox.Show(this, "Go through all of HOME's boxes and rename every EMPTY box to \"HOME n\" (its box number)?\n\n" +
                "Nothing is moved. Boxes that already have that name are skipped. It takes a few seconds per box.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _cts = new CancellationTokenSource();
        _start.Enabled = false;
        _stop.Enabled = true;
        _log.Clear();
        var runner = new HomeSorterRunner(_o, Log, _cts.Token);
        runner.Progress += s => OnUi(() => _status.Text = s);
        string result;
        try { result = $"Renamed {await runner.RenameEmptyBoxes(0, HomeSortPlanner.Boxes - 1)} empty box(es)."; }
        catch (OperationCanceledException) { result = "Stopped."; }
        catch (Exception e)
        {
            result = "Stopped: " + e.Message;
            if (!IsDisposed) HomeSorterReport.Offer(this, e.Message, runner.LogFile);
        }
        finally { _cts.Dispose(); _cts = null; if (!IsDisposed) { _start.Enabled = true; _stop.Enabled = false; } }
        if (IsDisposed) return;
        _status.Text = result;
        Log(result);
    }

    /// <summary>Connects to the board and the capture card and shows what ShinyForge sees - presses no buttons.</summary>
    private async Task CheckSetup()
    {
        Save();
        _log.Clear();
        Log("Checking the setup (no buttons are pressed)...");
        await Task.Run(() =>
        {
            if (!ConsoleKeepAwake.BoardGate.Wait(TimeSpan.FromSeconds(20))) { Log("Controller: busy (the sorter or keep-awake is using it)."); return; }
            try
            {
                var port = _o.ComPort.Length > 0 ? _o.ComPort : PokeldnService.FindBoards().Select(b => b.Port).FirstOrDefault();
                if (port == null) Log("Controller: no ESP32 / Pico board found on a COM port.");
                else
                {
                    using var pad = PaBotBase2.Connect(port, Log);
                    Log($"Controller: {pad.DeviceName} on {port}, firmware {pad.FirmwareVersion}, " +
                        $"mode {(pad.ControllerMode == PaBotBase2.WiredProController ? "wired Pro Controller" : "0x" + pad.ControllerMode.ToString("x"))}, " +
                        $"console {(pad.ConsoleConnected() switch { true => "connected", false => "NOT connected", _ => "unknown" })}.");
                }
            }
            catch (Exception e) { Log("Controller: " + e.Message); }
            finally { ConsoleKeepAwake.BoardGate.Release(); }
            try
            {
                var device = _o.Device.Length > 0 ? _o.Device : HomeCapture.DefaultDevice();
                if (device == null) { Log("Capture card: none found."); return; }
                using var cam = HomeCapture.Start(device);
                var f = cam.Grab(_o.VideoDelayMs, 15000);
                if (f == null) { Log(cam.NoPictureReason()); return; }
                Log($"Capture card \"{device}\": {f.Width}x{f.Height}.");
                var counter = HomeVision.ReadBoxCounter(f);
                var cursor = HomeVision.FindCursor(f);
                Log(counter is { } c ? $"HOME box view: box {c.Box + 1} of {c.Total}."
                    : HomeVision.IsDimmed(f) ? "The screen looks dimmed (no input for a while) or HOME isn't on the boxes - press any button on the console and check again. A sort wakes it by itself."
                    : "The box counter (like 12/300) isn't visible - is HOME on the Pokémon boxes?");
                if (cursor is { } k) Log($"Cursor: row {k.Row + 1}, column {k.Col + 1} ({k.Mode} mode). Info bar: {HomeVision.BarSpecies(f)} {string.Concat(HomeVision.BarMarks(f).Select((m, i) => m == MarkColor.None ? "" : $"{m} {Marks.Names[i]} "))}");
                if (HomeVision.ReadSummary(f, _o.HomeLanguage, _o.OtNameLanguage.Length > 0 ? _o.OtNameLanguage : "en") is { } mon) Log("Summary screen: " + mon);
                Log(HomeVision.References.Available ? "Pokémon Automation's ball / type pictures: found." : "Pokémon Automation's ball / type pictures: NOT found (set its folder in Settings).");
                Log(DexTemplates.ResourceFolder() != null ? "Living dex templates: found." : "Living dex templates: NOT found (Pokémon Automation's folder).");
                if (WinOcr.Languages() is { } langs) Log("Text reading languages on this PC: " + string.Join(", ", langs));
            }
            catch (Exception e) { Log("Capture card: " + e.Message); }
        });
    }
}
