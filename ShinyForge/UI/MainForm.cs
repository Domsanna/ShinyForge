using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

public sealed class MainForm : Form
{
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    private readonly Label _crumb = Theme.Label("", new Font("Segoe UI Semibold", 11f), Theme.Muted);
    private readonly Label _status = Theme.Label("Ready.", Theme.Small, Theme.Muted);
    private readonly FancyButton _back = new("Back", Theme.GlyphBack, Theme.PanelHi) { Visible = false };
    private readonly FancyButton _updates = new("Tools & Updates", Theme.GlyphTools, Theme.PanelHi);
    private readonly Panel _banner = new() { Dock = DockStyle.Top, Height = 0, BackColor = Color.FromArgb(60, 45, 10) };
    private readonly Stack<Func<Control>> _history = new();
    private bool? _isSwitch;

    public MainForm()
    {
        Text = "ShinyForge" + (AppPaths.IsPortable ? "  (portable)" : "");
        Icon = File.Exists(Path.Combine(AppPaths.AssetsDir, "ShinyForge.ico")) ? new Icon(Path.Combine(AppPaths.AssetsDir, "ShinyForge.ico")) : SystemIcons.Application;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        MinimumSize = new Size(1120, 760);
        Size = new Size(1320, 920);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        // ---- header
        var header = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Theme.Panel };
        header.Paint += (_, e) =>
        {
            using var b = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, header.Height - 3, header.Width, 3), Theme.Accent, Theme.Accent2, 0f);
            e.Graphics.FillRectangle(b, 0, header.Height - 3, header.Width, 3);
        };
        var logo = new PictureBox { Size = new Size(44, 44), Location = new Point(16, 10), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        var logoPath = Path.Combine(AppPaths.AssetsDir, "ShinyForge.png");
        if (File.Exists(logoPath)) logo.Image = Image.FromFile(logoPath);
        var name = Theme.Label("ShinyForge", new Font("Segoe UI", 17f, FontStyle.Bold));
        name.Location = new Point(66, 14);
        _back.Location = new Point(220, 14);
        _back.Click += (_, _) => GoBack();
        _crumb.AutoSize = false;     // it gets the room left of the buttons (header.Resize), cut with "..." - it used to
        _crumb.AutoEllipsis = true;  // grow over the Guide button on long game names
        var settings = new FancyButton("Settings", Theme.GlyphSettings, Theme.PanelHi) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _updates.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var livingDex = new FancyButton("Living Dex", Theme.GlyphSparkle, Theme.Accent2) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        livingDex.Click += (_, _) => new LivingDexForm().Show(this);
        var stats = new FancyButton("Hunt stats", Theme.GlyphInfo, Theme.PanelHi) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        stats.Click += (_, _) => new HuntStatsForm().Show(this);
        var guide = new FancyButton("Guide", "" /* Help (question mark) */, Theme.PanelHi) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        guide.Click += (_, _) => OpenUserGuide();
        header.Controls.AddRange(new Control[] { settings, _updates, livingDex, stats, guide, logo, name, _back, _crumb }); // buttons on top
        header.Resize += (_, _) =>
        {
            settings.Location = new Point(header.Width - settings.Width - 16, 14);
            _updates.Location = new Point(settings.Left - _updates.Width - 10, 14);
            livingDex.Location = new Point(_updates.Left - livingDex.Width - 10, 14);
            stats.Location = new Point(livingDex.Left - stats.Width - 10, 14);
            guide.Location = new Point(stats.Left - guide.Width - 10, 14);
            _crumb.Bounds = new Rectangle(330, 22, Math.Max(0, guide.Left - 12 - 330), 24);
        };
        settings.Click += (_, _) => { using var f = new SettingsForm(); f.ShowDialog(this); };
        _updates.Click += (_, _) => { using (var f = new ToolsForm(_isSwitch)) f.ShowDialog(this); RefreshUpdateBanner(); };

        // ---- status bar
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Theme.Panel };
        _status.Location = new Point(14, 6);
        var mode = Theme.Label($"v{AppInfo.Display}  •  " + (AppPaths.IsPortable ? "Portable mode  •  data stored next to the program" : $"Installed mode  •  data in {AppPaths.DataDir}"), Theme.Small, Theme.Muted);
        mode.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _modeLabel = mode;
        _health.Cursor = Cursors.Hand;
        _health.Click += (_, _) => { using var f = new HealthForm(); f.ShowDialog(this); UpdateHealthLabel(); };
        footer.Controls.AddRange(new Control[] { _status, mode, _health });
        footer.Resize += (_, _) =>
        {
            mode.Location = new Point(footer.Width - mode.Width - 14, 6);
            UpdateHealthLabel();
        };

        // ---- safety checks for the whole program
        HealthMonitor.Updated += () => UiSafe(UpdateHealthLabel);
        HealthMonitor.Problem += item => UiSafe(() => OnHealthProblem(item));
        HuntService.SwitchUnreachable += msg => UiSafe(() => AskNewSwitchIp(msg));
        Action updatesChanged = () => UiSafe(RefreshUpdateBanner);
        DependencyService.UpdatesChanged += updatesChanged;
        FormClosed += (_, _) => DependencyService.UpdatesChanged -= updatesChanged;
        UpdateHealthLabel();

        Controls.Add(_content);
        Controls.Add(_banner);
        _banner.Resize += (_, _) => _bannerLayout?.Invoke();
        Controls.Add(footer);
        Controls.Add(header);

        Navigate(BuildPlatformPage, "", root: true);
        Shown += async (_, _) =>
        {
            ShowBackupFolderNoticeOnce();
            await StartupChecks();
            StartRecheckTimer();
        };
        FormClosing += (_, e) =>
        {
            if (_content.Controls.Count > 0 && _content.Controls[0] is GamePage { IsRecording: true } &&
                MessageBox.Show(this, "A recording is in progress. Stop it and exit?", "ShinyForge", MessageBoxButtons.YesNo) == DialogResult.No)
                e.Cancel = true;
        };
    }

    public void SetStatus(string text)
    {
        _status.Text = text;
        _statusTip.SetToolTip(_status, text);
    }

    private readonly ToolTip _statusTip = new();

    // ================================================================ health / safety
    private readonly Label _health = Theme.Label("● Checking...", Theme.Small, Theme.Muted);
    private bool _askingSwitchIp;
    private Label? _modeLabel;

    private void UiSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    private void UpdateHealthLabel()
    {
        var state = HealthMonitor.Overall;
        var n = HealthMonitor.Items.Count(i => i.State != HealthState.Ok);
        _health.ForeColor = state switch { HealthState.Error => Theme.Bad, HealthState.Warning => Theme.Warn, _ => Theme.Good };
        _health.Text = state == HealthState.Ok
            ? $"● All systems OK{(HealthMonitor.LastRun == default ? "" : $" (checked {HealthMonitor.LastRun:HH:mm})")}"
            : $"● {n} problem{(n == 1 ? "" : "s")} - click for details";
        if (_modeLabel != null) _health.Location = new Point(_modeLabel.Left - _health.Width - 24, 6);
        // the status text gets the space left of the health indicator (cut with "..." if longer)
        _status.AutoSize = false;
        _status.AutoEllipsis = true;
        _status.Height = 18;
        _status.Width = Math.Max(100, _health.Left - _status.Left - 20);
    }

    private void OnHealthProblem(HealthItem item)
    {
        SetStatus($"{(item.State == HealthState.Error ? "Problem" : "Warning")} - {item.Feature}: {item.Message}");
        ShowBanner($"{item.Feature}: {item.Message}", health: true);
        try { (item.State == HealthState.Error ? System.Media.SystemSounds.Hand : System.Media.SystemSounds.Exclamation).Play(); } catch { }
    }

    /// <summary>The Switch stopped answering during a hunt: ask for its new IP, then restart the hunt with it.</summary>
    private void AskNewSwitchIp(string why)
    {
        if (_askingSwitchIp) return;
        _askingSwitchIp = true;
        try
        {
            if (IpPrompt.AskSwitch(this, why + " Its IP address may have changed.") == null) return;
            if (HuntService.HeldFind is { } held)   // shiny failsafe: reconnecting never restarts (= resets) the game
                SetStatus($"Switch IP set to {AppSettings.Current.SwitchIP}. {held} is held - the hunter waits for you (Keep hunting / Start hunting).");
            else if (HuntService.RestartPythonHunt())
                SetStatus($"Switch IP set to {AppSettings.Current.SwitchIP} - the Python hunter was restarted.");
        }
        finally { _askingSwitchIp = false; }
    }

    private Control BuildPlatformPage()
    {
        _isSwitch = null;
        var p = new PlatformPage();
        p.Chosen += isSwitch =>
        {
            _isSwitch = isSwitch;
            AppSettings.Current.LastPlatform = isSwitch ? "switch" : "3ds";
            AppSettings.Current.Save();
            CheckPlatformRequirements(isSwitch);
            // Each console section asks for the console's IP address first (once per run). Cancel still opens the games:
            // recording, trackers and saves don't need the console on the network, and every feature that does asks again.
            var ip = isSwitch ? IpPrompt.GetOrAskSwitch(this) : IpPrompt.GetOrAsk(this);
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

    private void Navigate(Func<Control> factory, string crumb, bool root = false)
    {
        if (_content.Controls.Count > 0 && _content.Controls[0] is GamePage { IsRecording: true } &&
            MessageBox.Show(this, "A recording is in progress - leaving this game stops it. Continue?", "ShinyForge", MessageBoxButtons.YesNo) == DialogResult.No)
            return;
        if (root) _history.Clear();
        _history.Push(factory);
        _crumbs.Push(crumb);
        Show(factory(), crumb);
    }

    private readonly Stack<string> _crumbs = new();

    /// <summary>Opens the user guide (ShinyForge User Guide.html next to the program) in the default web browser.</summary>
    private void OpenUserGuide()
    {
        var path = Path.Combine(AppPaths.AppDir, "ShinyForge User Guide.html");
        if (!File.Exists(path)) { MessageBox.Show(this, "The user guide wasn't found next to ShinyForge.exe:\n" + path, "ShinyForge"); return; }
        try { Browser.Open(path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "ShinyForge"); }
    }

    private void GoBack()
    {
        if (_history.Count <= 1) return;
        if (_content.Controls.Count > 0 && _content.Controls[0] is GamePage { IsRecording: true } &&
            MessageBox.Show(this, "A recording is in progress - leaving this game stops it. Continue?", "ShinyForge", MessageBoxButtons.YesNo) == DialogResult.No)
            return;
        _history.Pop();
        _crumbs.Pop();
        Show(_history.Peek()(), _crumbs.Peek());
    }

    private void Show(Control page, string crumb)
    {
        SuspendLayout();
        foreach (Control c in _content.Controls) c.Dispose();
        _content.Controls.Clear();
        page.Dock = DockStyle.Fill;
        _content.Controls.Add(page);
        _crumb.Text = crumb;
        _back.Visible = _history.Count > 1;
        ResumeLayout();
    }

    /// <summary>Required software per system: Switch needs capture software (Streamlabs) + FFmpeg; 3DS needs 3DS Capture + FFmpeg.</summary>
    private void CheckPlatformRequirements(bool isSwitch)
    {
        var needed = isSwitch ? new[] { "Streamlabs Desktop", "FFmpeg" } : new[] { "3DS Capture", "FFmpeg" };
        foreach (var name in needed)
        {
            var dep = DependencyService.All.First(d => d.Name == name);
            DependencyService.Refresh(dep);
            if (dep.State == DepState.Missing)
                ToolsForm.AskInstall(this, dep);
        }
    }

    /// <summary>
    /// Tells the user where the Backup Tools folder is - only ONCE per Windows user: the "seen" mark is kept in
    /// %LocalAppData%\ShinyForge (shared by every copy: installed, portable, other drives), so new copies don't ask again.
    /// </summary>
    private void ShowBackupFolderNoticeOnce()
    {
        var userFlag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShinyForge", "backup_folder_notice.flag");
        var oldFlags = new[]
        {
            Path.Combine(AppPaths.DataDir, "backup_folder_notice.flag"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShinyForge", "backup_folder_notice.flag"),
        };
        var seen = File.Exists(userFlag) || oldFlags.Any(File.Exists);
        try { Directory.CreateDirectory(Path.GetDirectoryName(userFlag)!); File.WriteAllText(userFlag, DateTime.Now.ToString("s")); } catch { }
        if (seen) return;
        var r = MessageBox.Show(this,
            "ShinyForge has a Backup Tools folder:\n\n" + AppPaths.BackupToolsDir + "\n\n" +
            "When ShinyForge starts it first looks for the programs it needs (FFmpeg, PKHeX, SysBot, 3DS Capture, Python...) " +
            "on this computer. Anything it can't find there, it looks for in this folder - so you can copy programs in by hand " +
            "as a backup.\n\nYou can open it any time from Tools & Updates.\n\nOpen the folder now?",
            "ShinyForge - Backup Tools folder", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r == DialogResult.Yes) ToolsForm.OpenBackupFolder();
    }

    /// <summary>Shows the window on top (ShinyForge was started again while it's open).</summary>
    public void BringToFront(bool activate)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        BringToFront();
        if (activate) Activate();
    }

    /// <summary>Runs every time the program starts: checks the outside programs and downloadable files for updates.</summary>
    private async Task StartupChecks()
    {
        // Always at boot: look for every tool on the computer first, then in the Backup Tools folder.
        await Task.Run(() => { foreach (var d in DependencyService.All) DependencyService.Refresh(d); });
        if (!AppSettings.Current.CheckUpdatesOnStart) return;
        SetStatus("Checking for updates...");
        if (await CheckAppUpdate(quiet: true)) return; // a newer ShinyForge is being installed - this copy closes
        SetStatus("Checking for updates...");
        try
        {
            var updates = await DependencyService.CheckUpdates();
            _bannerUpdates = updates.Select(u => u.Item).ToHashSet();
            if (UpdateBannerText() is not { } text)
            {
                SetStatus("Everything is up to date.");
                return;
            }
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
    private readonly System.Windows.Forms.Timer _recheck = new() { Interval = 5 * 60 * 1000 };
    private bool _rechecking;

    /// <summary>
    /// While ShinyForge stays open, the tools (and ShinyForge itself) are checked again every hour - a release made after
    /// start (pokeldn, PKHeX, SysBot...) shows up without restarting. Ticks every 5 minutes so a PC that slept catches up.
    /// </summary>
    private void StartRecheckTimer()
    {
        _recheck.Tick += async (_, _) => await RecheckInBackground();
        _recheck.Start();
        FormClosed += (_, _) => _recheck.Dispose();
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

    /// <summary>
    /// Asks GitHub whether a newer ShinyForge was released and offers it. quiet = at start-up: no message when up to date
    /// or offline, and a skipped version isn't offered again. Returns true when the update started (ShinyForge closes).
    /// </summary>
    public async Task<bool> CheckAppUpdate(bool quiet)
    {
        AppRelease? r;
        try { r = await AppUpdateService.CheckAsync(); }
        catch (Exception e)
        {
            AppPaths.Log("ShinyForge update check: " + e.Message);
            if (!quiet) MessageBox.Show(this, "Couldn't reach GitHub to check for a new version:\n" + e.Message, "ShinyForge update");
            return false;
        }
        if (r == null || !r.IsNewer || (quiet && r.Tag == AppSettings.Current.SkippedAppVersion))
        {
            if (!quiet) MessageBox.Show(this, $"You have the newest ShinyForge: {AppInfo.Display}.", "ShinyForge update");
            return false;
        }
        using var f = new UpdateForm(r);
        f.ShowDialog(this);
        if (!f.CloseApp) return false;
        Close();
        return true;
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
    /// nothing is (it used to keep saying e.g. "1 update(s) available (pokeldn)" after pokeldn was updated). A banner the user
    /// closed stays closed.
    /// </summary>
    private void RefreshUpdateBanner()
    {
        if (!_bannerIsUpdates || _banner.Height == 0) return;
        if (UpdateBannerText() is { } text) ShowBanner(text);
        else { _banner.Height = 0; _bannerIsUpdates = false; }
    }

    private void ShowBanner(string text, bool health = false)
    {
        _bannerIsUpdates = !health;
        _banner.Controls.Clear();
        _banner.Height = 44;
        _banner.BackColor = health ? Color.FromArgb(80, 30, 30) : Color.FromArgb(60, 45, 10);
        var icon = Theme.Label(Theme.GlyphWarn, Theme.Icons, Theme.Warn);
        icon.Location = new Point(16, 11);
        var label = Theme.Label(text, Theme.Body, Theme.Text, autoSize: false);
        label.AutoEllipsis = true;
        var review = new FancyButton(health ? "Details" : "Review", health ? Theme.GlyphInfo : Theme.GlyphTools, Theme.Accent) { Height = 32 };
        review.Click += (_, _) =>
        {
            if (health) { using var h = new HealthForm(); h.ShowDialog(this); UpdateHealthLabel(); return; }
            using (var f = new ToolsForm(_isSwitch)) f.ShowDialog(this);
            RefreshUpdateBanner();
        };
        var installAll = new FancyButton("Install all", Theme.GlyphDownload, Theme.PanelHi) { Height = 32 };
        installAll.Click += (_, _) => { ToolsForm.InstallAll(this, _isSwitch, everything: false); RefreshUpdateBanner(); };
        installAll.Visible = !health && DependencyService.All.Any(d => d.State == DepState.Missing && d.Wanted);
        var close = Theme.Label("✕", Theme.H2, Theme.Muted);
        close.Cursor = Cursors.Hand;
        close.Click += (_, _) => _banner.Height = 0;
        // Buttons first = on top of the text. Positions come from the banner's CURRENT width on every resize: the hourly
        // check can show the banner while the window is hidden in the tray (width ~0), and fixed positions + anchors then
        // stretched the text over the Review button once the window came back.
        _banner.Controls.AddRange(new Control[] { close, review, installAll, icon, label });
        _bannerLayout = () =>
        {
            int w = _banner.ClientSize.Width;
            int bh = _banner.Height > 0 ? _banner.Height : 44;
            close.Location = new Point(w - close.Width - 14, (bh - close.Height) / 2);
            review.Location = new Point(close.Left - review.Width - 14, (bh - review.Height) / 2);
            installAll.Location = new Point(review.Left - installAll.Width - 10, review.Top);
            int textRight = (installAll.Visible ? installAll.Left : review.Left) - 12;
            label.Bounds = new Rectangle(46, (bh - 22) / 2, Math.Max(0, textRight - 46), 22);
        };
        _bannerLayout();
    }

    private Action? _bannerLayout;
}
