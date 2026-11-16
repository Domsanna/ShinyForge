using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>A simple list with column headers and selectable rows (the Windows version's ListView in details mode).</summary>
public sealed class Table : Border
{
    private readonly StackPanel _rows = new();
    private readonly double[] _widths;
    private Border? _selected;
    public object? SelectedTag { get; private set; }
    public event Action<object?>? DoubleClicked;
    public event Action<object?>? SelectionChanged;

    public Table(params (string Header, double Width)[] columns)
    {
        _widths = columns.Select(c => c.Width).ToArray();
        Background = Skin.B(Skin.Panel);
        CornerRadius = new CornerRadius(8);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Background = Skin.B(Skin.PanelHi) };
        foreach (var (h, w) in columns) head.Children.Add(Cell(h, w, Skin.Gold, FontWeight.SemiBold));
        Child = new DockPanel { Children = { new Border { [DockPanel.DockProperty] = Dock.Top, Child = head }, new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } } };
    }

    private static TextBlock Cell(string t, double w, Color c, FontWeight weight = FontWeight.Normal)
    {
        var b = Skin.T(t, 12.5, c, weight, false);
        b.Width = w;
        b.Padding = new Thickness(8, 6);
        b.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(b, t);
        return b;
    }

    public void Clear() { _rows.Children.Clear(); _selected = null; SelectedTag = null; }

    public void Add(object? tag, Color color, params string[] cells)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < cells.Length && i < _widths.Length; i++) row.Children.Add(Cell(cells[i], _widths[i], color));
        var b = new Border { Child = row, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        b.PointerPressed += (_, e) =>
        {
            if (_selected != null) _selected.Background = Brushes.Transparent;
            _selected = b;
            b.Background = Skin.B(Skin.Accent);
            SelectedTag = tag;
            SelectionChanged?.Invoke(tag);
            if (e.ClickCount == 2) DoubleClicked?.Invoke(tag);
        };
        _rows.Children.Add(b);
        if (_rows.Children.Count == 1) { _selected = b; b.Background = Skin.B(Skin.Accent); SelectedTag = tag; }
    }
}

