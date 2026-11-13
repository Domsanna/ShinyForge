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

    public HuntOptionsForm(bool rngAvailable)
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

        // ---------------------------------------------------------------- stop rules
        body.Controls.Add(H("Stop on more than shinies"));
        body.Controls.Add(P("The bot always stops on a shiny. It can ALSO stop when an encounter matches these rules " +
                            "(e.g. a Jolly nature, 31 Speed, IV total ≥ 150). Leave everything empty to stop on shinies only."));
        var species = new TextBox { Width = 500, Text = _o.StopSpecies };
        Theme.StyleInput(species);
        body.Controls.Add(Row(L("Species (comma separated):"), species));
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

        save.Click += (_, _) =>
        {
            _o.StopSpecies = species.Text.Trim();
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
            if (bot != null) try { HuntService.WriteBotOverrides(bot); } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            if (HuntService.IsPythonHuntRunning())
                MessageBox.Show(this, "Saved. The running Python hunter uses the new options after it's restarted (Stop, then Start hunting).", "Hunt options");
            DialogResult = DialogResult.OK;
            Close();
        };
    }
}
