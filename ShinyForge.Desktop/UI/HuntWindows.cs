using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>Hunt options: stop rules, OPTIONAL auto-catch and OPTIONAL shiny RNG manipulation (both off unless turned on here).</summary>
public sealed class HuntOptionsWindow : Window
{
    private static readonly string[] Natures = { "Hardy", "Lonely", "Brave", "Adamant", "Naughty", "Bold", "Docile", "Relaxed", "Impish", "Lax",
        "Timid", "Hasty", "Serious", "Jolly", "Naive", "Modest", "Mild", "Quiet", "Bashful", "Rash", "Calm", "Gentle", "Sassy", "Careful", "Quirky" };
    private static readonly string[] Stats = { "HP", "Atk", "Def", "SpA", "SpD", "Spe" };

    /// <param name="game">The game whose stop targets are edited.</param>
    /// <param name="targetsOnly">SysBot.exe games: only the stop targets.</param>
    public HuntOptionsWindow(Game game, bool rngAvailable, bool targetsOnly = false)
    {
        var o = AppSettings.Current.Hunt;
        Title = "ShinyForge - hunt options";
        Icon = App.Icon;
        Width = 820; Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Skin.B(Skin.Bg);
        var body = new StackPanel { Margin = new Thickness(18), Spacing = 6 };
        TextBlock H(string t) { var h = Skin.H2(t, Skin.Gold); h.Margin = new Thickness(0, 14, 0, 2); return h; }
        Dictionary<string, CheckBox> NatureGrid(IEnumerable<string> selected)
        {
            var d = new Dictionary<string, CheckBox>();
            var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            foreach (var n in Natures)
            {
                var cb = Skin.Check(n, selected.Contains(n, StringComparer.OrdinalIgnoreCase));
                cb.Width = 140;
                d[n] = cb;
                wrap.Children.Add(cb);
            }
            body.Children.Add(new Border { Background = Skin.B(Skin.PanelHi), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Child = wrap });
            return d;
        }
        Dictionary<string, NumericUpDown> IvRow(Dictionary<string, int> values, string label)
        {
            var d = new Dictionary<string, NumericUpDown>();
            var wrap = new WrapPanel();
            wrap.Children.Add(Skin.T(label, 13, Skin.Text, wrap: false));
            foreach (var s in Stats)
            {
                var n = Skin.Number(0, 31, values.TryGetValue(s, out var v) ? v : 0, 110);
                d[s] = n;
                var l = Skin.T(s, 13, Skin.Text, wrap: false);
                l.Margin = new Thickness(12, 6, 4, 0);
                wrap.Children.Add(l);
                wrap.Children.Add(n);
            }
            body.Children.Add(wrap);
            return d;
        }

        // ---------------------------------------------------------------- stop targets (this game)
        var gs = AppSettings.Current.For(game.Id);
        var available = StopTargets.Available(game);
        var targets = gs.StopTargets.Select(t => new StopTarget { Species = t.Species, Form = t.Form, Gender = t.Gender, Shiny = t.Shiny }).ToList();
        if (targets.Count == 0 && o.StopSpecies.Trim().Length > 0) // the old "species, comma separated" box → targets
            foreach (var n in o.StopSpecies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (available.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } hit)
                    targets.Add(new StopTarget { Species = hit.Species });
        body.Children.Add(H($"Stop targets - Pokémon {game.Title}"));
        body.Children.Add(Skin.MutedText("Stop when one of these shows up: any Pokémon you can get in this game, with its form, gender and whether it must be shiny. " +
                                         "The bot still stops on EVERY shiny on top of these." + (targetsOnly ? " SysBot.exe holds one target, so it uses the first one in the list." : "")));
        var targetList = new ListBox { Height = 120, Background = Skin.B(Skin.PanelHi) };
        void ShowTargets() => targetList.ItemsSource = targets.Select(t => StopTargets.Describe(game, t)).ToList();
        ShowTargets();
        body.Children.Add(targetList);
        var pick = new AutoCompleteBox
        {
            Width = 240, FilterMode = AutoCompleteFilterMode.Contains, MinimumPrefixLength = 1,
            ItemsSource = available.Select(x => $"#{x.Species:000} {x.Name}").ToList(), Watermark = "Pokémon (name or number)",
        };
        var formBox = new ComboBox { Width = 180 };
        var genderBox = new ComboBox { Width = 130, ItemsSource = new[] { "Any gender", "Male", "Female" }, SelectedIndex = 0 };
        var shinyChoices = new List<(string Key, string Text)> { ("any", "Shiny or not"), ("shiny", "Shiny only") };
        if (StopTargets.HasSquareShinies(game)) { shinyChoices.Add(("star", "Star shiny only")); shinyChoices.Add(("square", "Square shiny only")); }
        shinyChoices.Add(("not", "Not shiny"));
        var shinyBox = new ComboBox { Width = 160, ItemsSource = shinyChoices.Select(c => c.Text).ToList(), SelectedIndex = 0 };
        // "#025 Pikachu", "Pikachu" or "25" - also species the game doesn't have (they get the warning)
        ushort? SpeciesOf(string? text)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith('#')) text = text[1..];
            var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0 && ushort.TryParse(digits, out var n)) return n;
            var all = PKHeX.Core.GameInfo.Strings.specieslist;
            var i = Array.FindIndex(all, x => x.Equals(text, StringComparison.OrdinalIgnoreCase));
            return i > 0 ? (ushort)i : null;
        }
        var formKeys = new List<int>();
        void FillForms()
        {
            var names = new List<string> { "Any form" };
            formKeys.Clear(); formKeys.Add(-1);
            if (SpeciesOf(pick.Text) is { } sp && available.FirstOrDefault(x => x.Species == sp) is { Forms.Length: > 1 } opt)
                for (var f = 0; f < opt.Forms.Length; f++)
                    if (opt.Forms[f].Length > 0) { names.Add(opt.Forms[f]); formKeys.Add(f); }
            formBox.ItemsSource = names;
            formBox.SelectedIndex = 0;
        }
        pick.TextChanged += (_, _) => FillForms();
        FillForms();
        var add = new FancyButton("Add", Skin.GlyphCheck, Skin.Accent);
        var remove = new FancyButton("Remove", "", Skin.PanelHi);
        add.Click += async (_, _) =>
        {
            if (SpeciesOf(pick.Text) is not { } sp)
            {
                await Dialogs.Info(this, $"There's no Pokémon called \"{(pick.Text ?? "").Trim()}\". {StopTargets.NotExisting}", "Stop targets");
                return;
            }
            var t = new StopTarget
            {
                Species = sp, Form = formBox.SelectedIndex >= 0 && formBox.SelectedIndex < formKeys.Count ? formKeys[formBox.SelectedIndex] : -1,
                Gender = genderBox.SelectedIndex switch { 1 => "male", 2 => "female", _ => "any" },
                Shiny = shinyChoices[Math.Max(0, shinyBox.SelectedIndex)].Key,
            };
            if (StopTargets.Problem(game, t) is { } problem) { await Dialogs.Info(this, problem, "Stop targets"); return; }
            if (targets.Any(x => x.Species == t.Species && x.Form == t.Form && x.Gender == t.Gender && x.Shiny == t.Shiny)) return;
            targets.Add(t);
            ShowTargets();
        };
        remove.Click += (_, _) => { if (targetList.SelectedIndex >= 0) { targets.RemoveAt(targetList.SelectedIndex); ShowTargets(); } };
        body.Children.Add(Skin.Wrap(Skin.T("Pokémon:", 13, Skin.Text, wrap: false), pick, Skin.T("Form:", 13, Skin.Text, wrap: false), formBox));
        body.Children.Add(Skin.Wrap(Skin.T("Gender:", 13, Skin.Text, wrap: false), genderBox, Skin.T("Shiny:", 13, Skin.Text, wrap: false), shinyBox, add, remove));

        var rulesStart = body.Children.Count;
        body.Children.Add(H("Stop on more than shinies"));
        body.Children.Add(Skin.MutedText("The bot always stops on a shiny. It can ALSO stop when an encounter matches these rules (e.g. a Jolly nature, 31 Speed, IV total ≥ 150). Leave everything empty to stop on shinies only."));
        body.Children.Add(Skin.T("Natures:", 13, Skin.Text));
        var stopNatures = NatureGrid(o.StopNatures);
        var minTotal = Skin.Number(0, 186, o.MinIvTotal);
        body.Children.Add(Skin.Row(Skin.T("Minimum IV total (0 = off, max 186):", 13, Skin.Text, wrap: false), minTotal));
        var stopIvs = IvRow(o.MinIvs, "Minimum IVs:");
        var all = Skin.Check("All of the above must match (otherwise any one is enough)", o.RequireAll);
        body.Children.Add(all);

        body.Children.Add(H("Auto-catch (optional - off by default)"));
        body.Children.Add(Skin.MutedText("When the bot finds something it normally stops and leaves the battle to you. If you turn this on it throws Poké Balls by itself until it's caught. " +
                                      "It stops and hands the battle back to you if anything unexpected happens. After a catch you get a notification - SAVE THE GAME before hunting again. Works with the FireRed/LeafGreen Python hunter."));
        var acOff = new RadioButton { Content = Skin.T("Off (stop and wait for me)", 13, Skin.Text), GroupName = "ac", IsChecked = o.AutoCatch == "off" };
        var acNon = new RadioButton { Content = Skin.T("Catch non-legendary Pokémon only", 13, Skin.Text), GroupName = "ac", IsChecked = o.AutoCatch == "non_legendary" };
        var acAll = new RadioButton { Content = Skin.T("Catch everything, including legendaries (e.g. Deoxys)", 13, Skin.Warn), GroupName = "ac", IsChecked = o.AutoCatch == "all" };
        body.Children.Add(Skin.Column(2, acOff, acNon, acAll));
        var slot = Skin.Number(1, 16, o.BallSlot);
        var maxBalls = Skin.Number(1, 99, o.MaxBalls);
        body.Children.Add(Skin.Wrap(Skin.T("Ball = position in the POKé BALLS pocket (1 = top):", 13, Skin.Text, wrap: false), slot, Skin.T("Max. balls:", 13, Skin.Text, wrap: false), maxBalls));
        body.Children.Add(Skin.T("Tip: position 1 is usually the MASTER BALL if you have one (FireRed/LeafGreen sort the pocket by item number).", 13, Skin.Warn));

        body.Children.Add(H("Shiny RNG manipulation (optional - off by default)"));
        body.Children.Add(Skin.MutedText("Instead of resetting until a shiny shows up by chance (about 1 in 8,192 per reset), ShinyForge can read the game's random number generator from the Switch's memory " +
                                      "and press A at the exact moment that produces a shiny. The shiny is generated by the game itself, but it's a manipulated - not a random - encounter, which some players " +
                                      "prefer not to use. That's why it only runs if you turn it on here.\n\nWorks for FireRed/LeafGreen (Switch) static encounters with the Python hunter. Stand in front of the Pokémon and save first."));
        var rngOn = Skin.Check("Use shiny RNG manipulation for static encounters", o.RngEnabled && rngAvailable);
        rngOn.IsEnabled = rngAvailable;
        body.Children.Add(rngOn);
        if (!rngAvailable) body.Children.Add(Skin.T("(Only available for FireRed / LeafGreen on the Switch.)", 13, Skin.Warn));
        body.Children.Add(Skin.T("Optional: only these natures (none ticked = any nature; each one ticked makes the wait longer):", 13, Skin.Text));
        var rngNatures = NatureGrid(o.RngNatures);
        var rngIvs = IvRow(o.RngMinIvs, "Optional minimum IVs:");
        void SyncRng() { foreach (var c in rngNatures.Values) c.IsEnabled = rngOn.IsChecked == true; foreach (var n in rngIvs.Values) n.IsEnabled = rngOn.IsChecked == true; }
        rngOn.IsCheckedChanged += async (_, _) =>
        {
            if (rngOn.IsChecked == true && !await Dialogs.YesNo(this, "Turn on shiny RNG manipulation?\n\nThe Python hunter will time its button press to make the game generate a shiny instead of resetting at random. You can turn it off here at any time.", "Shiny RNG manipulation"))
                rngOn.IsChecked = false;
            SyncRng();
        };
        SyncRng();

        // SysBot.exe games: only the stop targets (the rest is the Python hunter's / 3DS bot's)
        if (targetsOnly)
            for (var i = rulesStart; i < body.Children.Count; i++) body.Children[i].IsVisible = false;

        var save = new FancyButton("Save", Skin.GlyphCheck, Skin.Accent);
        var cancel = new FancyButton("Cancel", "", Skin.PanelHi);
        cancel.Click += (_, _) => Close();
        save.Click += async (_, _) =>
        {
            gs.StopTargets = targets;
            o.StopSpecies = ""; // replaced by the per-game stop targets
            o.StopNatures = stopNatures.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
            o.MinIvTotal = (int)(minTotal.Value ?? 0);
            o.MinIvs = stopIvs.ToDictionary(k => k.Key, v => (int)(v.Value.Value ?? 0));
            o.RequireAll = all.IsChecked == true;
            o.AutoCatch = acAll.IsChecked == true ? "all" : acNon.IsChecked == true ? "non_legendary" : "off";
            o.BallSlot = (int)(slot.Value ?? 1);
            o.MaxBalls = (int)(maxBalls.Value ?? 25);
            if (rngAvailable) // the 3DS pages show RNG greyed out - saving there must not switch the Switch's RNG setting off
            {
                o.RngEnabled = rngOn.IsChecked == true;
                o.RngNatures = rngNatures.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
                o.RngMinIvs = rngIvs.ToDictionary(k => k.Key, v => (int)(v.Value.Value ?? 0));
            }
            AppSettings.Current.Save();
            if (game.Bot == BotKind.FrlgSwitch && AppPaths.FindPythonBotFolder(AppSettings.Current.PythonBotFolder) is { } bot)
                try { HuntService.WriteBotOverrides(bot, game); } catch (Exception ex) { await Dialogs.Info(this, ex.Message); }
            if (HuntService.IsPythonHuntRunning())
                await Dialogs.Info(this, "Saved. The running Python hunter uses the new options after it's restarted (Stop, then Start hunting).", "Hunt options");
            Close();
        };
        var bottom = new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(12, 8), Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } } };
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new DockPanel { Children = { bottom, new ScrollViewer { Content = body } } };
    }
}