/// <summary>
/// The program-wide safety checks. Selecting a problem shows ShinyForge's diagnosis with step-by-step solutions and,
/// when it has one, a fix it can try itself; after that the problem can be reported to GitHub.
/// Minor problems (they don't affect hunting/playing) are hidden unless "Show minor issues" is ticked.
/// </summary>
public sealed class HealthWindow : Window
{
    private readonly Table _list = new(("", 30), ("Time", 70), ("Feature", 220), ("Details", 640));
    private readonly TextBlock _summary = Skin.T("", 13, Skin.Text, wrap: false);
    private readonly CheckBox _minor = new() { Foreground = Skin.B(Skin.Muted), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _cause = Skin.T("", 13.5, Skin.Text, FontWeight.SemiBold);
    private readonly TextBlock _steps = Skin.Small("");
    private readonly TextBlock _result = Skin.Small("");
    private readonly FancyButton _fix = new("Try the fix", Skin.GlyphTools, Skin.Accent);
    private readonly FancyButton _report = new("Report to GitHub", Skin.GlyphInfo, Skin.PanelHi);
    /// <summary>Fix results per problem (feature|message), so the report says what was already tried.</summary>
    private readonly Dictionary<string, string> _fixResults = new();
    private HealthItem? _item;
    private Diagnosis? _diag;

    public HealthWindow()
    {
        Title = "ShinyForge - system check";
        Icon = App.Icon;
        Width = 1000; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Skin.B(Skin.Panel);
        var top = new StackPanel
        {
            Margin = new Thickness(16, 12), Spacing = 6,
            Children = { Skin.H2("System check"), Skin.Small("Every minute ShinyForge checks everything that's running: hunting bots (same Pokémon repeating, stalls, crashes), recordings, collection monitoring, the Switch / 3DS connections, disk space and its own files. Select a problem to see what's wrong, how to solve it, and a fix ShinyForge can try itself.") },
        };
        var check = new FancyButton("Check now", Skin.GlyphSync, Skin.Accent);
        check.Click += async (_, _) => { check.IsEnabled = false; await Task.Run(HealthMonitor.RunNow); Fill(); check.IsEnabled = true; };
        var clear = new FancyButton("Clear reported problems", Skin.GlyphCheck, Skin.PanelHi);
        clear.Click += (_, _) => { HealthMonitor.Clear(); Fill(); };
        var log = new FancyButton("Open log", Skin.GlyphFolder, Skin.PanelHi);
        log.Click += (_, _) => { try { OsInfo.Open(_minor.IsChecked == true && File.Exists(ErrorDoctor.IssuesLog) ? ErrorDoctor.IssuesLog : AppPaths.LogFile); } catch { } };
        _minor.IsCheckedChanged += (_, _) => Fill();
        _summary.VerticalAlignment = VerticalAlignment.Center;
        var bottom = new Border { Padding = new Thickness(10, 8), Child = Skin.Row(check, clear, log, _minor, _summary) };

        _fix.Click += async (_, _) => await RunFix();
        _report.Click += async (_, _) => await Report();
        _result.VerticalAlignment = VerticalAlignment.Center;
        _result.MaxWidth = 560;
        var detail = new StackPanel
        {
            Margin = new Thickness(16, 10, 16, 4), Spacing = 6,
            Children = { _cause, _steps, Skin.Row(_fix, _report, _result) },
        };
        _list.SelectionChanged += tag => ShowDiagnosis(tag as HealthItem);

        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(detail, Dock.Bottom);
        _list.Margin = new Thickness(10, 0);
        Content = new DockPanel { Children = { top, bottom, detail, _list } };
        Fill();
    }

    private static string Key(HealthItem i) => i.Feature + "|" + i.Message;

    private void Fill()
    {
        _list.Clear();
        var gameplay = HealthMonitor.Items;
        var minor = HealthMonitor.MinorItems;
        _minor.Content = $"Show minor issues ({minor.Count})";
        foreach (var i in _minor.IsChecked == true ? gameplay.Concat(minor) : gameplay)
            _list.Add(i, !i.AffectsGameplay ? Skin.Muted : i.State switch { HealthState.Error => Skin.Bad, HealthState.Warning => Skin.Warn, _ => Skin.Good },
                i.State switch { HealthState.Error => "✖", HealthState.Warning => "!", _ => "✓" }, i.When.ToString("HH:mm"),
                i.AffectsGameplay ? i.Feature : i.Feature + " (minor)", i.Message);
        _summary.Text = gameplay.Count == 0
            ? $"Everything that affects hunting is working{(HealthMonitor.LastRun == default ? "." : $" (last check {HealthMonitor.LastRun:HH:mm:ss}).")}"
            : $"{gameplay.Count(i => i.State == HealthState.Error)} error(s), {gameplay.Count(i => i.State == HealthState.Warning)} warning(s).";
        _summary.Foreground = Skin.B(gameplay.Count == 0 ? Skin.Good : Skin.Warn);
        ShowDiagnosis(_list.SelectedTag as HealthItem);   // the first row is selected after a refill
    }

    private void ShowDiagnosis(HealthItem? item)
    {
        _item = item;
        _diag = item == null ? null : ErrorDoctor.Diagnose(item);
        if (item == null || _diag == null)
        {
            _cause.Text = "No problems.";
            _steps.Text = "When something goes wrong it appears in the list above with ShinyForge's diagnosis and a way to fix it.";
            _fix.IsVisible = _report.IsVisible = false;
            _result.Text = "";
            return;
        }
        _cause.Text = (item.AffectsGameplay ? "" : "Minor (doesn't affect hunting) - ") + _diag.Cause;
        _steps.Text = "How to solve it:\n" + string.Join("\n", _diag.Steps.Select((s, n) => $"  {n + 1}. {s}"));
        _fix.IsVisible = _diag.Fix != null;
        if (_diag.FixLabel != null) _fix.Text = _diag.FixLabel;
        _report.IsVisible = true;
        _fixResults.TryGetValue(Key(item), out var done);
        _result.Text = done ?? (_diag.Fix != null ? "Try the fix first - if the problem stays, report it." : "");
        _result.Foreground = Skin.B(done != null ? Skin.Good : Skin.Muted);
    }

    private async Task RunFix()
    {
        if (_item is not { } item || _diag is not { Fix: { } fix } d) return;
        _fix.IsEnabled = false;
        _result.Foreground = Skin.B(Skin.Muted);
        _result.Text = "Working on it...";
        string msg;
        try { msg = await fix(); }
        catch (Exception e) { msg = "The fix failed: " + e.Message; }
        AppPaths.Log($"[health] fix '{d.FixLabel}' for {item.Feature}: {msg}");
        _fixResults[Key(item)] = msg;
        _fix.IsEnabled = true;
        await Task.Run(HealthMonitor.RunNow);
        Fill();
        if (_item == null || Key(_item) != Key(item)) { _result.Text = msg; _result.Foreground = Skin.B(Skin.Good); }
    }

    private async Task Report()
    {
        if (_item is not { } item || _diag is not { } d) return;
        _fixResults.TryGetValue(Key(item), out var fixResult);
        if (d.Fix != null && fixResult == null &&
            await Dialogs.YesNo(this, $"ShinyForge can try to fix this itself (\"{d.FixLabel}\"). Try that before reporting it?", "Report to GitHub"))
        {
            await RunFix();
            return;
        }
        if (await Dialogs.Show(this, ErrorDoctor.ReportPrompt(item, d, fixResult), "Report to GitHub", "Open GitHub", Dialogs.Cancel) != "Open GitHub") return;
        DependencyService.OpenUrl(ErrorDoctor.ReportUrl(item, d, fixResult));
        AppPaths.Log($"[health] opened a GitHub report for {item.Feature}");
        _result.Foreground = Skin.B(Skin.Good);
        _result.Text = "Opened the report in your browser - press \"Create\" there to send it. Thank you!";
    }
}

/// <summary>The automatic backups of saves and spreadsheets; restores one.</summary>
public sealed class BackupsWindow : Window
{
    private readonly Table _list = new(("When", 150), ("Type", 100), ("File", 250), ("Size", 90), ("Original location", 380));
    private readonly TextBlock _info = Skin.Small("");

