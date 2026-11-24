using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// Hunt profiles: several hunts side by side - start another hunt and come back to this one later, each with its own timer
/// (time spent hunting, start and finish date) and its own encounters. Same data as the Windows version (Services\HuntProfiles).
/// </summary>
public sealed class HuntProfilesWindow : Window
{
    private readonly ListBox _list = new() { Background = Skin.B(Skin.Panel) };
    private readonly SelectableTextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, Foreground = Skin.B(Skin.Text), FontSize = 13, Margin = new Thickness(14, 8) };
    private readonly FancyButton _activate = new("Make active", Skin.GlyphPlay, Skin.Accent);
    private readonly FancyButton _timer = new("Start timer", Skin.GlyphPlay, Skin.PanelHi);
    private readonly FancyButton _plusOne = new("+1 encounter", Skin.GlyphSparkle, Skin.PanelHi);
    private readonly FancyButton _finish = new("Found it!", Skin.GlyphCheck, Skin.Good);
    private readonly FancyButton _edit = new("Edit", Skin.GlyphSettings, Skin.PanelHi);
    private readonly FancyButton _delete = new("Delete", Skin.GlyphStop, Skin.StopRed);
    private readonly FancyButton _stats = new("Statistics", Skin.GlyphInfo, Skin.PanelHi);
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, (int Count, int Shinies, DateTime At)> _encounters = new();
    private List<HuntProfile> _shown = new();
    private readonly string? _gameId;

    public HuntProfilesWindow(string? gameId = null)
    {
        _gameId = gameId;
        Title = "ShinyForge - hunt profiles";
        Icon = App.Icon;
        Width = 1100; Height = 600; MinWidth = 820; MinHeight = 420;
        Background = Skin.B(Skin.Bg);
        var add = new FancyButton("New hunt", Skin.GlyphSparkle, Skin.Accent2);
        var bar = new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(10, 8), Child = Skin.Wrap(add, _activate, _timer, _plusOne, _finish, _edit, _delete, _stats) };
        var intro = Skin.Small("Each hunt keeps its own timer and encounters. The active hunt of a game counts the time its bot hunts (or your own timer, " +
                               "for hunts without a bot) and the encounters it logs. Make another hunt active to switch - the first one waits, paused, " +
                               "until you come back. A shiny of the target finishes the hunt.");
        intro.Margin = new Thickness(14, 6);
        var root = new DockPanel();
        foreach (var (c, d) in new (Control, Dock)[] { (bar, Dock.Top), (intro, Dock.Top), (_detail, Dock.Bottom) }) { DockPanel.SetDock(c, d); root.Children.Add(c); }
        root.Children.Add(_list);
        Content = root;

        add.Click += async (_, _) => await NewHunt();
        _activate.Click += (_, _) =>
        {
            if (Selected() is not { } p) return;
            if (p.State == HuntState.Active) HuntProfiles.Pause(p.Id); else HuntProfiles.Activate(p.Id);
            Fill();
        };
        _timer.Click += (_, _) => { if (Selected() is { } p) { HuntProfiles.SetTimer(p.Id, !p.TimerRunning); Fill(); } };
        _plusOne.Click += (_, _) => { if (Selected() is { } p) { HuntProfiles.Edit(p.Id, x => x.ManualEncounters++); _encounters.Remove(p.Id); Fill(); } };
        _finish.Click += async (_, _) => await Finish();
        _edit.Click += async (_, _) => { if (Selected() is { } p) await Edit(p); };
        _delete.Click += async (_, _) =>
        {
            if (Selected() is not { } p) return;
            if (!await Dialogs.YesNo(this, $"Delete the hunt \"{p.Name}\"? Its timer and notes are lost (the encounter log itself stays).", "Hunt profiles")) return;
            HuntProfiles.Delete(p.Id);
            Fill();
        };
        _stats.Click += (_, _) => { if (Selected() is { } p) new HuntStatsWindow(profileId: p.Id).Show(this); };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        Action changed = () => Dispatcher.UIThread.Post(() => Fill());
        HuntProfiles.Changed += changed;
        _clock.Tick += (_, _) => Fill();
        _clock.Start();
        Closed += (_, _) => { HuntProfiles.Changed -= changed; _clock.Stop(); };
        Opened += async (_, _) =>
        {
            Fill();
            var active = _gameId != null ? _shown.FindIndex(p => p.GameId == _gameId && p.State == HuntState.Active) : -1;
            _list.SelectedIndex = Math.Max(0, active);
            if (_shown.Count == 0) await NewHunt();
        };
    }

    private HuntProfile? Selected() => _list.SelectedIndex >= 0 && _list.SelectedIndex < _shown.Count ? HuntProfiles.Get(_shown[_list.SelectedIndex].Id) : null;

    private void Fill()
    {
        var sel = Selected()?.Id;
        _shown = HuntProfiles.All.OrderBy(p => p.State).ThenByDescending(p => p.Started ?? p.Created).ToList();
        var lines = _shown.Select(p =>
        {
            var enc = Count(p);
            var state = p.State switch { HuntState.Active => p.TimerRunning ? "â— active (timer)" : "â— active", HuntState.Paused => "paused", _ => p.FoundSpecies.Length > 0 ? "â˜… found " + p.FoundSpecies : "finished" };
            return new TextBlock
            {
                Text = $"{p.Name}  â€¢  {p.GameTitle}  â€¢  {(p.Target.Length > 0 ? p.Target : "any shiny")}  â€¢  {state}  â€¢  {HuntProfiles.Format(p.Time)}  â€¢  {enc.Count:N0} encounters" +
                       (p.Started is { } s ? $"  â€¢  started {s:yyyy-MM-dd}" : "") + (p.Finished is { } f ? $"  â€¢  finished {f:yyyy-MM-dd}" : ""),
                Foreground = Skin.B(p.State switch { HuntState.Active => Skin.Good, HuntState.Finished => Skin.Gold, _ => Skin.Text }),
                FontSize = 13.5,
            };
        }).ToList();
        var texts = lines.Select(l => l.Text).ToList();
        var old = _list.Items.OfType<TextBlock>().Select(t => t.Text).ToList();
        if (!texts.SequenceEqual(old))
        {
            if (old.Count == texts.Count)
                for (int i = 0; i < texts.Count; i++) { var t = (TextBlock)_list.Items[i]!; t.Text = texts[i]; t.Foreground = lines[i].Foreground; }
            else
            {
                _list.Items.Clear();
                foreach (var l in lines) _list.Items.Add(l);
                _list.SelectedIndex = Math.Max(sel == null ? 0 : _shown.FindIndex(p => p.Id == sel), 0);
            }
        }
        UpdateButtons();
    }

    private (int Count, int Shinies) Count(HuntProfile p)
    {
        if (_encounters.TryGetValue(p.Id, out var c) && (DateTime.Now - c.At).TotalSeconds < 10) return (c.Count, c.Shinies);
        var n = HuntProfiles.Encounters(p, out var shinies);
        _encounters[p.Id] = (n, shinies, DateTime.Now);
        return (n, shinies);
    }

    private void UpdateButtons()
    {
        var p = Selected();
        foreach (var b in new[] { _activate, _timer, _plusOne, _finish, _edit, _delete, _stats }) b.IsEnabled = p != null;
        if (p == null) { _detail.Text = "Pick a hunt, or start a new one."; return; }
        _activate.Text = p.State == HuntState.Active ? "Pause hunt" : p.State == HuntState.Finished ? "Reopen" : "Make active";
        _timer.Text = p.TimerRunning ? "Stop timer" : "Start timer";
        _timer.IsEnabled = _plusOne.IsEnabled = _finish.IsEnabled = p.State != HuntState.Finished;
        var bot = p.Game is { } g && HuntStats.SourceFor(g) != null;
        _stats.IsEnabled = bot;
        var enc = Count(p);
        _detail.Text =
            $"{p.Name} - {p.GameTitle}{(p.Target.Length > 0 ? ", hunting " + p.Target : "")}\n" +
            $"Hunting time {HuntProfiles.Format(p.Time)}" + (p.Calendar is { } c ? $"  â€¢  {HuntProfiles.Format(c)} from start to {(p.Finished != null ? "finish" : "now")}" : "") +
            $"  â€¢  {enc.Count:N0} encounter{(enc.Count == 1 ? "" : "s")} ({(bot ? "from the bot's log" : "counted by hand")})" + (enc.Shinies > 0 ? $", {enc.Shinies} shiny" : "") + "\n" +
            (p.State == HuntState.Active
                ? p.TimerRunning ? "The timer is running (started by hand)." : bot ? "The timer runs while this game's bot is hunting." : "No bot hunts this game - use Start timer while you hunt."
                : p.State == HuntState.Paused ? "Paused - make it active to continue where you left off." : $"Finished {p.Finished:yyyy-MM-dd HH:mm}.") +
            (p.Notes.Length > 0 ? "\nNotes: " + p.Notes : "");
    }

    private async Task NewHunt()
    {
        var d = new HuntProfileEditWindow(null, _gameId);
        if (!await d.ShowDialog<bool>(this) || d.Game == null) return;
        var p = HuntProfiles.Create(d.HuntName, d.Game, d.Target, d.MakeActive);
        HuntProfiles.Edit(p.Id, x => x.Notes = d.Notes);
        Fill();
        _list.SelectedIndex = _shown.FindIndex(x => x.Id == p.Id);
    }

    private async Task Edit(HuntProfile p)
    {
        var d = new HuntProfileEditWindow(p, p.GameId);
        if (!await d.ShowDialog<bool>(this) || d.Game == null) return;
        HuntProfiles.Edit(p.Id, x => { x.Name = d.HuntName; x.Target = d.Target; x.Notes = d.Notes; x.GameId = d.Game.Id; x.Seconds = d.Seconds ?? x.Seconds; });
        if (d.MakeActive && p.State != HuntState.Active) HuntProfiles.Activate(p.Id);
        _encounters.Remove(p.Id);
        Fill();
    }

    private async Task Finish()
    {
        if (Selected() is not { } p) return;
        var species = p.Target;
        if (species.Length == 0)
        {
            var d = new HuntProfileEditWindow(null, null, askSpecies: true);
            if (!await d.ShowDialog<bool>(this)) return;
            species = d.Target;
        }
        HuntProfiles.Finish(p.Id, species);
        Fill();
        var done = HuntProfiles.Get(p.Id)!;
        await Dialogs.Info(this, $"{done.Name}: {(species.Length > 0 ? "â˜… " + species : "finished")}\n\nHunting time: {HuntProfiles.Format(done.Time)}\n" +
                                 $"Started {done.Started:yyyy-MM-dd HH:mm}, finished {done.Finished:yyyy-MM-dd HH:mm}" +
                                 (done.Calendar is { } c ? $" ({HuntProfiles.Format(c)})" : ""), "Hunt finished");
    }
}