/// <summary>Hunt statistics: counter, odds, chance by now, rate, ETA, best IVs, natures, species, shinies and a 24-hour chart.</summary>
public sealed class HuntStatsWindow : Window
{
    private readonly ComboBox _source;
    private readonly CheckBox _charm = Skin.Check("Shiny Charm", WebServer.ShinyCharm);
    private readonly TextBlock _big = Skin.T("", 38, Skin.Text, FontWeight.Bold, false);
    private readonly TextBlock _sub = Skin.H2("", Skin.Gold);
    private readonly SelectableTextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Foreground = Skin.B(Skin.Text), FontSize = 13.5 };
    private readonly SelectableTextBlock _side = new() { TextWrapping = TextWrapping.Wrap, Foreground = Skin.B(Skin.Muted), FontSize = 13 };
    private readonly Chart _chart = new() { Height = 170 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly List<HuntStats.Source> _sources;

    public HuntStatsWindow(string? preferSource = null)
    {
        Title = "ShinyForge - hunt statistics";
        Icon = App.Icon;
        Width = 980; Height = 640; MinWidth = 820; MinHeight = 560;
        Background = Skin.B(Skin.Bg);
        _sources = HuntStats.Sources();
        var names = _sources.Select(s => s.Name).ToList();
        if (names.Count == 0) names.Add("No hunt logs yet - start a hunt first");
        var idx = preferSource == null ? -1 : _sources.FindIndex(s => s.Name.Contains(preferSource, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 && HuntStats.Active() is { } a) idx = _sources.FindIndex(s => s.Name == a.Name);
        _source = Skin.Combo(names, names[Math.Max(0, idx)], 420);
        var refresh = new FancyButton("Refresh", Skin.GlyphSync, Skin.PanelHi) { Height = 32 };
        var log = new FancyButton("Open log", Skin.GlyphFolder, Skin.PanelHi) { Height = 32 };
        var top = new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(12, 8), Child = Skin.Row(_source, _charm, refresh, log) };
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("62*,38*"), Margin = new Thickness(18, 10, 18, 4) };
        var left = new StackPanel { Spacing = 4, Children = { _big, _sub, _details } };
        Grid.SetColumn(_side, 1);
        main.Children.Add(new ScrollViewer { Content = left });
        main.Children.Add(new ScrollViewer { Content = _side, [Grid.ColumnProperty] = 1 });
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_chart, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_chart);
        root.Children.Add(main);
        Content = root;
        _source.SelectionChanged += (_, _) => Fill();
        _charm.IsCheckedChanged += (_, _) => { WebServer.ShinyCharm = _charm.IsChecked == true; Fill(); };
        refresh.Click += (_, _) => Fill();
        log.Click += (_, _) => { if (Current() is { } s) try { OsInfo.Open(s.CsvPath); } catch { } };
        _timer.Tick += (_, _) => Fill();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Opened += (_, _) => Fill();
    }

    private HuntStats.Source? Current() => _source.SelectedIndex >= 0 && _source.SelectedIndex < _sources.Count ? _sources[_source.SelectedIndex] : null;

    private void Fill()
    {
        if (Current() is not { } src) { _big.Text = "No hunt yet"; return; }
        HuntStatsResult r;
        try { r = HuntStats.Compute(src, _charm.IsChecked == true); }
        catch (Exception ex) { _big.Text = "Can't read the log"; _details.Text = ex.Message; return; }
        _big.Text = $"{r.Phase:N0} encounters";
        _sub.Text = r.Milestone;
        var eta = r.Eta90 is { } e ? $"{(int)e.TotalHours}h {e.Minutes:00}m" : r.Phase >= r.To90 ? "already past 90 %" : "-";
        _details.Text =
            $"Current hunt (since the last shiny{(r.PhaseStart is { } ps ? $", started {ps:yyyy-MM-dd HH:mm}" : "")})\n\n" +
            $"Shiny odds per encounter:  1 in {Math.Round(1 / r.Odds):N0}{(r.ShinyCharm ? " (Shiny Charm)" : "")}  (Generation {r.Gen})\n" +
            $"Chance you'd have found one by now:  {r.ChanceByNow:P1}\n" +
            $"50 % of hunters find it by {r.To50:N0} encounters, 90 % by {r.To90:N0}\n" +
            $"Speed:  {(r.PerHour > 0 ? $"{r.PerHour:0} encounters / hour" : "- (not hunting right now)")}      Time to 90 %:  {eta}\n\n" +
            (r.BestIv is { } b ? $"Best IVs this hunt:  {b.IvTotal}/186 ({string.Join("/", b.Ivs)}) - {b.Species}, {b.Nature} at {b.Time:MM-dd HH:mm}\n" : "") +
            (r.Last is { } l ? $"Last encounter:  {l.Species}, {l.Nature}, IVs {l.IvTotal}/186 at {l.Time:HH:mm:ss}\n" : "") +
            $"\nAll time in this log: {r.Total:N0} encounters, {r.ShiniesFound} shin{(r.ShiniesFound == 1 ? "y" : "ies")}.";
        _side.Text =
            "Species this hunt\n" + string.Join("\n", r.Species.Select(s => $"  {s.Name}  ×{s.Count:N0}")) + "\n\n" +
            "Most common natures\n" + string.Join("\n", r.Natures.Select(n => $"  {n.Name}  ×{n.Count:N0}")) + "\n\n" +
            "Shinies found\n" + (r.Shinies.Count == 0 ? "  none yet" : string.Join("\n", r.Shinies.TakeLast(8).Select(s => $"  ★ {s.Species} {s.Time:yyyy-MM-dd}")));
        _chart.Data = r.PerHourLast24;
        _chart.InvalidateVisual();
    }

    private sealed class Chart : Control
    {
        public int[] Data { get; set; } = new int[24];
        public override void Render(DrawingContext g)
        {
            g.FillRectangle(Skin.B(Skin.Panel), new Rect(Bounds.Size));
            FormattedText T(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Skin.B(Skin.Muted));
            g.DrawText(T("Encounters per hour - last 24 hours"), new Point(14, 6));
            var max = Math.Max(1, Data.Max());
            var area = new Rect(14, 28, Bounds.Width - 28, Bounds.Height - 52);
            var w = area.Width / 24;
            for (int i = 0; i < 24; i++)
            {
                var h = Data[i] * area.Height / max;
                g.FillRectangle(Skin.B(Skin.Accent), new Rect(area.X + i * w + 2, area.Bottom - h, Math.Max(1, w - 4), h));
                if (i % 3 == 0) g.DrawText(T(DateTime.Now.AddHours(-(24 - i)).ToString("HH:00")), new Point(area.X + i * w, area.Bottom + 4));
            }
            g.DrawText(T($"max {max}/h"), new Point(area.Right - 70, 6));
        }
    }
}