    public BackupsWindow()
    {
        Title = "ShinyForge - backups";
        Icon = App.Icon;
        Width = 1000; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Skin.B(Skin.Panel);
        var restore = new FancyButton("Restore selected", Skin.GlyphSync, Skin.Accent);
        restore.Click += async (_, _) => await Restore();
        var open = new FancyButton("Open backup folder", Skin.GlyphFolder, Skin.PanelHi);
        open.Click += (_, _) => { Directory.CreateDirectory(BackupService.Root); OsInfo.OpenFolder(BackupService.Root); };
        _info.VerticalAlignment = VerticalAlignment.Center;
        var top = Skin.Small("ShinyForge copies your save files and tracker spreadsheets automatically before it reads or changes them (tracker sync, Pokémon HOME export).\nRestoring puts the copy back - the current file is backed up first, so a restore can be undone.");
        top.Margin = new Thickness(12, 10);
        var bottom = new Border { Padding = new Thickness(10, 8), Child = Skin.Row(restore, open, _info) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        _list.Margin = new Thickness(10, 0);
        Content = new DockPanel { Children = { top, bottom, _list } };
        Fill();
    }

    private void Fill()
    {
        _list.Clear();
        var all = BackupService.List();
        foreach (var b in all)
            _list.Add(b, Skin.Text, b.When.ToString("yyyy-MM-dd HH:mm:ss"), b.Kind, Path.GetFileName(b.Original), b.Size > 1_000_000 ? $"{b.Size / 1e6:0.0} MB" : $"{b.Size / 1e3:0} KB", b.Original);
        _info.Text = $"{all.Count} backup(s), {BackupService.TotalSize() / 1e6:0} MB in {BackupService.Root}";
    }

    private async Task Restore()
    {
        if (_list.SelectedTag is not BackupEntry b) return;
        if (!await Dialogs.YesNo(this, $"Restore the backup from {b.When:yyyy-MM-dd HH:mm:ss} over\n{b.Original}?\n\nClose the game / Excel / PKHeX if they have this file open. The current file is backed up first.", "Restore backup")) return;
        try { BackupService.Restore(b); await Dialogs.Info(this, "Restored.", "Restore backup"); Fill(); }
        catch (Exception e) { await Dialogs.Info(this, e.Message, "Restore backup"); }
    }
}

/// <summary>Several Checkpoint backups of the game were found: the user picks one (newest first, preselected).</summary>
public sealed class CheckpointPicker : Window
{
    private readonly Table _list = new(("Backup", 180), ("Saved", 140), ("Trainer", 140), ("Play time", 90), ("Where", 290));
    public CheckpointBackup? Chosen { get; private set; }

