using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>Every outside program ShinyForge uses on this OS (installed / missing / update available), with install and update buttons.</summary>
public sealed class ToolsWindow : Window
{
    private readonly StackPanel _list = new() { Spacing = 8, Margin = new Thickness(20, 10, 20, 10) };
    private readonly TextBlock _status = Skin.T("", 13, Skin.Muted, wrap: false);
    private readonly bool? _isSwitch;

    public ToolsWindow(bool? isSwitch)
    {
        _isSwitch = isSwitch;
        Title = "ShinyForge - Tools & Updates";
        Icon = App.Icon;
        Width = 880; Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Skin.B(Skin.Bg);
        var recheck = new FancyButton("Check for updates", Skin.GlyphSync, Skin.Accent);
        recheck.Click += async (_, _) =>
        {
            _status.Text = "Checking...";
            var found = await DependencyService.CheckUpdates();
            await DependencyService.ReadVersions();
            Populate();
            _status.Text = DependencyService.CheckSummary(found.Count);
        };
        var installAll = new FancyButton("Install all missing", Skin.GlyphDownload, Skin.Accent);
        installAll.Click += async (_, _) => { await InstallAll(this, _isSwitch, everything: true); Populate(); };
        _updateAll.Click += async (_, _) => { await InstallAllUpdates(this); Populate(); _status.Text = DependencyService.CheckSummary(DependencyService.Updates.Count); };
        _status.VerticalAlignment = VerticalAlignment.Center;
        var bottom = new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(16, 10), Child = Skin.Row(recheck, installAll, _updateAll, _status) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var title = Skin.Title("Tools & Updates");
        title.Margin = new Thickness(20, 14, 0, 0);
        DockPanel.SetDock(title, Dock.Top);
        Content = new DockPanel { Children = { title, bottom, new ScrollViewer { Content = _list } } };
        Populate();
        // the installed versions (some programs are asked, which takes a moment): filled in when known
        Opened += async (_, _) =>
        {
            await DependencyService.ReadVersions();
            Populate();
            // the last check is more than 10 minutes old (or never ran): ask again, so a release made since start shows
            if (!DependencyService.CheckIsOlderThan(TimeSpan.FromMinutes(10))) return;
            _status.Text = "Checking for updates...";
            var found = await DependencyService.CheckUpdates();
            Populate();
            _status.Text = DependencyService.CheckSummary(found.Count);
        };
    }

