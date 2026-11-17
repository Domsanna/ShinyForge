using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// Hunt options: stop rules (natures / IVs / species, not only shinies), OPTIONAL auto-catch and OPTIONAL shiny RNG
/// manipulation. Auto-catch and RNG are off unless the user turns them on here.
/// </summary>
public sealed class HuntOptionsForm : Form
{
    private static readonly string[] Natures = { "Hardy", "Lonely", "Brave", "Adamant", "Naughty", "Bold", "Docile", "Relaxed", "Impish", "Lax",
        "Timid", "Hasty", "Serious", "Jolly", "Naive", "Modest", "Mild", "Quiet", "Bashful", "Rash", "Calm", "Gentle", "Sassy", "Careful", "Quirky" };
    private static readonly string[] Stats = { "HP", "Atk", "Def", "SpA", "SpD", "Spe" };

    private readonly HuntOptions _o = AppSettings.Current.Hunt;
    private readonly bool _rngAvailable;

    /// <param name="game">The game whose stop targets are edited.</param>
    /// <param name="targetsOnly">SysBot.exe games: only the stop targets (SysBot has its own other settings).</param>
    public HuntOptionsForm(Game game, bool rngAvailable, bool targetsOnly = false)
    {
        _rngAvailable = rngAvailable;
        Text = "ShinyForge - hunt options";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, 860);
        MinimumSize = new Size(700, 500);

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16) };
        Controls.Add(body);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 8, 12, 8), BackColor = Theme.Panel };
        var save = new FancyButton("Save", Theme.GlyphCheck, Theme.Accent);
        var cancel = new FancyButton("Cancel", "", Theme.PanelHi);
        bottom.Controls.AddRange(new Control[] { save, cancel });
        Controls.Add(bottom);
        cancel.Click += (_, _) => Close();

        Label H(string t) => new() { Text = t, Font = Theme.H2, ForeColor = Theme.Gold, AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
        Label P(string t, Color? c = null) => new() { Text = t, ForeColor = c ?? Theme.Muted, AutoSize = true, MaximumSize = new Size(740, 0), Margin = new Padding(0, 2, 0, 6) };
        NumericUpDown Num(int min, int max, int val, int w = 70) { var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max), Width = w }; Theme.StyleInput(n); return n; }
        FlowLayoutPanel Row(params Control[] c) { var r = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(760, 0), Margin = new Padding(0, 2, 0, 2) }; r.Controls.AddRange(c); return r; }
        Label L(string t) => new() { Text = t, AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 6, 6, 0) };
        CheckedListBox NatureList(IEnumerable<string> selected)
        {
            var box = new CheckedListBox { MultiColumn = true, ColumnWidth = 140, Width = 740, Height = 120, CheckOnClick = true, BackColor = Theme.PanelHi, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
            foreach (var n in Natures) box.Items.Add(n, selected.Contains(n, StringComparer.OrdinalIgnoreCase));
            return box;
        }
        Dictionary<string, NumericUpDown> IvRow(Dictionary<string, int> values, FlowLayoutPanel into)
        {
            var d = new Dictionary<string, NumericUpDown>();
            foreach (var s in Stats)
            {
                var n = Num(0, 31, values.TryGetValue(s, out var v) ? v : 0, 56);
                d[s] = n;
                into.Controls.Add(L(s)); into.Controls.Add(n);
            }
            return d;
        }

        // ---------------------------------------------------------------- stop targets (this game)
        var gs = AppSettings.Current.For(game.Id);
        var available = StopTargets.Available(game);
        var targets = gs.StopTargets.Select(t => new StopTarget { Species = t.Species, Form = t.Form, Gender = t.Gender, Shiny = t.Shiny }).ToList();
        if (targets.Count == 0 && _o.StopSpecies.Trim().Length > 0) // the old "species, comma separated" box → targets
            foreach (var n in _o.StopSpecies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (available.FirstOrDefault(o => o.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } hit)
                    targets.Add(new StopTarget { Species = hit.Species });
        body.Controls.Add(H($"Stop targets - Pokémon {game.Title}"));
        body.Controls.Add(P("Stop when one of these shows up: any Pokémon you can get in this game, with its form, gender and whether it must " +
                            "be shiny. The bot still stops on EVERY shiny on top of these." +
                            (targetsOnly ? " SysBot.exe holds one target, so it uses the first one in the list." : "")));
        var targetList = new ListBox { Width = 740, Height = 110, BackColor = Theme.PanelHi, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
        void ShowTargets() { targetList.Items.Clear(); foreach (var t in targets) targetList.Items.Add(StopTargets.Describe(game, t)); }
        ShowTargets();
        body.Controls.Add(targetList);
        ComboBox Combo(int w, bool editable = false)
        {
            var c = new ComboBox { Width = w };
            Theme.StyleInput(c);
            if (editable) c.DropDownStyle = ComboBoxStyle.DropDown; // typing a name: after the theme (it sets a pick-only list)
            return c;
        }
        var pick = Combo(230, editable: true);
        pick.Items.AddRange(available.Select(o => (object)$"#{o.Species:000} {o.Name}").ToArray());
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) // Windows' autocomplete only works there (ShinyForge's UI is STA)
        {
            pick.AutoCompleteSource = AutoCompleteSource.ListItems; // the source first: the mode needs it
            pick.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        }
        var formBox = Combo(170);
        var genderBox = Combo(110);
        genderBox.Items.AddRange(new object[] { "Any gender", "Male", "Female" });
        genderBox.SelectedIndex = 0;
        var shinyChoices = new List<(string Key, string Text)> { ("any", "Shiny or not"), ("shiny", "Shiny only") };
        if (StopTargets.HasSquareShinies(game)) { shinyChoices.Add(("star", "Star shiny only")); shinyChoices.Add(("square", "Square shiny only")); }
        shinyChoices.Add(("not", "Not shiny"));
        var shinyBox = Combo(140);
        shinyBox.Items.AddRange(shinyChoices.Select(c => (object)c.Text).ToArray());
        shinyBox.SelectedIndex = 0;
        // the species typed / picked: "#025 Pikachu", "Pikachu" or "25" - also species the game doesn't have (they get the warning)
        ushort? SpeciesOf(string text)
        {
            text = text.Trim();
            if (text.StartsWith('#')) text = text[1..];
            var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0 && ushort.TryParse(digits, out var n)) return n;
            var all = PKHeX.Core.GameInfo.Strings.specieslist;
            var i = Array.FindIndex(all, s => s.Equals(text, StringComparison.OrdinalIgnoreCase));
            return i > 0 ? (ushort)i : null;
        }
        var formKeys = new List<int>();
        void FillForms()
        {
            formBox.Items.Clear(); formKeys.Clear();
            formBox.Items.Add("Any form"); formKeys.Add(-1);
            if (SpeciesOf(pick.Text) is { } sp && available.FirstOrDefault(o => o.Species == sp) is { Forms.Length: > 1 } o)
                for (var f = 0; f < o.Forms.Length; f++)
                    if (o.Forms[f].Length > 0) { formBox.Items.Add(o.Forms[f]); formKeys.Add(f); }
            formBox.SelectedIndex = 0;
        }
        pick.SelectedIndexChanged += (_, _) => FillForms();
        pick.Leave += (_, _) => FillForms();
        FillForms();
        var add = new FancyButton("Add", Theme.GlyphCheck, Theme.Accent) { Height = 34 };
        var remove = new FancyButton("Remove", "", Theme.PanelHi) { Height = 34 };
        add.Click += (_, _) =>
        {
            if (SpeciesOf(pick.Text) is not { } sp)
            {
                MessageBox.Show(this, $"There's no Pokémon called \"{pick.Text.Trim()}\". {StopTargets.NotExisting}", "Stop targets", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var t = new StopTarget
            {
                Species = sp, Form = formBox.SelectedIndex >= 0 && formBox.SelectedIndex < formKeys.Count ? formKeys[formBox.SelectedIndex] : -1,
                Gender = genderBox.SelectedIndex switch { 1 => "male", 2 => "female", _ => "any" },
                Shiny = shinyChoices[Math.Max(0, shinyBox.SelectedIndex)].Key,
            };
            if (StopTargets.Problem(game, t) is { } problem)
            {
                MessageBox.Show(this, problem, "Stop targets", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (targets.Any(x => x.Species == t.Species && x.Form == t.Form && x.Gender == t.Gender && x.Shiny == t.Shiny)) return;
            targets.Add(t);
            ShowTargets();
        };
        remove.Click += (_, _) => { if (targetList.SelectedIndex >= 0) { targets.RemoveAt(targetList.SelectedIndex); ShowTargets(); } };
        body.Controls.Add(Row(L("Pokémon:"), pick, L("Form:"), formBox));
        body.Controls.Add(Row(L("Gender:"), genderBox, L("Shiny:"), shinyBox, add, remove));

        // ---------------------------------------------------------------- stop rules
        var rulesStart = body.Controls.Count;
        body.Controls.Add(H("Stop on more than shinies"));
        body.Controls.Add(P("The bot always stops on a shiny. It can ALSO stop when an encounter matches these rules " +
                            "(e.g. a Jolly nature, 31 Speed, IV total ≥ 150). Leave everything empty to stop on shinies only."));
        body.Controls.Add(L("Natures:"));
        var stopNatures = NatureList(_o.StopNatures);
        body.Controls.Add(stopNatures);
        var minTotal = Num(0, 186, _o.MinIvTotal);
        body.Controls.Add(Row(L("Minimum IV total (0 = off, max 186):"), minTotal));
        var ivRow = Row(L("Minimum IVs:"));
        var stopIvs = IvRow(_o.MinIvs, ivRow);
        body.Controls.Add(ivRow);
        var all = new CheckBox { Text = "All of the above must match (otherwise any one is enough)", Checked = _o.RequireAll, AutoSize = true, ForeColor = Theme.Text };
        body.Controls.Add(all);

        // ---------------------------------------------------------------- auto-catch (optional)
        body.Controls.Add(H("Auto-catch (optional - off by default)"));
        body.Controls.Add(P("When the bot finds something it normally stops and leaves the battle to you. If you turn this on it throws " +
                            "Poké Balls by itself until it's caught. It stops and hands the battle back to you if anything unexpected " +
                            "happens (your Pokémon faints, the menu doesn't come back). After a catch you get a notification - SAVE THE GAME " +
                            "before hunting again. Works with the FireRed/LeafGreen Python hunter.", Theme.Muted));
        var acOff = new RadioButton { Text = "Off (stop and wait for me)", AutoSize = true, ForeColor = Theme.Text, Checked = _o.AutoCatch == "off" };
        var acNon = new RadioButton { Text = "Catch non-legendary Pokémon only", AutoSize = true, ForeColor = Theme.Text, Checked = _o.AutoCatch == "non_legendary" };
        var acAll = new RadioButton { Text = "Catch everything, including legendaries (e.g. Deoxys)", AutoSize = true, ForeColor = Theme.Warn, Checked = _o.AutoCatch == "all" };
        var acGroup = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        acGroup.Controls.AddRange(new Control[] { acOff, acNon, acAll });
        body.Controls.Add(acGroup);
        var slot = Num(1, 16, _o.BallSlot);
        var maxBalls = Num(1, 99, _o.MaxBalls);
        body.Controls.Add(Row(L("Ball = position in the POKé BALLS pocket (1 = top):"), slot, L("   Max. balls:"), maxBalls));
        body.Controls.Add(P("Tip: position 1 is usually the MASTER BALL if you have one (FireRed/LeafGreen sort the pocket by item number).", Theme.Warn));

        // ---------------------------------------------------------------- RNG manipulation (optional)
        body.Controls.Add(H("Shiny RNG manipulation (optional - off by default)"));
        body.Controls.Add(P("Instead of resetting until a shiny shows up by chance (about 1 in 8,192 per reset), ShinyForge can read the " +
                            "game's random number generator from the Switch's memory and press A at the exact moment that produces a shiny " +
                            "(usually minutes instead of days). The shiny is generated by the game itself, but it's a manipulated - not a " +
                            "random - encounter, which some players prefer not to use. That's why it only runs if you turn it on here.\n\n" +
                            "Works for FireRed/LeafGreen (Switch) static encounters with the Python hunter: Deoxys, Mewtwo, the legendary birds, " +
                            "Lugia, Ho-Oh, Snorlax... Stand in front of the Pokémon and save first, like normal soft resetting. A miss (the " +
                            "WiFi timing is off by a frame) just means one more reset; the bot learns the timing by itself."));
        var rngOn = new CheckBox
        {
            Text = "Use shiny RNG manipulation for static encounters", Checked = _o.RngEnabled && _rngAvailable, AutoSize = true,
            ForeColor = Theme.Gold, Font = new Font("Segoe UI Semibold", 10.5f), Enabled = _rngAvailable,
        };
        body.Controls.Add(rngOn);
        if (!_rngAvailable) body.Controls.Add(P("(Only available for FireRed / LeafGreen on the Switch.)", Theme.Warn));
        body.Controls.Add(L("Optional: only these natures (none ticked = any nature; each one ticked makes the wait longer):"));
        var rngNatures = NatureList(_o.RngNatures);
        body.Controls.Add(rngNatures);
        var rngIvRow = Row(L("Optional minimum IVs:"));
        var rngIvs = IvRow(_o.RngMinIvs, rngIvRow);
        body.Controls.Add(rngIvRow);
        void SyncRng() { rngNatures.Enabled = rngOn.Checked; foreach (var n in rngIvs.Values) n.Enabled = rngOn.Checked; }
        rngOn.CheckedChanged += (_, _) =>
        {
            if (rngOn.Checked && MessageBox.Show(this, "Turn on shiny RNG manipulation?\n\nThe Python hunter will time its button press to " +
                    "make the game generate a shiny instead of resetting at random. You can turn it off here at any time.",
                    "Shiny RNG manipulation", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                rngOn.Checked = false;
            SyncRng();
        };
        SyncRng();

        // SysBot.exe games: only the stop targets (the rest is the Python hunter's / 3DS bot's)
        if (targetsOnly)
            for (var i = rulesStart; i < body.Controls.Count; i++) body.Controls[i].Visible = false;

        save.Click += (_, _) =>
        {
            gs.StopTargets = targets;
            _o.StopSpecies = ""; // replaced by the per-game stop targets
            _o.StopNatures = stopNatures.CheckedItems.Cast<string>().ToList();
            _o.MinIvTotal = (int)minTotal.Value;
            _o.MinIvs = stopIvs.ToDictionary(k => k.Key, v => (int)v.Value.Value);
            _o.RequireAll = all.Checked;
            _o.AutoCatch = acAll.Checked ? "all" : acNon.Checked ? "non_legendary" : "off";
            _o.BallSlot = (int)slot.Value;
            _o.MaxBalls = (int)maxBalls.Value;
            if (_rngAvailable) // the 3DS pages show RNG greyed out - saving there must not switch the Switch's RNG setting off
            {
                _o.RngEnabled = rngOn.Checked;
                _o.RngNatures = rngNatures.CheckedItems.Cast<string>().ToList();
                _o.RngMinIvs = rngIvs.ToDictionary(k => k.Key, v => (int)v.Value.Value);
            }
            AppSettings.Current.Save();
            var bot = AppPaths.FindPythonBotFolder(AppSettings.Current.PythonBotFolder);
            if (bot != null && game.Bot == BotKind.FrlgSwitch) try { HuntService.WriteBotOverrides(bot, game); } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            if (HuntService.IsPythonHuntRunning())
                MessageBox.Show(this, "Saved. The running Python hunter uses the new options after it's restarted (Stop, then Start hunting).", "Hunt options");
            DialogResult = DialogResult.OK;
            Close();
        };
    }
}