    public CheckpointPicker(string gameTitle, IReadOnlyList<CheckpointBackup> backups)
    {
        Title = "ShinyForge - choose a Checkpoint backup";
        Icon = App.Icon;
        Width = 900; Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Skin.B(Skin.Bg);
        foreach (var b in backups)
        {
            var s = b.Save.Save;
            _list.Add(b, Skin.Text, b.BackupName, b.Time.ToString("yyyy-MM-dd HH:mm"), $"{s.OT} ({s.DisplayTID})", $"{s.PlayedHours}h {s.PlayedMinutes:00}m", Path.GetFileName(b.TitleFolder) + "  •  " + b.Drive);
        }
        var head = Skin.H2($"{backups.Count} Checkpoint backups of Pokémon {gameTitle} were found. Which one should ShinyForge use?");
        head.Margin = new Thickness(16, 12);
        var use = new FancyButton("Use this backup", Skin.GlyphCheck, Skin.Accent);
        var cancel = new FancyButton("Cancel", "", Skin.PanelHi);
        use.Click += (_, _) => { Chosen = _list.SelectedTag as CheckpointBackup; Close(); };
        cancel.Click += (_, _) => Close();
        _list.DoubleClicked += t => { Chosen = t as CheckpointBackup; Close(); };
        var bottom = new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(12, 8), Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, use } } };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        _list.Margin = new Thickness(16, 0, 16, 8);
        Content = new DockPanel { Children = { head, bottom, _list } };
    }
}

/// <summary>"A new version of ShinyForge is available": what's new, then download (installed copies on Windows) or the release page.</summary>
public sealed class UpdateWindow : Window
{
    public UpdateWindow(AppRelease r)
    {
        Title = "ShinyForge - update available";
        Icon = App.Icon;
        Width = 760; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Skin.B(Skin.Bg);
        var head = new StackPanel
        {
            Margin = new Thickness(20, 14), Spacing = 6,
            Children =
            {
                Skin.Title($"ShinyForge {r.VersionText} is available"),
                Skin.MutedText($"You have {AppInfo.Display}.  Released {(r.Published == default ? "" : r.Published.ToLocalTime().ToString("yyyy-MM-dd"))}" +
                            (OsInfo.IsWindows ? "" : $"  •  the {OsInfo.Name} download is on the release page")),
            },
        };
        var notes = new SelectableTextBlock { Text = "What's new\n\n" + Plain(r.Notes), TextWrapping = TextWrapping.Wrap, Foreground = Skin.B(Skin.Text), FontSize = 13, Margin = new Thickness(20, 0) };
        var progress = Skin.T("", 13, Skin.Muted, wrap: false);
        var install = new FancyButton(OsInfo.IsWindows ? "Download and install" : "Open the release page", Skin.GlyphDownload, Skin.Accent);
        var skip = new FancyButton("Skip this version", Skin.GlyphStop, Skin.PanelHi);
        var later = new FancyButton("Later", Skin.GlyphBack, Skin.PanelHi);
        later.Click += (_, _) => Close();
        skip.Click += (_, _) => { AppSettings.Current.SkippedAppVersion = r.Tag; AppSettings.Current.Save(); Close(); };
        install.Click += async (_, _) =>
        {
            install.IsEnabled = skip.IsEnabled = later.IsEnabled = false;
            try
            {
                if (await AppUpdateService.InstallAsync(r, s => Dialogs.Ui(() => progress.Text = s)))
                    (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                else Close();
            }
            catch (Exception ex)
            {
                progress.Text = "Update failed: " + ex.Message;
                install.IsEnabled = skip.IsEnabled = later.IsEnabled = true;
            }
        };
        var bottom = new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(14, 10), Child = Skin.Wrap(install, skip, later, progress) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new DockPanel { Children = { head, bottom, new ScrollViewer { Content = notes } } };
    }

    /// <summary>Release notes are Markdown: show them as plain text.</summary>
    public static string Plain(string md) =>
        string.Join("\n", md.Replace("\r", "").Split('\n').Select(line =>
        {
            var l = line.Replace("**", "").Replace("`", "");
            var t = l.TrimStart();
            var indent = l.Length - t.Length;
            if (t.StartsWith('#')) return t.TrimStart('#', ' ').ToUpperInvariant();
            if (t.StartsWith("- ") || t.StartsWith("* ")) return new string(' ', indent) + "•  " + t[2..];
            return l;
        }));
}
