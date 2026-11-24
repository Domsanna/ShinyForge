using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// Hunt profiles: several hunts side by side - start another hunt and come back to this one later, each with its own
/// timer (time spent hunting, start and finish date) and its own encounters.
/// </summary>
public sealed class HuntProfilesForm : Form
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        BorderStyle = BorderStyle.None, BackColor = Theme.Panel, ForeColor = Theme.Text, Font = Theme.Body,
    };
    private readonly Label _detail = new() { Dock = DockStyle.Bottom, Height = 92, ForeColor = Theme.Text, Font = Theme.Body, Padding = new Padding(14, 8, 14, 4), BackColor = Theme.Bg };
    private readonly FancyButton _activate = new("Make active", Theme.GlyphPlay, Theme.Accent);
    private readonly FancyButton _timer = new("Start timer", Theme.GlyphPlay, Theme.PanelHi);
    private readonly FancyButton _plusOne = new("+1 encounter", Theme.GlyphSparkle, Theme.PanelHi);
    private readonly FancyButton _finish = new("Found it!", Theme.GlyphCheck, Theme.Good);
    private readonly FancyButton _edit = new("Edit", Theme.GlyphSettings, Theme.PanelHi);
    private readonly FancyButton _delete = new("Delete", Theme.GlyphStop, Color.FromArgb(150, 40, 60));
    private readonly FancyButton _stats = new("Statistics", Theme.GlyphInfo, Theme.PanelHi);
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private readonly Dictionary<string, (int Count, int Shinies, DateTime At)> _encounters = new();
    private readonly string? _gameId;

    /// <param name="gameId">Opened from a game's page: a new hunt starts on that game.</param>
    public HuntProfilesForm(string? gameId = null)
    {
        _gameId = gameId;
        Text = "ShinyForge - hunt profiles";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Size = new Size(1180, 600);
        MinimumSize = new Size(900, 420);
        StartPosition = FormStartPosition.CenterParent;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(10, 9, 10, 4), BackColor = Theme.Panel, WrapContents = false };
        var add = new FancyButton("New hunt", Theme.GlyphSparkle, Theme.Accent2);
        bar.Controls.AddRange(new Control[] { add, _activate, _timer, _plusOne, _finish, _edit, _delete, _stats });
        foreach (Control c in bar.Controls) c.Margin = new Padding(0, 0, 8, 0);
        var intro = Theme.Label("Each hunt keeps its own timer and encounters. The active hunt of a game counts the time its bot hunts " +
                                "(or your own timer, for hunts without a bot) and the encounters it logs. Make another hunt active " +
                                "to switch - the first one waits, paused, until you come back. A shiny of the target finishes the hunt.",
                                Theme.Small, Theme.Muted);
        intro.AutoSize = false;
        intro.Dock = DockStyle.Top;
        intro.Height = 40;
        intro.Padding = new Padding(14, 6, 14, 0);

        foreach (var (h, w) in new[] { ("Hunt", 220), ("Game", 150), ("Target", 110), ("State", 120), ("Hunting time", 120), ("Encounters", 95),
                                         ("Started", 130), ("Finished", 130), ("Start → finish", 110) })
            _list.Columns.Add(h, w);
        Controls.Add(_list);
        Controls.Add(_detail);
        Controls.Add(intro);
        Controls.Add(bar);

        add.Click += (_, _) => NewHunt();
        _activate.Click += (_, _) =>
        {
            if (Selected() is not { } p) return;
            if (p.State == HuntState.Active) HuntProfiles.Pause(p.Id); else HuntProfiles.Activate(p.Id);
            Fill();
        };
        _timer.Click += (_, _) => { if (Selected() is { } p) { HuntProfiles.SetTimer(p.Id, !p.TimerRunning); Fill(); } };
        _plusOne.Click += (_, _) =>
        {
            if (Selected() is not { } p) return;
            HuntProfiles.Edit(p.Id, x => x.ManualEncounters++);
            _encounters.Remove(p.Id);
            Fill();
        };
        _finish.Click += (_, _) => Finish();
        _edit.Click += (_, _) => { if (Selected() is { } p) Edit(p); };
        _list.DoubleClick += (_, _) => { if (Selected() is { } p) Edit(p); };
        _delete.Click += (_, _) =>
        {
            if (Selected() is not { } p) return;
            if (MessageBox.Show(this, $"Delete the hunt \"{p.Name}\"? Its timer and notes are lost (the encounter log itself stays).", "Hunt profiles",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            HuntProfiles.Delete(p.Id);
            Fill();
        };
        _stats.Click += (_, _) => { if (Selected() is { } p) new HuntStatsForm(profileId: p.Id).Show(Owner ?? this); };
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        HuntProfiles.Changed += OnChanged;
        _clock.Tick += (_, _) => Fill(keepEncounters: true);
        _clock.Start();
        FormClosed += (_, _) => { HuntProfiles.Changed -= OnChanged; _clock.Dispose(); };
        Shown += (_, _) =>
        {
            Fill();
            var start = (_gameId != null ? HuntProfiles.All.FirstOrDefault(p => p.GameId == _gameId && p.State == HuntState.Active) : null)?.Id;
            Select(start);
            if (HuntProfiles.All.Count == 0) NewHunt();
        };
    }

    private void OnChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => Fill(keepEncounters: true)); } catch (InvalidOperationException) { }
    }

    private HuntProfile? Selected() => _list.SelectedItems.Count > 0 ? HuntProfiles.Get((string)_list.SelectedItems[0].Tag!) : null;

    private void Select(string? id)
    {
        foreach (ListViewItem i in _list.Items)
            if ((string)i.Tag! == id || (id == null && i.Index == 0)) { i.Selected = true; i.EnsureVisible(); break; }
        UpdateButtons();
    }

    private void Fill(bool keepEncounters = false)
    {
        var selected = Selected()?.Id;
        var all = HuntProfiles.All.OrderBy(p => p.State).ThenByDescending(p => p.Started ?? p.Created).ToList();
        _list.BeginUpdate();
        // rebuild only when the hunts themselves changed; otherwise update the text (no flicker every second)
        var same = _list.Items.Count == all.Count && all.Select((p, i) => (string)_list.Items[i].Tag! == p.Id).All(x => x);
        if (!same) _list.Items.Clear();
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            var enc = EncounterCount(p, keepEncounters);
            var cells = new[]
            {
                p.Name, p.GameTitle, p.Target.Length > 0 ? p.Target : "any shiny",
                p.State switch { HuntState.Active => p.TimerRunning ? "● active (timer)" : "● active", HuntState.Paused => "paused", _ => p.FoundSpecies.Length > 0 ? "★ found " + p.FoundSpecies : "finished" },
                HuntProfiles.Format(p.Time), enc.Count.ToString("N0"),
                p.Started?.ToString("yyyy-MM-dd HH:mm") ?? "not started", p.Finished?.ToString("yyyy-MM-dd HH:mm") ?? "",
                p.Calendar is { } c ? HuntProfiles.Format(c) : "",
            };
            if (!same)
            {
                var item = new ListViewItem(cells) { Tag = p.Id };
                _list.Items.Add(item);
            }
            else
                for (int k = 0; k < cells.Length; k++)
                    if (_list.Items[i].SubItems[k].Text != cells[k]) _list.Items[i].SubItems[k].Text = cells[k];
            _list.Items[i].ForeColor = p.State switch { HuntState.Active => Theme.Good, HuntState.Finished => Theme.Gold, _ => Theme.Text };
        }
        _list.EndUpdate();
        if (!same) Select(selected);
        else UpdateButtons();
    }

    private (int Count, int Shinies) EncounterCount(HuntProfile p, bool cached)
    {
        // reading the log is slow on big hunts: at most every 10 s per hunt
        if (cached && _encounters.TryGetValue(p.Id, out var c) && (DateTime.Now - c.At).TotalSeconds < 10) return (c.Count, c.Shinies);
        var n = HuntProfiles.Encounters(p, out var shinies);
        _encounters[p.Id] = (n, shinies, DateTime.Now);
        return (n, shinies);
    }

    private void UpdateButtons()
    {
        var p = Selected();
        foreach (var b in new[] { _activate, _timer, _plusOne, _finish, _edit, _delete, _stats }) b.Enabled = p != null;
        if (p == null) { _detail.Text = "Pick a hunt, or start a new one."; return; }
        _activate.Text = p.State == HuntState.Active ? "Pause hunt" : p.State == HuntState.Finished ? "Reopen" : "Make active";
        _timer.Text = p.TimerRunning ? "Stop timer" : "Start timer";
        _timer.Enabled = p.State != HuntState.Finished;
        _plusOne.Enabled = p.State != HuntState.Finished;
        _finish.Enabled = p.State != HuntState.Finished;
        var enc = EncounterCount(p, cached: true);
        var bot = p.Game is { } g && HuntStats.SourceFor(g) != null;
        _stats.Enabled = bot; // statistics come from a bot's encounter log
        _detail.Text =
            $"{p.Name} - {p.GameTitle}{(p.Target.Length > 0 ? ", hunting " + p.Target : "")}\n" +
            $"Hunting time {HuntProfiles.Format(p.Time)}" + (p.Calendar is { } c ? $"  •  {HuntProfiles.Format(c)} from start to {(p.Finished != null ? "finish" : "now")}" : "") +
            $"  •  {enc.Count:N0} encounter{(enc.Count == 1 ? "" : "s")} ({(bot ? "from the bot's log" : "counted by hand")}{(p.ManualEncounters > 0 && bot ? $", {p.ManualEncounters} by hand" : "")})" +
            (enc.Shinies > 0 ? $", {enc.Shinies} shiny" : "") + "\n" +
            (p.State == HuntState.Active
                ? p.TimerRunning ? "The timer is running (started by hand)." : bot ? "The timer runs while this game's bot is hunting." : "No bot hunts this game - use Start timer while you hunt."
                : p.State == HuntState.Paused ? "Paused - make it active to continue where you left off." : $"Finished {p.Finished:yyyy-MM-dd HH:mm}.") +
            (p.Notes.Length > 0 ? "\nNotes: " + p.Notes : "");
    }

    private void NewHunt()
    {
        var game = _gameId != null ? GameCatalog.All.FirstOrDefault(g => g.Id == _gameId) : null;
        using var f = new HuntProfileDialog(null, game);
        if (f.ShowDialog(this) != DialogResult.OK || f.Game == null) return;
        var p = HuntProfiles.Create(f.HuntName, f.Game, f.Target, f.MakeActive);
        HuntProfiles.Edit(p.Id, x => x.Notes = f.Notes);
        Fill();
        Select(p.Id);
    }

    private void Edit(HuntProfile p)
    {
        using var f = new HuntProfileDialog(p, p.Game);
        if (f.ShowDialog(this) != DialogResult.OK || f.Game == null) return;
        HuntProfiles.Edit(p.Id, x =>
        {
            x.Name = f.HuntName;
            x.Target = f.Target;
            x.Notes = f.Notes;
            x.GameId = f.Game.Id;
            x.Seconds = f.Seconds ?? x.Seconds;
        });
        if (f.MakeActive && p.State != HuntState.Active) HuntProfiles.Activate(p.Id);
        _encounters.Remove(p.Id);
        Fill();
    }

    private void Finish()
    {
        if (Selected() is not { } p) return;
        var species = p.Target;
        if (species.Length == 0)
        {
            using var ask = new HuntProfileDialog.AskText("Hunt finished", "Which shiny did you find? (leave empty if you gave up)", "");
            if (ask.ShowDialog(this) != DialogResult.OK) return;
            species = ask.Value;
        }
        HuntProfiles.Finish(p.Id, species);
        Fill();
        var done = HuntProfiles.Get(p.Id)!;
        MessageBox.Show(this, $"{done.Name}: {(species.Length > 0 ? "★ " + species : "finished")}\n\nHunting time: {HuntProfiles.Format(done.Time)}\n" +
                              $"Started {done.Started:yyyy-MM-dd HH:mm}, finished {done.Finished:yyyy-MM-dd HH:mm}" +
                              (done.Calendar is { } c ? $" ({HuntProfiles.Format(c)})" : ""), "Hunt finished", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

/// <summary>New / edit hunt: name, game, target Pokémon, notes (and the hunting time, to correct it).</summary>
public sealed class HuntProfileDialog : Form
{
    private readonly TextBox _name = new() { Width = 360 };
    private readonly ComboBox _game = new() { Width = 360, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _target = new() { Width = 360, PlaceholderText = "e.g. Deoxys - empty = any shiny" };
    private readonly TextBox _notes = new() { Width = 360, Height = 60, Multiline = true };
    private readonly TextBox _time = new() { Width = 120, PlaceholderText = "h:mm:ss" };
    private readonly CheckBox _activate = new() { Text = "Make it the active hunt of its game", AutoSize = true, Checked = true };

    public string HuntName => _name.Text.Trim().Length > 0 ? _name.Text.Trim() : $"{(Target.Length > 0 ? Target : "Shiny")} hunt ({Game?.Title})";
    public Game? Game => _game.SelectedIndex >= 0 ? GameCatalog.All[_game.SelectedIndex] : null;
    public string Target => _target.Text.Trim();
    public string Notes => _notes.Text.Trim();
    public bool MakeActive => _activate.Checked;
    /// <summary>The hunting time typed in (edit only), or null if unchanged / not readable.</summary>
    public double? Seconds { get; private set; }

    public HuntProfileDialog(HuntProfile? p, Game? game)
    {
        Text = p == null ? "New hunt" : "Edit hunt";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        foreach (var g in GameCatalog.All) _game.Items.Add($"{g.Title}  ({g.PlatformLabel})");
        var gi = Array.FindIndex(GameCatalog.All, g => g.Id == (p?.GameId ?? game?.Id));
        _game.SelectedIndex = Math.Max(0, gi);
        foreach (var c in new Control[] { _name, _game, _target, _notes, _time }) Theme.StyleInput(c);
        _activate.ForeColor = Theme.Text;
        if (p != null)
        {
            _name.Text = p.Name;
            _target.Text = p.Target;
            _notes.Text = p.Notes;
            _time.Text = $"{(int)p.Time.TotalHours}:{p.Time.Minutes:00}:{p.Time.Seconds:00}";
            _activate.Checked = p.State == HuntState.Active;
        }

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        void Row(string label, Control c) { grid.Controls.Add(Theme.Label(label, Theme.Body, Theme.Muted)); grid.Controls.Add(c); }
        Row("Name", _name);
        Row("Game", _game);
        Row("Target Pokémon", _target);
        Row("Notes", _notes);
        if (p != null) Row("Hunting time", _time);
        grid.Controls.Add(new Label());
        grid.Controls.Add(_activate);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 100, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 100, FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelHi, ForeColor = Theme.Text };
        grid.Controls.Add(new Label());
        grid.Controls.Add(new FlowLayoutPanel { AutoSize = true, Controls = { ok, cancel } });
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK || p == null) return;
            var parts = _time.Text.Trim().Split(':');
            if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && int.TryParse(parts[2], out var s) && h >= 0 && m is >= 0 and < 60 && s is >= 0 and < 60)
                Seconds = h * 3600 + m * 60 + s;
        };
    }

    /// <summary>A one-line question.</summary>
    public sealed class AskText : Form
    {
        private readonly TextBox _box = new() { Width = 320 };
        public string Value => _box.Text.Trim();

        public AskText(string title, string question, string value)
        {
            Text = title;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);
            Theme.StyleInput(_box);
            _box.Text = value;
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 100, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White };
            var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
            panel.Controls.AddRange(new Control[] { Theme.Label(question, Theme.Body, Theme.Text), _box, ok });
            Controls.Add(panel);
            AcceptButton = ok;
        }
    }
}
