using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>The main window: header with the tool buttons, a banner for problems/updates, the page area and a status bar.</summary>
public sealed class MainWindow : Window
{
    private readonly ContentControl _content = new();
    private readonly TextBlock _crumb = Skin.T("", 13, Skin.Muted, wrap: false);
    private readonly FancyButton _back = new("Back", Skin.GlyphBack, Skin.PanelHi) { IsVisible = false };
    private readonly TextBlock _status = Skin.T("Ready.", 11.5, Skin.Muted, wrap: false);
    private readonly TextBlock _health = Skin.T("● Checking...", 11.5, Skin.Muted, wrap: false);
    private readonly Border _banner = new() { IsVisible = false, Padding = new Thickness(16, 8) };
    private readonly Stack<(Func<Control> Factory, string Crumb)> _history = new();
    private bool? _isSwitch;
    private bool _askingSwitchIp, _closeConfirmed;

    public MainWindow()
    {
        Title = "ShinyForge" + (AppPaths.IsPortable ? "  (portable)" : "");
        Icon = App.Icon;
        Width = 1320; Height = 920;
        MinWidth = 1000; MinHeight = 700;
        Background = Skin.B(Skin.Bg);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;

        // ---- header
        var logo = new Image { Width = 42, Height = 42, Source = App.LoadBitmap(Path.Combine(AppPaths.AssetsDir, "ShinyForge.png")) };
        _back.Click += (_, _) => GoBack();
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center,
            Children = { logo, Skin.T("ShinyForge", 22, Skin.Text, FontWeight.Bold, false), _back, _crumb },
        };
        _crumb.VerticalAlignment = VerticalAlignment.Center;
        var guide = new FancyButton("Guide", Skin.GlyphHelp, Skin.PanelHi);
        guide.Click += async (_, _) => await OpenUserGuide();
        var stats = new FancyButton("Hunt stats", Skin.GlyphInfo, Skin.PanelHi);
        stats.Click += (_, _) => new HuntStatsWindow().Show(this);
        var living = new FancyButton("Living Dex", Skin.GlyphSparkle, Skin.Accent2);
        living.Click += (_, _) => new LivingDexWindow().Show(this);
        var tools = new FancyButton("Tools & Updates", Skin.GlyphTools, Skin.PanelHi);
        tools.Click += async (_, _) => { await new ToolsWindow(_isSwitch).ShowDialog(this); RefreshUpdateBanner(); };
        var settings = new FancyButton("Settings", Skin.GlyphSettings, Skin.PanelHi);
        settings.Click += async (_, _) => await new SettingsWindow().ShowDialog(this);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { guide, stats, living, tools, settings } };
        var header = new Border
        {
            Background = Skin.B(Skin.Panel), Padding = new Thickness(16, 10), BorderBrush = Skin.B(Skin.Accent), BorderThickness = new Thickness(0, 0, 0, 3),
            Child = new DockPanel { Children = { right, left } },
        };
        DockPanel.SetDock(right, Dock.Right);

        // ---- status bar
        _health.Cursor = new Cursor(StandardCursorType.Hand);
        _health.PointerReleased += async (_, _) => { await new HealthWindow().ShowDialog(this); UpdateHealthLabel(); };
        var mode = Skin.T($"v{AppInfo.Display}  •  {OsInfo.Name}  •  " + (AppPaths.IsPortable ? "Portable mode  •  data stored next to the program" : $"data in {AppPaths.DataDir}"), 11.5, Skin.Muted, wrap: false);
        var footerRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { _health, mode } };
        DockPanel.SetDock(footerRight, Dock.Right);
        var footer = new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(14, 5), Child = new DockPanel { Children = { footerRight, _status } } };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_banner, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(_banner);
        root.Children.Add(footer);
        root.Children.Add(_content);
        Content = root;

        // ---- safety checks for the whole program
        HealthMonitor.Updated += () => Dialogs.Ui(UpdateHealthLabel);
        HealthMonitor.Problem += item => Dialogs.Ui(() => OnHealthProblem(item));
        HuntService.SwitchUnreachable += msg => Dialogs.Ui(async () => await AskNewSwitchIp(msg));
        Action updatesChanged = () => Dialogs.Ui(RefreshUpdateBanner);
        DependencyService.UpdatesChanged += updatesChanged;
        Closed += (_, _) => DependencyService.UpdatesChanged -= updatesChanged;
        UpdateHealthLabel();

        Navigate(BuildPlatformPage, "", root: true);
        Opened += async (_, _) =>
        {
            if (App.ScreenshotDir != null) return;
            await ShowBackupFolderNoticeOnce();
            await StartupChecks();
            StartRecheckTimer();
        };
        // Closing ShinyForge stops everything it runs (the hunter used to keep pressing buttons on the Switch on its own) -
        // after asking, when something is running.
        Closing += async (_, e) =>
        {
            if (_closeConfirmed) return;
            var parts = RunningParts.Find();
            if (parts.Count == 0) return;
            e.Cancel = true;
            if (!await Dialogs.YesNo(this, RunningParts.Question(parts), "ShinyForge is still running")) return;
            SetStatus("Stopping " + string.Join(", ", parts.Select(p => p.Name.ToLowerInvariant())) + "...");
            Cursor = new Cursor(StandardCursorType.Wait);
            await Task.Run(() => RunningParts.StopAll(parts));
            _closeConfirmed = true;
            Close();
        };
    }

    public Control? CurrentPage => _content.Content as Control;

    public void SetStatus(string text)
    {
        _status.Text = text;
        ToolTip.SetTip(_status, text);
    }

    // ================================================================ health / safety
    private void UpdateHealthLabel()
    {
        var state = HealthMonitor.Overall;
        var n = HealthMonitor.Items.Count(i => i.State != HealthState.Ok);
        _health.Foreground = Skin.B(state switch { HealthState.Error => Skin.Bad, HealthState.Warning => Skin.Warn, _ => Skin.Good });
        _health.Text = state == HealthState.Ok
            ? $"● All systems OK{(HealthMonitor.LastRun == default ? "" : $" (checked {HealthMonitor.LastRun:HH:mm})")}"
            : $"● {n} problem{(n == 1 ? "" : "s")} - click for details";
    }

    private void OnHealthProblem(HealthItem item)
    {
        SetStatus($"{(item.State == HealthState.Error ? "Problem" : "Warning")} - {item.Feature}: {item.Message}");
        ShowBanner($"{item.Feature}: {item.Message}", health: true);
        Sounds.Play(item.State == HealthState.Error ? Sounds.Kind.Error : Sounds.Kind.Alert);
    }

    /// <summary>The Switch stopped answering during a hunt: ask for its new IP, then restart the hunt with it.</summary>
    private async Task AskNewSwitchIp(string why)
    {
        if (_askingSwitchIp) return;
        _askingSwitchIp = true;
        try
        {
            if (await IpPrompt.AskSwitch(this, why + " Its IP address may have changed.") == null) return;
            if (HuntService.HeldFind is { } held)   // shiny failsafe: reconnecting never restarts (= resets) the game
                SetStatus($"Switch IP set to {AppSettings.Current.SwitchIP}. {held} is held - the hunter waits for you (Keep hunting / Start hunting).");
            else if (HuntService.RestartPythonHunt())
                SetStatus($"Switch IP set to {AppSettings.Current.SwitchIP} - the Python hunter was restarted.");
        }
        finally { _askingSwitchIp = false; }
    }

    // ================================================================ navigation
    private Control BuildPlatformPage()
    {
        _isSwitch = null;
        var p = new PlatformPage();
        p.Chosen += async isSwitch =>
        {
            _isSwitch = isSwitch;
            AppSettings.Current.LastPlatform = isSwitch ? "switch" : "3ds";
            AppSettings.Current.Save();
            await CheckPlatformRequirements();
            // Each console section asks for the console's IP address first (once per run). Cancel still opens the games:
            // recording, trackers and saves don't need the console on the network, and every feature that does asks again.
            var ip = isSwitch ? await IpPrompt.GetOrAskSwitch(this) : await IpPrompt.GetOrAsk(this);
            Navigate(() => BuildGamesPage(isSwitch), $"Nintendo {(isSwitch ? "Switch" : "3DS")}  ({(string.IsNullOrWhiteSpace(ip) ? "no IP yet" : ip)})");
        };
        return p;
    }

    private Control BuildGamesPage(bool isSwitch)
    {
        _isSwitch = isSwitch;
        var p = new GamesPage(isSwitch);
        p.Chosen += game => Navigate(() => BuildGamePage(game), $"{(isSwitch ? "Nintendo Switch" : "Nintendo 3DS")}  ›  Pokémon {game.Title}");
        return p;
    }

    private Control BuildGamePage(Game game)
    {
        var p = new GamePage(game);
        p.StatusMessage += SetStatus;
        return p;
    }

    public void Navigate(Func<Control> factory, string crumb, bool root = false)
    {
        _ = NavigateAsync(factory, crumb, root);
    }

    private async Task NavigateAsync(Func<Control> factory, string crumb, bool root)
    {
        if (_content.Content is GamePage { IsRecording: true } &&
            !await Dialogs.YesNo(this, "A recording is in progress - leaving this game stops it. Continue?"))
            return;
        if (root) _history.Clear();
        _history.Push((factory, crumb));
        ShowPage(factory(), crumb);
    }

    private async void GoBack()
    {
        if (_history.Count <= 1) return;
        if (_content.Content is GamePage { IsRecording: true } &&
            !await Dialogs.YesNo(this, "A recording is in progress - leaving this game stops it. Continue?"))
            return;
        _history.Pop();
        var (factory, crumb) = _history.Peek();
        ShowPage(factory(), crumb);
    }

    private void ShowPage(Control page, string crumb)
    {
        if (_content.Content is IDisposable old) old.Dispose();
        _content.Content = page;
        _crumb.Text = crumb;
        _back.IsVisible = _history.Count > 1;
    }

    /// <summary>Opens the user guide (next to the program) in the default web browser.</summary>
    private async Task OpenUserGuide()
    {
        var path = Path.Combine(AppPaths.AppDir, "ShinyForge User Guide.html");
        if (!File.Exists(path)) { await Dialogs.Info(this, "The user guide wasn't found next to ShinyForge:\n" + path); return; }
        try { Browser.Open(path); }
        catch (Exception ex) { await Dialogs.Info(this, ex.Message); }
    }

    /// <summary>FFmpeg is needed for recording on every system: offer it once when a console is chosen.</summary>
    private async Task CheckPlatformRequirements()
    {
        foreach (var dep in DependencyService.All.Where(d => d.Name == "FFmpeg"))
        {
            DependencyService.Refresh(dep);
            if (dep.State == DepState.Missing)
                await ToolsWindow.AskInstall(this, dep);
        }
    }

    /// <summary>Tells the user where the Backup Tools folder is - only once per user account.</summary>
    private async Task ShowBackupFolderNoticeOnce()
    {
        var flag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShinyForge", "backup_folder_notice.flag");
        var seen = File.Exists(flag) || File.Exists(Path.Combine(AppPaths.DataDir, "backup_folder_notice.flag"));
        try { Directory.CreateDirectory(Path.GetDirectoryName(flag)!); File.WriteAllText(flag, DateTime.Now.ToString("s")); } catch { }
        if (seen) return;
        if (await Dialogs.YesNo(this,
                "ShinyForge has a Backup Tools folder:\n\n" + AppPaths.BackupToolsDir + "\n\n" +
                "When ShinyForge starts it first looks for the programs it needs (FFmpeg, Python, pokeldn, esptool...) on this computer. " +
                "Anything it can't find there, it looks for in this folder - so you can copy programs in by hand as a backup.\n\n" +
                "You can open it any time from Tools & Updates.\n\nOpen the folder now?", "ShinyForge - Backup Tools folder"))
            ToolsWindow.OpenBackupFolder();
    }

    /// <summary>Every start: finds the tools, checks for a new ShinyForge and for tool updates.</summary>
    private async Task StartupChecks()
    {
        await Task.Run(() => { foreach (var d in DependencyService.All) DependencyService.Refresh(d); });
        if (!AppSettings.Current.CheckUpdatesOnStart) return;
        SetStatus("Checking for updates...");
        await CheckAppUpdate(quiet: true);
        SetStatus("Checking for updates...");
        try
        {
            var updates = await DependencyService.CheckUpdates();
            _bannerUpdates = updates.Select(u => u.Item).ToHashSet();
            if (UpdateBannerText() is not { } text) { SetStatus("Everything is up to date."); return; }
            ShowBanner(text);
            SetStatus("Update check finished.");
        }
        catch (Exception e)
        {
            SetStatus("Update check failed (offline?).");
            AppPaths.Log("Update check: " + e);
        }
    }

    /// <summary>The updates the banner already told about: the hourly check only speaks up when something new appears.</summary>
    private HashSet<string> _bannerUpdates = new();
    private readonly Avalonia.Threading.DispatcherTimer _recheck = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool _rechecking;

    /// <summary>
    /// While ShinyForge stays open, the tools (and ShinyForge itself) are checked again every hour - a release made after
    /// start (pokeldn, PKHeX, SysBot...) shows up without restarting. Ticks every 5 minutes so a computer that slept catches up.
    /// </summary>
    private void StartRecheckTimer()
    {
        _recheck.Tick += async (_, _) => await RecheckInBackground();
        _recheck.Start();
        Closed += (_, _) => _recheck.Stop();
    }

    private async Task RecheckInBackground()
    {
        if (_rechecking || !AppSettings.Current.CheckUpdatesOnStart || !DependencyService.CheckIsOlderThan(DependencyService.RecheckEvery)) return;
        _rechecking = true;
        try
        {
            var news = false;
            // ShinyForge itself: only named in the banner (no window pops up in the middle of a hunt); Tools & Updates installs it
            try
            {
                if (await AppUpdateService.CheckAsync() is { IsNewer: true } r && r.Tag != AppSettings.Current.SkippedAppVersion)
                {
                    _appUpdateText = $"ShinyForge {r.VersionText} is available";
                    news |= _bannerUpdates.Add("ShinyForge " + r.Tag);
                }
            }
            catch (Exception e) { AppPaths.Log("ShinyForge update check: " + e.Message); }
            var updates = await DependencyService.CheckUpdates();
            foreach (var u in updates) news |= _bannerUpdates.Add(u.Item);
            AppPaths.Log($"Hourly update check: {(updates.Count == 0 ? "nothing new" : string.Join(", ", updates.Select(u => u.Item)))}");
            // only something new opens the banner; what's in it is always everything still pending
            if (news && UpdateBannerText() is { } text) { ShowBanner(text); SetStatus("Update check finished."); }
        }
        catch (Exception e) { AppPaths.Log("Hourly update check: " + e.Message); }
        finally { _rechecking = false; }
    }

    /// <summary>Asks GitHub whether a newer ShinyForge was released and offers it.</summary>
    public async Task CheckAppUpdate(bool quiet)
    {
        AppRelease? r;
        try { r = await AppUpdateService.CheckAsync(); }
        catch (Exception e)
        {
            AppPaths.Log("ShinyForge update check: " + e.Message);
            if (!quiet) await Dialogs.Info(this, "Couldn't reach GitHub to check for a new version:\n" + e.Message, "ShinyForge update");
            return;
        }
        if (r == null || !r.IsNewer || (quiet && r.Tag == AppSettings.Current.SkippedAppVersion))
        {
            if (!quiet) await Dialogs.Info(this, $"You have the newest ShinyForge: {AppInfo.Display}.", "ShinyForge update");
            return;
        }
        await new UpdateWindow(r).ShowDialog(this);
    }

    /// <summary>"ShinyForge 1.x is available" once the hourly check found it (ShinyForge isn't in DependencyService.Updates).</summary>
    private string? _appUpdateText;
    /// <summary>The banner shows updates (not a health problem): it follows <see cref="DependencyService.UpdatesChanged"/>.</summary>
    private bool _bannerIsUpdates;

    /// <summary>What the update banner says now - pending updates and missing tools - or null when nothing is left.</summary>
    private string? UpdateBannerText()
    {
        var parts = new List<string>();
        if (_appUpdateText != null) parts.Add(_appUpdateText);
        var updates = DependencyService.Updates;
        if (updates.Count > 0) parts.Add($"{updates.Count} update(s) available ({string.Join(", ", updates.Select(u => u.Item))})");
        var missing = DependencyService.All.Where(d => d.State == DepState.Missing && d.Wanted).ToList();
        if (missing.Count > 0) parts.Add($"{missing.Count} optional tool(s) not installed ({string.Join(", ", missing.Select(m => m.Name))})");
        return parts.Count == 0 ? null : string.Join("   •   ", parts);
    }

    /// <summary>
    /// After an update, an install or a new check: the update banner is made again from what's still pending, and closes when
    /// nothing is. A banner the user closed stays closed.
    /// </summary>
    private void RefreshUpdateBanner()
    {
        if (!_bannerIsUpdates || !_banner.IsVisible) return;
        if (UpdateBannerText() is { } text) ShowBanner(text);
        else { _banner.IsVisible = false; _bannerIsUpdates = false; }
    }

    private void ShowBanner(string text, bool health = false)
    {
        _bannerIsUpdates = !health;
        var close = Skin.T("✕", 16, Skin.Muted, wrap: false);
        close.Cursor = new Cursor(StandardCursorType.Hand);
        close.PointerReleased += (_, _) => _banner.IsVisible = false;
        var review = new FancyButton(health ? "Details" : "Review", health ? Skin.GlyphInfo : Skin.GlyphTools, Skin.Accent) { Height = 32 };
        review.Click += async (_, _) =>
        {
            if (health) { await new HealthWindow().ShowDialog(this); UpdateHealthLabel(); return; }
            await new ToolsWindow(_isSwitch).ShowDialog(this);
            RefreshUpdateBanner();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { review, close } };
        close.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(buttons, Dock.Right);
        var msg = Skin.T("⚠  " + text, 13, Skin.Text, wrap: false);
        msg.TextTrimming = TextTrimming.CharacterEllipsis;
        msg.VerticalAlignment = VerticalAlignment.Center;
        _banner.Background = Skin.B(health ? Color.FromRgb(80, 30, 30) : Color.FromRgb(60, 45, 10));
        _banner.Child = new DockPanel { Children = { buttons, msg } };
        ToolTip.SetTip(msg, text);
        _banner.IsVisible = true;
    }
}