    private Border RowPanel(Color bg, string glyph, Color glyphColor, string name, string detail, Control? button)
    {
        var text = new StackPanel { Spacing = 2, Children = { Skin.H2(name), Skin.Small(detail) } };
        var dock = new DockPanel();
        if (button != null) { DockPanel.SetDock(button, Dock.Right); button.VerticalAlignment = VerticalAlignment.Center; dock.Children.Add(button); }
        var icon = Skin.T(glyph, 20, glyphColor, wrap: false);
        icon.Width = 34;
        icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(text);
        return new Border { Background = Skin.B(bg), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10), Child = dock };
    }

    /// <summary>Installs every update ShinyForge can install itself (greyed out when there's none).</summary>
    private readonly FancyButton _updateAll = new("Install all updates", Skin.GlyphDownload, Skin.Accent);

    /// <summary>Confirms the list, then runs every update one after the other and shows what happened to each.</summary>
    public static async Task InstallAllUpdates(Control? owner)
    {
        var todo = DependencyService.InstallableUpdates;
        var manual = DependencyService.Updates.Where(u => u.Apply == null).ToList();
        if (todo.Count == 0)
        {
            await Dialogs.Info(owner, manual.Count == 0 ? "There are no updates to install." :
                "These updates need you:\n\n" + string.Join("\n\n", manual.Select(u => $"• {u.Item}: {u.Message}")));
            return;
        }
        if (!await Dialogs.YesNo(owner, "Install all updates?\n\n" + string.Join("\n", todo.Select(u => $"  • {u.Item}: {u.Message}")) +
                                        (manual.Count > 0 ? "\n\nThese need you (not installed automatically):\n" + string.Join("\n", manual.Select(u => "  • " + u.Item)) : "") +
                                        "\n\nPrograms that are being updated should be closed.", "ShinyForge - install all updates"))
            return;
        await RunInstall(owner, "Installing updates...", DependencyService.InstallAllUpdates);
    }

    private void Populate()
    {
        _list.Children.Clear();
        _updateAll.IsEnabled = DependencyService.InstallableUpdates.Count > 0;
        var latest = AppUpdateService.Latest;
        var check = new FancyButton("Check now", Skin.GlyphSync, Skin.Accent) { Height = 34 };
        check.Click += async (_, _) => { if (Owner is MainWindow main) await main.CheckAppUpdate(quiet: false); Populate(); };
        _list.Children.Add(RowPanel(Color.FromRgb(36, 30, 70), Skin.GlyphSparkle, Skin.Gold,
            $"ShinyForge {AppInfo.Display} for {OsInfo.Name}   —   " + (latest == null ? "installed" : latest.IsNewer ? $"{latest.VersionText} available" : "up to date"),
            "New versions are published on GitHub: " + AppInfo.ReleasesUrl, check));
        var open = new FancyButton("Open folder", Skin.GlyphFolder, Skin.PanelHi) { Height = 34 };
        open.Click += (_, _) => OpenBackupFolder();
        _list.Children.Add(RowPanel(Color.FromRgb(24, 40, 60), Skin.GlyphTools, Skin.Accent, "Backup Tools folder",
            "Anything not installed on this computer is looked for here (sub-folders too).\n" + AppPaths.BackupToolsDir, open));
        foreach (var d in DependencyService.All.Where(d => _isSwitch is null || (_isSwitch.Value ? d.ForSwitch : d.For3DS)))
        {
            DependencyService.Refresh(d); // keeps "update available" for tools the last check found a newer version of
            var update = DependencyService.UpdateFor(d);
            var problem = d.State == DepState.Ok && DependencyService.CheckedFor(d)?.Problem != null;
            var (glyph, color) = d.State switch
            {
                DepState.Ok => problem ? (Skin.GlyphWarn, Skin.Warn) : (Skin.GlyphCheck, Skin.Good),
                DepState.UpdateAvailable => (Skin.GlyphDownload, Skin.Warn),
                _ => (Skin.GlyphWarn, Skin.Bad),
            };
            var state = DependencyService.StateText(d);
            FancyButton? install = null;
            if (d.State == DepState.Missing)
            {
                install = new FancyButton("Install", Skin.GlyphDownload, Skin.Accent) { Height = 34 };
                var dep = d;
                install.Click += async (_, _) => { await AskInstall(this, dep); Populate(); };
            }
            else if (update?.Apply != null) install = UpdateButton(update);
            _list.Children.Add(RowPanel(update != null ? Color.FromRgb(50, 40, 12) : Skin.Panel, glyph, color,
                $"{d.Name}{(d.Path != null && d.Version != null ? "  " + d.Version : "")}   —   {state}",
                update != null ? update.Message : d.Purpose + (d.State == DepState.Ok ? "\n" + DependencyService.CheckLine(d) : ""), install));
        }
        // updates that aren't a tool on this computer (sys-botbase on the Switch, the PKHeX library, artwork...)
        foreach (var u in DependencyService.Updates.Where(u => DependencyService.All.All(d => d.Name != u.Item)))
            _list.Children.Add(RowPanel(Color.FromRgb(50, 40, 12), Skin.GlyphDownload, Skin.Warn, u.Item, u.Message, u.Apply != null ? UpdateButton(u) : null));
    }

    /// <summary>"Update" for one item: runs it, then redraws the list with the tool's new version (or the error).</summary>
    private FancyButton UpdateButton(DependencyService.UpdateInfo u)
    {
        var apply = new FancyButton("Update", Skin.GlyphDownload, Skin.Accent) { Height = 34 };
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            _status.Text = $"Updating {u.Item}...";
            try
            {
                var msg = await DependencyService.ApplyUpdate(u, s => Dialogs.Ui(() => _status.Text = s));
                Populate();
                _status.Text = msg;
            }
            catch (Exception ex) { _status.Text = ex.Message; apply.IsEnabled = true; }
        };
        return apply;
    }

    public static void OpenBackupFolder()
    {
        AppPaths.EnsureBackupToolsDir();
        OsInfo.OpenFolder(AppPaths.BackupToolsDir);
    }

    /// <summary>Runs an install job behind a small progress window and shows its result.</summary>
    public static async Task RunInstall(Control? owner, string title, Func<Action<string>, Task<string>> job)
    {
        var label = Skin.T(title, 13, Skin.Text);
        var progress = new Window
        {
            Title = "ShinyForge", Width = 520, Height = 150, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Skin.B(Skin.Panel), Icon = App.Icon,
            Content = new StackPanel { Margin = new Thickness(18), Spacing = 14, Children = { label, new ProgressBar { IsIndeterminate = true, Height = 8 } } },
        };
        if (Dialogs.Owner(owner) is { } o) progress.Show(o); else progress.Show();
        string result;
        try { result = await job(s => Dialogs.Ui(() => label.Text = s)); }
        catch (Exception e) { result = e.GetBaseException().Message; }
        finally { progress.Close(); }
        await Dialogs.Info(owner, result);
    }

    /// <summary>Confirms, then installs every missing program for the current console (or all).</summary>
    /// <param name="everything">true = every tool the Tools window shows as "Not installed"; false = only the ones ShinyForge needs now.</param>
    public static async Task InstallAll(Control? owner, bool? isSwitch, bool everything)
    {
        var missing = DependencyService.MissingTools(isSwitch, everything);
        if (missing.Count == 0) { await Dialogs.Info(owner, "Everything is already installed."); return; }
        var auto = missing.Where(d => d.CanAutoInstall || (!OsInfo.IsWindows && d.Name is "FFmpeg" or "Python 3")).Select(d => "  • " + d.Name).ToList();
        var manual = missing.Where(d => !(d.CanAutoInstall || (!OsInfo.IsWindows && d.Name is "FFmpeg" or "Python 3"))).Select(d => "  • " + d.Name).ToList();
        if (!await Dialogs.YesNo(owner, "Install all missing programs?\n\n" +
                                        (auto.Count > 0 ? "Installed automatically (or the exact command is shown):\n" + string.Join("\n", auto) + "\n\n" : "") +
                                        (manual.Count > 0 ? "Can't be installed automatically (their download pages will open):\n" + string.Join("\n", manual) + "\n\n" : ""),
                "ShinyForge - install everything"))
            return;
        await RunInstall(owner, "Installing missing programs...", async p =>
        {
            var lines = new List<string>();
            foreach (var d in missing)
            {
                try { lines.Add(await DependencyService.Install(d, p)); }
                catch (Exception e) { lines.Add($"✗ {d.Name}: {e.GetBaseException().Message}"); }
            }
            return string.Join(Environment.NewLine + Environment.NewLine, lines);
        });
    }

    /// <summary>Asks whether to install a missing program automatically or manually, then does it.</summary>
    public static async Task AskInstall(Control? owner, Dependency d)
    {
        var auto = d.CanAutoInstall || (!OsInfo.IsWindows && d.Name is "FFmpeg" or "Python 3");
        var choice = await Dialogs.Show(owner,
            $"{d.Name} is needed: {d.Purpose}.\n\n" +
            (d.ExeNames.Length > 0 ? $"(You can also put it in the Backup Tools folder:\n{AppPaths.BackupToolsDir})\n\n" : "") +
            (auto ? "Install = let ShinyForge install it (on Linux it shows the exact command to run)\nDownload page = I'll install it myself"
                  : "It can't be installed automatically. Open its download page?"),
            "ShinyForge - install " + d.Name, auto ? new[] { "Install", "Download page", Dialogs.Cancel } : new[] { "Download page", Dialogs.Cancel });
        if (choice == "Install") await RunInstall(owner, $"Installing {d.Name}...", p => DependencyService.Install(d, p));
        else if (choice == "Download page") DependencyService.OpenUrl(d.ManualUrl);
    }
}