/// <summary>New / edit hunt (or, with askSpecies, only "which shiny did you find?").</summary>
public sealed class HuntProfileEditWindow : Window
{
    private readonly TextBox _name = Skin.Input("", 360);
    private readonly ComboBox _game;
    private readonly TextBox _target = Skin.Input("", 360);
    private readonly TextBox _notes = Skin.Input("", 360);
    private readonly TextBox _time = Skin.Input("", 120);
    private readonly CheckBox _activate = Skin.Check("Make it the active hunt of its game", true);

    public Game? Game => _game.SelectedIndex >= 0 ? GameCatalog.All[_game.SelectedIndex] : null;
    public string Target => _target.Text?.Trim() ?? "";
    public string HuntName => (_name.Text?.Trim() ?? "") is { Length: > 0 } n ? n : $"{(Target.Length > 0 ? Target : "Shiny")} hunt ({Game?.Title})";
    public string Notes => _notes.Text?.Trim() ?? "";
    public bool MakeActive => _activate.IsChecked == true;
    public double? Seconds { get; private set; }

    public HuntProfileEditWindow(HuntProfile? p, string? gameId, bool askSpecies = false)
    {
        Title = askSpecies ? "Hunt finished" : p == null ? "New hunt" : "Edit hunt";
        Icon = App.Icon;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Background = Skin.B(Skin.Bg);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var titles = GameCatalog.All.Select(g => $"{g.Title}  ({g.PlatformLabel})").ToList();
        var gi = Array.FindIndex(GameCatalog.All, g => g.Id == (p?.GameId ?? gameId));
        _game = Skin.Combo(titles, titles[Math.Max(0, gi)], 360);
        _target.PlaceholderText = "e.g. Deoxys - empty = any shiny";
        _notes.AcceptsReturn = true;
        _notes.Height = 60;
        _time.PlaceholderText = "h:mm:ss";
        if (p != null)
        {
            _name.Text = p.Name; _target.Text = p.Target; _notes.Text = p.Notes;
            _time.Text = $"{(int)p.Time.TotalHours}:{p.Time.Minutes:00}:{p.Time.Seconds:00}";
            _activate.IsChecked = p.State == HuntState.Active;
        }
        var ok = new FancyButton("OK", Skin.GlyphCheck, Skin.Accent);
        var cancel = new FancyButton("Cancel", Skin.GlyphStop, Skin.PanelHi);
        ok.Click += (_, _) =>
        {
            var parts = (_time.Text ?? "").Trim().Split(':');
            if (p != null && parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && int.TryParse(parts[2], out var s) &&
                h >= 0 && m is >= 0 and < 60 && s is >= 0 and < 60)
                Seconds = h * 3600 + m * 60 + s;
            Close(true);
        };
        cancel.Click += (_, _) => Close(false);
        var form = new StackPanel { Spacing = 6, Margin = new Thickness(16) };
        void Row(string label, Control c) => form.Children.Add(new StackPanel { Spacing = 2, Children = { Skin.Small(label), c } });
        if (askSpecies)
        {
            _target.PlaceholderText = "";
            Row("Which shiny did you find? (leave empty if you gave up)", _target);
        }
        else
        {
            Row("Name", _name);
            Row("Game", _game);
            Row("Target PokÃ©mon", _target);
            Row("Notes", _notes);
            if (p != null) Row("Hunting time", _time);
            form.Children.Add(_activate);
        }
        form.Children.Add(Skin.Row(ok, cancel));
        Content = form;
    }
}
