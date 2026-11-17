using System.Diagnostics;
using PKHeX.Core;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

public sealed class GamePage : UserControl
{
    private readonly Game _game;
    private readonly GameSettings _gs;
    private readonly RecordingService _recorder = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    private FileSystemWatcher? _watcher;
    private System.Windows.Forms.Timer? _liveTimer;
    private DateTime _lastAutoSync = DateTime.MinValue;
    private bool _syncing;

    // controls referenced across handlers
    private Label _huntStatus = null!, _recStatus = null!, _saveStatus = null!, _sheetLabel = null!;
    private TextBox _trackLog = null!, _folderBox = null!, _recFolder = null!;
    private Label? _trackSaveLabel;
    private ComboBox _source = null!, _audio = null!, _segments = null!, _trackSource = null!;
    private ComboBox? _overlay;
    private CheckBox? _crop;
    private List<string> _overlayKeys = new();
    private FancyButton _recButton = null!;
    private CheckBox _autoMonitor = null!;
    private FoundSave? _save;
    public event Action<string>? StatusMessage;
    public bool IsRecording => _recorder.IsRecording;

    public GamePage(Game game)
    {
        _game = game;
        _gs = AppSettings.Current.For(game.Id);
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        AutoScroll = true;

        var header = new GameTile(game, large: true) { Location = new Point(24, 14) };
        var info = new Label
        {
            Location = new Point(560, 30), Size = new Size(640, 150), ForeColor = Theme.Muted, Font = Theme.Body, BackColor = Theme.Bg,
            Text = $"{game.PlatformLabel}  •  Generation {game.Gen}\n\n" +
                   (game.Bot != BotKind.None ? "★ Automatic shiny hunting available.\n" : "") +
                   "Record gameplay in timed segments, keep your Pokédex tracker spreadsheet up to date automatically, " +
                   "back up every Pokémon from your save into a 'Pokemon Home' folder and get a Game Info report.",
        };
        Controls.Add(header);
        Controls.Add(info);

        var grid = new TableLayoutPanel
        {
            Location = new Point(18, 214), Size = new Size(1230, 1100), ColumnCount = 2, RowCount = 2, BackColor = Theme.Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 500));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 590));
        grid.Controls.Add(BuildHuntCard(), 0, 0);
        grid.Controls.Add(BuildRecordCard(), 1, 0);
        grid.Controls.Add(BuildTrackerCard(), 0, 1);
        grid.Controls.Add(BuildSaveCard(), 1, 1);
        Controls.Add(grid);
        Resize += (_, _) => grid.Width = Math.Max(900, ClientSize.Width - 40);

        _recorder.Status += s => Ui(() => _recStatus.Text = s);
        _poll.Tick += (_, _) => RefreshHuntStatus();
        _poll.Start();
        if (_gs.AutoMonitor)
            Ui(() => _autoMonitor.Checked = true);

        // Safety checks for this game's features, run every minute by the HealthMonitor.
        _healthKey = "game-" + game.Id;
        HealthMonitor.Register(_healthKey, CheckHealth);
        if (game.Bot == BotKind.FrlgSwitch) HuntService.Found += OnFound;
        Disposed += (_, _) =>
        {
            HealthMonitor.Unregister(_healthKey);
            HuntService.Found -= OnFound;
            _clips.Stop();
        };
    }

    private readonly string _healthKey;

    private IEnumerable<HealthItem> CheckHealth()
    {
        var items = new List<HealthItem>();
        if (_recorder.CheckHealth() is { } r)
            items.Add(r.Error ? HealthMonitor.Error($"Recording ({_game.Title})", r.Message) : HealthMonitor.Warn($"Recording ({_game.Title})", r.Message));
        if (_gs.AutoMonitor && !_trackLive && !Directory.Exists(_gs.GameFolder))
            items.Add(HealthMonitor.Warn($"Collection tracker ({_game.Title})", $"Monitoring is on but the game folder '{_gs.GameFolder}' doesn't exist (drive unplugged?)."));
        if (_gs.AutoMonitor && _trackLive)
        {
            var s = AppSettings.Current;
            if (!HealthMonitor.SwitchReachable(2500))
                items.Add(HealthMonitor.Error($"Collection tracker ({_game.Title})", $"Live monitoring can't reach the Switch at {s.SwitchIP} - its IP may have changed (Settings, or start a hunt to enter it)."));
        }
        if (_hunt3ds != null && (DateTime.Now - _last3dsActivity).TotalMinutes > 4)
            items.Add(HealthMonitor.Warn($"3DS hunt ({_game.Title})", $"No activity from the 3DS bot for {(int)(DateTime.Now - _last3dsActivity).TotalMinutes} minutes - is the game still running?"));
        if (_trackError != null)
            items.Add(HealthMonitor.Warn($"Collection tracker ({_game.Title})", "Last sync failed: " + _trackError));
        if (_clips.Problem() is { } cp)
        {
            items.Add(HealthMonitor.Warn($"Shiny clips ({_game.Title})", cp + " - restarting it."));
            Ui(StartClipBuffer);
        }
        return items;
    }

    private volatile string? _trackError;

    // ================================================================ helpers
    private static FlowLayoutPanel Row(params Control[] items)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Panel, Margin = new Padding(0, 4, 0, 4) };
        row.Controls.AddRange(items);
        return row;
    }

    private static Label Info(string t, Color? c = null, int width = 540) =>
        new() { Text = t, ForeColor = c ?? Theme.Muted, Font = Theme.Body, AutoSize = true, MaximumSize = new Size(width, 0), BackColor = Theme.Panel, Margin = new Padding(0, 4, 0, 4) };

    private static ComboBox Combo(int width, IEnumerable<string> items, string? selected)
    {
        var cb = new ComboBox { Width = width };
        Theme.StyleInput(cb);
        cb.Items.AddRange(items.Cast<object>().ToArray());
        if (selected != null && cb.Items.Contains(selected)) cb.SelectedItem = selected;
        else if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        return cb;
    }

    private static string? PickFolder(string title, string current)
    {
        using var dlg = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, SelectedPath = Directory.Exists(current) ? current : "" };
        return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
    }

    private void Status(string s) => StatusMessage?.Invoke(s);

    // ---- thread-safe UI updates that also work before the page's window exists
    private readonly List<Action> _pendingUi = new();

    /// <summary>Runs <paramref name="action"/> on the UI thread; if the page isn't shown yet it runs as soon as it is.</summary>
    private void Ui(Action action)
    {
        if (IsDisposed || Disposing) return;
        if (!IsHandleCreated)
        {
            lock (_pendingUi) _pendingUi.Add(action);
            return;
        }
        try { base.BeginInvoke(action); }
        catch (InvalidOperationException) { /* closing */ }
    }

    private void Ui(Func<Task> action) => Ui(() => { _ = action(); });

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        List<Action> queued;
        lock (_pendingUi) { queued = _pendingUi.ToList(); _pendingUi.Clear(); }
        foreach (var a in queued) base.BeginInvoke(a);
    }

    private static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Before any hunt or tool (the user's rule): SysBot.exe must be on its latest release. Outdated → asks to update first
    /// (Yes = update, No = continue this time, Cancel = stop). A copy built from source is updated by rebuilding it, so Yes
    /// opens the download page and stops. Offline / not installed = nothing to check.
    /// </summary>
    private async Task<bool> SysBotCurrent()
    {
        if (await DependencyService.SysBotOutdated() is not { } o) return true;
        var r = MessageBox.Show(FindForm(),
            $"SysBot.exe is version {o.Installed}, but {o.Latest} is out. Please update it before continuing.\n\n" +
            (o.SelfBuilt
                ? "Your copy is built from source (with your own routines, such as FRLG), so it's updated by getting the new source " +
                  "and building it again.\n\nYes = open the download page (and stop here)\n"
                : "Yes = update it now, then continue\n") +
            "No = continue this time without updating\nCancel = stop",
            "SysBot update", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (r == DialogResult.No) return true;
        if (r != DialogResult.Yes) return false;
        if (o.SelfBuilt) { DependencyService.OpenUrl(DependencyService.SysBotReleases); return false; }
        var dep = DependencyService.All.First(d => d.Name == "SysBot.exe");
        try
        {
            var msg = await DependencyService.Install(dep, m => Ui(() => Status(m)));
            Status(msg);
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, "SysBot update", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        return await DependencyService.SysBotOutdated(fresh: true) == null;
    }

    private bool EnsureDependency(string name)
    {
        var dep = DependencyService.All.First(d => d.Name == name);
        DependencyService.Refresh(dep);
        if (dep.State != DepState.Missing)
            return true;
        ToolsForm.AskInstall(FindForm(), dep);
        DependencyService.Refresh(dep);
        return dep.State != DepState.Missing;
    }

    // ================================================================ Switch tools
    /// <summary>The connection line + sys-botbase (version, install sys-botbase-cpp, WiFi / USB) + Pokémon Automation.</summary>
    private void AddSwitchTools(Card card)
    {
        var s = AppSettings.Current;
        card.Body.Controls.Add(Info(s.SwitchUsb ? "Switch: USB cable (sys-botbase) - change with the sys-botbase button."
                                                : $"Switch: {s.SwitchIP}:{s.SwitchPort} over WiFi (sys-botbase) - IP in Settings.", Theme.Muted));
        var sbb = new FancyButton("sys-botbase", Theme.GlyphSettings, Theme.PanelHi);
        sbb.Click += async (_, _) => await SysBotBaseMenu();
        var pa = new FancyButton("Pokémon Automation", Theme.GlyphPlay, Theme.PanelHi);
        pa.Click += async (_, _) => { if (await SysBotCurrent()) PokemonAutomationMenu(); };
        card.Body.Controls.Add(Row(sbb, pa));
    }

    /// <summary>Shows what runs on the Switch and offers: install / update sys-botbase-cpp, switch WiFi ↔ USB.</summary>
    private async Task SysBotBaseMenu()
    {
        if (!await SysBotCurrent()) return;
        var s = AppSettings.Current;
        Status("Asking the Switch which sys-botbase it runs...");
        var version = await Task.Run(() => SysBotBaseInstaller.InstalledVersion(s.SwitchIP, s.SwitchPort));
        var cpp = SysBotBaseInstaller.IsCpp(version);
        // sys-botbase-cpp can fail to take screenshots on some consoles - and ShinyForge needs them
        var shots = version != null && await Task.Run(() => SysBotBaseInstaller.ScreenshotWorks(s.SwitchIP, s.SwitchPort));
        Status("");
        var backup = SysBotBaseInstaller.LatestBackup();
        var page = new TaskDialogPage
        {
            Caption = "ShinyForge - sys-botbase",
            Heading = version == null ? "The Switch doesn't answer" : cpp ? $"sys-botbase-cpp {version} (Pokémon Automation)" : $"sys-botbase {version} (original)",
            Text = (version == null ? (s.SwitchUsb ? IpPrompt.UsbMissing : $"Nothing answers at {s.SwitchIP}:{s.SwitchPort}. Is the Switch on, on WiFi, and running Atmosphère?") + "\n\n" : "") +
                   (version != null && !shots ? "⚠ It can't take screenshots on this Switch, and ShinyForge needs them to open games, find the FTP app and save. " +
                        (backup != null ? "Restore the previous sys-botbase (start the FTP app yourself first: hold R while starting a game → ftpd)." : "") + "\n\n" : "") +
                   $"Connection: {(s.SwitchUsb ? "USB cable" : "WiFi")}.\n\n" +
                   "sys-botbase-cpp (github.com/PokemonAutomation/sys-botbase-cpp, GPL-3.0) is a rewrite of sys-botbase with the same commands: " +
                   "lighter on memory, a fix for random crashes, exact button timing, a log file on the SD card and a USB mode. " +
                   "Installing copies it onto the SD card over WiFi with the FTP app (the old files are backed up first); then restart the Switch.\n\n" +
                   "USB: the Switch must be out of the dock (so no capture card picture: no recording, no shiny clips), and the Python hunter / " +
                   "SysBot.exe only work over WiFi. Windows needs the WinUSB driver for the Switch once (Zadig).",
            Icon = version == null || !shots ? TaskDialogIcon.Warning : TaskDialogIcon.Information,
            AllowCancel = true,
        };
        var install = new TaskDialogButton(cpp ? "Reinstall / update sys-botbase-cpp" : "Install sys-botbase-cpp") { Enabled = version != null };
        var toUsb = new TaskDialogButton("Use USB") { Enabled = cpp && !s.SwitchUsb };
        var toWifi = new TaskDialogButton("Use WiFi") { Enabled = s.SwitchUsb };
        var restore = new TaskDialogButton("Restore previous sys-botbase") { Enabled = backup != null && (cpp || version == null) };
        page.Buttons.Add(install);
        page.Buttons.Add(s.SwitchUsb ? toWifi : toUsb);
        if (backup != null) page.Buttons.Add(restore);
        page.Buttons.Add(TaskDialogButton.Close);
        var choice = TaskDialog.ShowDialog(FindForm()!, page);
        if (choice == restore)
        {
            try
            {
                await Task.Run(() => SysBotBaseInstaller.Restore(s.SwitchIP, backup!, m => Ui(() => Status(m))));
                Status("The previous sys-botbase is back on the SD card - restart the Switch.");
                MessageBox.Show(FindForm(), "The previous sys-botbase is back on the SD card. Restart the Switch to start it.", "sys-botbase",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Status(ex.Message); MessageBox.Show(FindForm(), ex.Message, "sys-botbase", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            return;
        }
        if (choice != install && choice != toUsb && choice != toWifi) return;
        if (HuntService.IsPythonHuntRunning()) { MessageBox.Show(FindForm(), "The hunter is running - stop it first.", "sys-botbase"); return; }
        if (choice == install && MessageBox.Show(FindForm(), "No game may be open on the Switch (ShinyForge starts the FTP app from the HOME menu). " +
                "Save and close your game, then press OK.", "sys-botbase", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        try
        {
            if (choice == install)
            {
                var saved = await SysBotBaseInstaller.Install(s.SwitchIP, s.SwitchPort, s.SwitchUsb ? "usb" : "wifi", m => Ui(() => Status(m)));
                Status("sys-botbase-cpp is on the SD card - restart the Switch.");
                MessageBox.Show(FindForm(), "sys-botbase-cpp is on the SD card. Restart the Switch (hold POWER → Power Options → Restart) to start it.\n\n" +
                    $"The old sys-botbase files are backed up in:\n{saved}\n\nAfter the restart, open sys-botbase again: ShinyForge checks that " +
                    "screenshots work (it needs them) and offers to put the old one back if they don't.", "sys-botbase", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                var usb = choice == toUsb;
                await Task.Run(() => SysBotBaseInstaller.SetMode(s.SwitchIP, s.SwitchPort, usb ? "usb" : "wifi", m => Ui(() => Status(m))));
                Status($"sys-botbase set to {(usb ? "USB" : "WiFi")} - restart the Switch.");
                MessageBox.Show(FindForm(), usb
                    ? "sys-botbase will use USB after a restart.\n\n1. Restart the Switch.\n2. Take it out of the dock and plug it into this PC with a USB-C cable.\n" +
                      "3. First time only: run Zadig (zadig.akeo.ie), pick \"Nintendo Switch\" (057E 3000) and install WinUSB.\n\nTo go back: sys-botbase → Use WiFi."
                    : "sys-botbase will use WiFi after a restart. Restart the Switch and put it back in the dock.",
                    "sys-botbase", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("sys-botbase install / switch failed: " + ex);
            Status(ex.Message);
            MessageBox.Show(FindForm(), ex.Message, "sys-botbase", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Pokémon Automation needs eyes and hands: a capture card, and a microcontroller (ESP32-S3, Pico W, ...) or sys-botbase on
    /// a modded Switch. Checked before it's opened.
    /// </summary>
    private bool PaEquipmentReady()
    {
        var s = AppSettings.Current;
        return EquipmentReady("Pokémon Automation", () => Equipment.VideoSources().Count > 0,
                   "No capture card was found. Pokémon Automation watches the game through a capture card - connect it (and the Switch dock).")
               && EquipmentReady("Pokémon Automation", () => Equipment.Microcontroller() != false || HealthMonitor.SwitchReachable(1500),
                   "No controller for Pokémon Automation was found: plug in its microcontroller (ESP32-S3, Raspberry Pi Pico W... with their firmware), " +
                   $"or turn on the modded Switch so sys-botbase answers at {s.SwitchIP}.");
    }

    /// <summary>Starts Pokémon Automation's Computer Control (or says where to get it) and opens this game's program list.</summary>
    private void PokemonAutomationMenu()
    {
        var found = PokemonAutomation.Find() != null;
        var r = MessageBox.Show(FindForm(),
            "Pokémon Automation's Computer Control has tested programs for this game (shiny hunts, eggs, raids, saving...). It runs on an " +
            "UNMODDED Switch through a microcontroller - e.g. your ESP32-S3 with their firmware (it can't run pokeldn at the same time) - " +
            "plus the capture card, or on this modded Switch through sys-botbase. It's their program, for personal, non-profit use.\n\n" +
            (found ? "Yes = start Computer Control and show this game's programs\nNo = only show this game's programs"
                   : "It isn't on this PC yet: unzip PA-SerialPrograms-*.zip (from their GitHub) into Downloads, Desktop or Documents - not OneDrive.\n\n" +
                     "Yes = open the download page and the setup guide\nNo = only show this game's programs"),
            "Pokémon Automation", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
        if (r == DialogResult.Cancel) return;
        try
        {
            if (r == DialogResult.Yes && found && PaEquipmentReady()) PokemonAutomation.Launch();
            if (r == DialogResult.Yes && !found) { PokemonAutomation.Open(PokemonAutomation.Releases); PokemonAutomation.Open(PokemonAutomation.SetupGuide); }
            PokemonAutomation.Open(PokemonAutomation.ProgramsUrl(_game));
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, "Pokémon Automation", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // ================================================================ shiny hunt
    private Card BuildHuntCard()
    {
        var card = new Card(Theme.GlyphSparkle, "Shiny Hunt (automatic)") { Dock = DockStyle.Fill, Margin = new Padding(8) };
        var profile3ds = Services.ThreeDS.Games3DS.All.FirstOrDefault(g => g.ShinyForgeId == _game.Id);
        if (profile3ds != null)
        {
            Build3DSHunt(card, profile3ds);
            _huntStatus = Info("");
            return card;
        }
        var options = HuntService.OptionsFor(_game);
        if (options.Count == 0)
        {
            card.Body.Controls.Add(Info(HuntService.UnsupportedReason(_game), Theme.Muted));
            if (_game.IsSwitch) AddSwitchTools(card);
            _huntStatus = Info("");
            return card;
        }

        var radios = new List<RadioButton>();
        foreach (var o in options)
        {
            var rb = new RadioButton
            {
                Text = o.Title, Tag = o.Key, ForeColor = Theme.Text, Font = new Font("Segoe UI Semibold", 10f), AutoSize = true,
                BackColor = Theme.Panel, Checked = radios.Count == 0 || _gs.HuntBot == o.Key, Margin = new Padding(0, 8, 0, 0),
            };
            radios.Add(rb);
            card.Body.Controls.Add(rb);
            card.Body.Controls.Add(Info("     " + o.Description, Theme.Muted));
        }
        if (_game.IsSwitch) AddSwitchTools(card);

        var start = new FancyButton("Start hunting", Theme.GlyphPlay, Theme.Accent);
        var stop = new FancyButton("Stop Python hunter", Theme.GlyphStop, Color.FromArgb(150, 40, 60));
        start.Click += async (_, _) =>
        {
            if (!await SysBotCurrent()) return;
            var key = radios.First(r => r.Checked).Tag as string ?? "";
            if (key == "python" && !EnsureDependency("Python 3")) return;
            if (key == "python" && !await EnsurePythonPackages()) return;
            if (key == "sysbot" && !EnsureDependency("SysBot.exe")) return;
            // The Switch must answer at its saved IP - if its address changed, ask for the new one first.
            if (!IpPrompt.EnsureSwitch(FindForm())) return;
            // FireRed / LeafGreen: the game must be open with the save loaded (the hunter decides its mode from where you stand)
            if (key == "python" && !await EnsureGameOpen()) return;
            StartHuntNow(key);
        };
        stop.Click += (_, _) => { HuntService.StopPythonHunt(); _clips.Stop(); RefreshHuntStatus(); };
        // "Stop Python hunter" only where the Python hunter exists (FireRed/LeafGreen); SysBot is stopped in its own window.
        card.Body.Controls.Add(options.Any(o => o.Key == "python") ? Row(start, stop) : Row(start));
        if (_game.Bot == BotKind.FrlgSwitch)
        {
            var pause = new FancyButton("Pause / Resume", Theme.GlyphStop, Theme.PanelHi);
            pause.Click += (_, _) =>
            {
                HuntService.SetPaused(!HuntService.IsPaused);
                Status(HuntService.IsPaused ? "Python hunter will pause after the current attempt." : "Python hunter resumed.");
                RefreshHuntStatus();
            };
            var optionsButton = new FancyButton("Hunt options", Theme.GlyphSettings, Theme.PanelHi);
            optionsButton.Click += (_, _) => { using var f = new HuntOptionsForm(_game, rngAvailable: true); f.ShowDialog(FindForm()); RefreshHuntStatus(); };
            var stats = new FancyButton("Statistics", Theme.GlyphInfo, Theme.PanelHi);
            stats.Click += (_, _) => new HuntStatsForm("Python").Show(FindForm());
            card.Body.Controls.Add(Row(pause, optionsButton, stats));
        }
        else if (_game.Bot is BotKind.SysBotLGPE or BotKind.SysBotSWSH or BotKind.SysBotBDSP)
        {
            // SysBot.exe games: which Pokémon to stop on (written into SysBot's stop conditions when the hunt starts)
            var targetsButton = new FancyButton("Stop targets", Theme.GlyphSettings, Theme.PanelHi);
            targetsButton.Click += (_, _) => { using var f = new HuntOptionsForm(_game, rngAvailable: false, targetsOnly: true); f.ShowDialog(FindForm()); };
            card.Body.Controls.Add(Row(targetsButton));
        }
        _huntStatus = Info("", Theme.Good);
        card.Body.Controls.Add(_huntStatus);
        return card;
    }

    // ---------------------------------------------------------------- 3DS hunting (Luma3DS)
    private CancellationTokenSource? _hunt3ds;
    private DateTime _last3dsActivity = DateTime.Now;
    private Services.ThreeDS.Bot3DS? _bot3ds;
    public bool IsHunting3DS => _hunt3ds != null;
    private static int _3dsHunts;
    /// <summary>Is a 3DS hunt running on any game page (for keep-awake)?</summary>
    public static bool Any3DSHunt => _3dsHunts > 0;

    private void Build3DSHunt(Card card, Services.ThreeDS.Game3DS profile)
    {
        card.Body.Controls.Add(Info("Controls your 3DS over WiFi (Luma3DS: Rosalina → InputRedirection + Debugger options → Enable debugger). " +
                                    "Recognises the game, logs every encounter and stops on a shiny.", Theme.Muted));
        var mode = Combo(330, new[] { "Auto (detects static vs. wild)", "Soft reset (static / legendary)", "Wild encounters (walk + run)" }, null);
        var axis = Combo(150, new[] { "Left / Right", "Up / Down" }, null);
        card.Body.Controls.Add(Row(Theme.Label("Mode  ", Theme.Body, Theme.Text), mode));
        card.Body.Controls.Add(Row(Theme.Label("Walk  ", Theme.Body, Theme.Text), axis));

        var log = new TextBox { Multiline = true, ReadOnly = true, Width = 520, Height = 150, ScrollBars = ScrollBars.Vertical };
        Theme.StyleInput(log);
        log.Font = Theme.Mono;
        void Say(string s) { Ui(() => log.AppendText($"[{DateTime.Now:HH:mm:ss}] {s.Replace("\n", "\r\n")}\r\n")); }

        var start = new FancyButton("Start hunting", Theme.GlyphPlay, Theme.Accent);
        var stop = new FancyButton("Stop", Theme.GlyphStop, Color.FromArgb(150, 40, 60)) { Enabled = false };
        var test = new FancyButton("Test connection", Theme.GlyphWifi, Theme.PanelHi);

        test.Click += async (_, _) =>
        {
            var ip = IpPrompt.GetChecked3DS(FindForm());
            if (ip == null) return;
            Say($"Testing {ip}...");
            await Task.Run(() =>
            {
                var bot = new Services.ThreeDS.Bot3DS(new Services.ThreeDS.Bot3DSOptions { Host = ip });
                bot.Log += Say;
                try
                {
                    var g = bot.Connect(profile.ShinyForgeId);
                    var pk = bot.ReadWild();
                    Say(pk == null ? "Connected. No wild Pokémon in memory right now." : "Last/current wild Pokémon:\n" + Services.ThreeDS.Bot3DS.Format(pk));
                    if (g.ShinyForgeId != profile.ShinyForgeId) Say($"Note: the 3DS is running {g.Name}, not {_game.Title}.");
                }
                catch (Exception ex) { Say("Connection test failed: " + ex.Message); }
                finally { bot.Close(); }
            });
        };

        start.Click += async (_, _) =>
        {
            if (!await SysBotCurrent()) return;
            var ip = IpPrompt.GetChecked3DS(FindForm());
            if (ip == null) return;
            var options = new Services.ThreeDS.Bot3DSOptions
            {
                Host = ip,
                Mode = mode.SelectedIndex switch { 1 => Services.ThreeDS.Hunt3DSMode.StaticSoftReset, 2 => Services.ThreeDS.Hunt3DSMode.WildWalking, _ => Services.ThreeDS.Hunt3DSMode.Auto },
                WalkHorizontal = axis.SelectedIndex == 0,
                StopRule = StopRuleFor3DS(_game),
            };
            _bot3ds = new Services.ThreeDS.Bot3DS(options);
            _bot3ds.Log += s => { _last3dsActivity = DateTime.Now; Say(s); };
            _bot3ds.Warning += w => HealthMonitor.Report($"3DS hunt ({_game.Title})", HealthState.Warning, w);
            _bot3ds.Encounter += (pk, stop) =>
            {
                if (!stop) return;
                var name = PKHeX.Core.SpeciesName.GetSpeciesName(pk.Species, 2);
                OnFound(name, pk.IsShiny);
                if (AppSettings.Current.NotifyShiny)
                    NotifyService.Send((pk.IsShiny ? "✨ Shiny " : "Found ") + name + " on the 3DS!",
                        $"{_game.Title}: {name} Lv.{pk.CurrentLevel}, {(PKHeX.Core.Nature)pk.Nature}, IVs {pk.IV_HP + pk.IV_ATK + pk.IV_DEF + pk.IV_SPA + pk.IV_SPD + pk.IV_SPE}/186. " +
                        "The bot stopped - the game is waiting in the battle.", important: true);
            };
            WebServer.RemoteAction = what =>
            {
                if (what != "stop") return "The phone remote can only stop the 3DS bot.";
                _hunt3ds?.Cancel();
                return "Stopping the 3DS hunt.";
            };
            WebServer.RemoteStatus = () => $"3DS bot ({_game.Title}): " + (_hunt3ds != null ? "hunting" : "stopped");
            StartClipBuffer();
            _hunt3ds = new CancellationTokenSource();
            var token = _hunt3ds.Token;
            _last3dsActivity = DateTime.Now;
            start.Enabled = false; stop.Enabled = true;
            Interlocked.Increment(ref _3dsHunts);
            _ = Task.Run(() => // the hunt runs in the background; it reports and cleans up by itself
            {
                Exception? failure = null;
                try
                {
                    _bot3ds.Connect(profile.ShinyForgeId);
                    _bot3ds.Run(token);
                }
                catch (Exception ex) { failure = ex; Say("Stopped: " + ex.Message); }
                finally
                {
                    _bot3ds.Close();
                    _hunt3ds = null;
                    Interlocked.Decrement(ref _3dsHunts);
                    WebServer.RemoteAction = null;
                    WebServer.RemoteStatus = null;
                    Ui(() => { start.Enabled = true; stop.Enabled = false; _clips.Stop(); });
                }
                // Lost the 3DS (network error) and it no longer answers at that IP: ask for its new address and restart.
                if (failure is IOException or System.Net.Sockets.SocketException or TimeoutException ||
                    failure?.InnerException is IOException or System.Net.Sockets.SocketException)
                {
                    HealthMonitor.Report($"3DS hunt ({_game.Title})", HealthState.Error, "Lost the connection to the 3DS: " + failure!.Message);
                    if (!HealthMonitor.CanReach(AppSettings.Current.ThreeDSIP, 4000, 2500))
                        Ui(() =>
                        {
                            if (IpPrompt.Ask(FindForm(), $"Lost the connection to the 3DS at {AppSettings.Current.ThreeDSIP}. " +
                                                         "Its IP address may have changed.") != null)
                                start.PerformClickSafe();
                        });
                }
            });
        };
        stop.Click += (_, _) => { _hunt3ds?.Cancel(); Say("Stopping..."); };

        card.Body.Controls.Add(Row(start, stop, test));
        var opts3 = new FancyButton("Stop rules", Theme.GlyphSettings, Theme.PanelHi);
        opts3.Click += (_, _) => { using var f = new HuntOptionsForm(_game, rngAvailable: false); f.ShowDialog(FindForm()); };
        var stats3 = new FancyButton("Statistics", Theme.GlyphInfo, Theme.PanelHi);
        stats3.Click += (_, _) => new HuntStatsForm(_game.Title).Show(FindForm());
        card.Body.Controls.Add(Row(opts3, stats3));
        card.Body.Controls.Add(log);
        card.Body.Controls.Add(Info($"Encounters are logged to {Path.Combine(AppPaths.DataDir, "3ds_encounters.csv")}", Theme.Muted));
    }

    /// <summary>The game's stop targets and the stop rules from Hunt options, as a check for the 3DS bot (null = shinies only).</summary>
    private static Func<PKHeX.Core.PKM, string?>? StopRuleFor3DS(Game game)
    {
        var o = AppSettings.Current.Hunt;
        var targets = AppSettings.Current.For(game.Id).StopTargets.ToList();
        // the shared nature / IV rules don't apply to Gen 1 / 2 (no natures; DVs instead of IVs): only the stop targets there
        var rules = game.Gen >= 3 && o.HasStopRules;
        if (!rules && targets.Count == 0) return null;
        return pk =>
        {
            var checks = new List<(string Name, bool Ok)>();
            if (targets.Count > 0)
            {
                var hit = targets.FirstOrDefault(t => StopTargets.Matches(t, pk));
                checks.Add((hit != null ? StopTargets.Describe(game, hit) : "a stop target", hit != null));
            }
            var nature = ((PKHeX.Core.Nature)pk.Nature).ToString();
            if (rules && o.StopNatures.Count > 0) checks.Add(("nature " + nature, o.StopNatures.Contains(nature, StringComparer.OrdinalIgnoreCase)));
            var ivs = new Dictionary<string, int> { ["HP"] = pk.IV_HP, ["Atk"] = pk.IV_ATK, ["Def"] = pk.IV_DEF, ["SpA"] = pk.IV_SPA, ["SpD"] = pk.IV_SPD, ["Spe"] = pk.IV_SPE };
            if (rules && o.MinIvTotal > 0) checks.Add(($"IV total {ivs.Values.Sum()}", ivs.Values.Sum() >= o.MinIvTotal));
            if (rules) foreach (var (k, v) in o.MinIvs.Where(kv => kv.Value > 0)) checks.Add(($"{k} IV {ivs[k]}", ivs[k] >= v));
            var hits = checks.Where(c => c.Ok).Select(c => c.Name).ToList();
            var match = o.RequireAll ? hits.Count == checks.Count && checks.Count > 0 : hits.Count > 0;
            return match ? "matches your stop rules (" + string.Join(", ", hits) + ")" : null;
        };
    }

    private void RefreshHuntStatus()
    {
        if (_huntStatus == null || _game.Bot != BotKind.FrlgSwitch) return;
        _ = Task.Run(() =>
        {
            var running = HuntService.IsPythonHuntRunning();
            var o = AppSettings.Current.Hunt;
            var extras = new List<string>();
            if (o.RngEnabled) extras.Add("RNG manipulation ON");
            if (o.AutoCatch != "off") extras.Add("auto-catch " + (o.AutoCatch == "all" ? "everything" : "non-legendary"));
            if (_gs.StopTargets.Count > 0) extras.Add($"{_gs.StopTargets.Count} stop target(s)");
            if (o.HasStopRules) extras.Add("extra stop rules");
            if (_clips.Running) extras.Add("shiny clip buffer");
            Ui(() => _huntStatus.Text = (running ? (HuntService.IsPaused ? "⏸ Python hunter is PAUSED" : "● Python hunter is running") : "") +
                                        (extras.Count > 0 ? $"{(running ? "  •  " : "")}{string.Join(", ", extras)}" : ""));
            if (!running && _clips.Running && !IsHunting3DS) Ui(() => _clips.Stop());
        });
    }

    // ---------------------------------------------------------------- shiny clips
    private readonly ShinyClipService _clips = new();

    /// <summary>Starts the rolling clip buffer from this game's chosen capture source (Record card) - unless already recording.</summary>
    private void StartClipBuffer()
    {
        if (!AppSettings.Current.ShinyClips || _recorder.IsRecording || _clips.Running) return;
        var source = _source?.SelectedItem as string ?? _gs.VideoSource;
        if (string.IsNullOrWhiteSpace(source) || source.StartsWith("(")) return;
        _clips.Start(CaptureSpec(source), _game.Id,
                     Directory.Exists(_gs.RecordingFolder) ? Path.Combine(_gs.RecordingFolder, "Shiny Clips") : ShinyClipService.DefaultFolder);
        if (_clips.Running) Status("Shiny clip buffer running - the last " + AppSettings.Current.ShinyClipSeconds + " s are saved when a shiny appears.");
    }

    /// <summary>Something was found: save the clip (or point at the running recording).</summary>
    private void OnFound(string species, bool shiny)
    {
        Ui(() => Status($"{(shiny ? "★ Shiny " : "Found ")}{species}! {(_recorder.IsRecording ? "It's in the current recording." : "")}"));
        if (_clips.Running)
            _ = Task.Run(async () =>
            {
                var clip = await _clips.SaveClipAsync((shiny ? "shiny_" : "") + species);
                if (clip != null)
                {
                    Ui(() => Status("Clip saved: " + clip));
                    if (AppSettings.Current.NotifyShiny) NotifyService.Send("Clip saved", $"{species}: {Path.GetFileName(clip)}");
                }
            });
    }

    // ================================================================ recording
    private Card BuildRecordCard()
    {
        var card = new Card(Theme.GlyphRecord, "Record Gameplay") { Dock = DockStyle.Fill, Margin = new Padding(8), AccentColor = Theme.Bad };
        card.Body.Controls.Add(Info("Records continuously, split into numbered files with no gap between them.", Theme.Muted));

        var (video, audio) = RecordingService.ListDevices();
        var sources = new List<string>();
        if (!_game.IsSwitch)
            // DS / 3DS games (Gen 4-7): both screens stacked; Game Boy games only have the top screen
            sources.Add(RecordingService.HasTwoScreens(_game.Platform) ? RecordingService.Both3DS : RecordingService.Top3DS);
        sources.AddRange(video.Select(v => RecordingService.DevicePrefix + v));
        if (_game.IsSwitch)
            sources.Add(RecordingService.WindowPrefix + "Streamlabs Desktop");
        var defaultSource = sources.Contains(_gs.VideoSource) ? _gs.VideoSource
            : sources.FirstOrDefault(s => _game.IsSwitch ? s.Contains("Game Capture") : s.StartsWith(RecordingService.WindowPrefix + "3DS Capture")) ?? sources.FirstOrDefault();
        _source = Combo(420, sources, defaultSource);

        var audios = new List<string> { RecordingService.NoAudio };
        if (!_game.IsSwitch) audios.Add(RecordingService.AppAudio3DS);
        audios.AddRange(audio);
        var defaultAudio = audios.Contains(_gs.AudioDevice) ? _gs.AudioDevice : RecordingService.DefaultAudio(defaultSource ?? "", audios);
        _audio = Combo(420, audios, defaultAudio);
        // picking another source picks the sound that belongs to it
        _source.SelectedIndexChanged += (_, _) => _audio.SelectedItem = RecordingService.DefaultAudio(_source.SelectedItem as string ?? "", audios);
        _segments = Combo(120, new[] { "5", "10", "15", "20", "30" }, _gs.SegmentMinutes.ToString());

        // console frame around the recording (Switch games: Switch 1 or Switch 2)
        var frames = ConsoleFrames.For(_game.Platform).ToList();
        var overlayNames = new List<string> { "No frame" };
        overlayNames.AddRange(frames.Select(f => f.Label + " frame" + (ConsoleFrames.Exists(f) ? "" : " (picture missing)")));
        var current = frames.FindIndex(f => f.Key == _gs.Overlay);
        _overlay = Combo(420, overlayNames, overlayNames[current + 1]);
        _overlayKeys = frames.Select(f => f.Key).ToList();
        _crop = new CheckBox { Text = "Cut the black border around DS / Game Boy pictures", Checked = _gs.CropToGame, AutoSize = true, ForeColor = Theme.Text, Visible = !_game.IsSwitch && _game.Platform != Platform.N3DS };

        _recFolder = new TextBox { Width = 330, Text = _gs.RecordingFolder };
        Theme.StyleInput(_recFolder);
        var browse = new FancyButton("Browse", Theme.GlyphFolder, Theme.PanelHi);
        browse.Click += (_, _) => { var f = PickFolder("Where should recordings be saved?", _recFolder.Text); if (f != null) _recFolder.Text = f; };

        card.Body.Controls.Add(Row(Theme.Label("Video   ", Theme.Body, Theme.Text), _source));
        card.Body.Controls.Add(Row(Theme.Label("Audio   ", Theme.Body, Theme.Text), _audio));
        card.Body.Controls.Add(Row(Theme.Label("Frame   ", Theme.Body, Theme.Text), _overlay));
        if (_crop.Visible) card.Body.Controls.Add(Row(_crop));
        card.Body.Controls.Add(Info(frames.Any(ConsoleFrames.Exists) ? "The game picture is fitted exactly into the console's screen(s)."
            : $"Console frame pictures go in {ConsoleFrames.Folder} (Settings can point somewhere else).", Theme.Muted));
        card.Body.Controls.Add(Row(Theme.Label("Segment length (minutes)  ", Theme.Body, Theme.Text), _segments));
        card.Body.Controls.Add(Row(Theme.Label("Folder  ", Theme.Body, Theme.Text), _recFolder, browse));

        _recButton = new FancyButton("Start recording", Theme.GlyphRecord, Color.FromArgb(190, 30, 50));
        _recButton.Click += (_, _) => ToggleRecording();
        if (_game.IsSwitch && AppSettings.Current.SwitchUsb)
        {
            // USB needs the Switch out of the dock: the capture card gets no picture
            _recButton.Enabled = false;
            card.Body.Controls.Add(Info("Recording is off while sys-botbase uses USB (the Switch is out of the dock, so the capture card has no picture). " +
                                        "Shiny Hunt card → sys-botbase → Use WiFi.", Theme.Warn));
        }
        var open = new FancyButton("Open folder", Theme.GlyphFolder, Theme.PanelHi);
        open.Click += (_, _) => { if (Directory.Exists(_recFolder.Text)) Process.Start("explorer.exe", _recFolder.Text); };
        var buttons = Row(_recButton, open);
        var streamlabs = new FancyButton("Streamlabs scenes", Theme.GlyphSettings, Theme.PanelHi);
        streamlabs.Click += (_, _) => { using var f = new StreamlabsSetupForm(video); f.ShowDialog(FindForm()); };
        buttons.Controls.Add(streamlabs);
        if (!_game.IsSwitch)
        {
            var launch = new FancyButton("Open 3DS Capture", Theme.GlyphUsb, Theme.PanelHi);
            launch.Click += (_, _) => Launch3DSCapture();
            buttons.Controls.Add(launch);
        }
        card.Body.Controls.Add(buttons);
        _recStatus = Info("Not recording.", Theme.Muted);
        card.Body.Controls.Add(_recStatus);
        return card;
    }

    /// <summary>What the Record card is set to: source, sound, console frame.</summary>
    private RecordingService.CaptureSpec CaptureSpec(string? source = null)
    {
        var overlay = _overlay?.SelectedIndex > 0 ? _overlayKeys[_overlay.SelectedIndex - 1] : _overlay == null ? _gs.Overlay : ConsoleFrames.None;
        return new RecordingService.CaptureSpec(source ?? _source?.SelectedItem as string ?? _gs.VideoSource,
            _audio?.SelectedItem as string ?? RecordingService.NoAudio, _game.Platform, overlay, _crop?.Checked ?? _gs.CropToGame);
    }

    /// <summary>
    /// Opens 3DS Capture once it's installed AND a capture board is plugged in (or the user chooses to open it anyway -
    /// not every board can be recognised). False = not opened.
    /// </summary>
    private bool Launch3DSCapture()
    {
        if (!EnsureDependency("3DS Capture")) return false;
        var cap = DependencyService.PathOf("3DS Capture");
        if (cap == null) return false;
        if (Process.GetProcessesByName("3ds_capture").Length > 0) return true; // already open
        if (!EquipmentReady("3DS Capture", () => Equipment.ThreeDSCaptureBoard() != false,
                "No 3DS capture board is plugged in. Connect the 3DS (with its capture board) to this PC by USB and turn it on.")) return false;
        Process.Start(new ProcessStartInfo(cap) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(cap)! });
        return true;
    }

    /// <summary>
    /// Is the hardware for <paramref name="what"/> there? If not: what's missing, then Retry (check again) / Ignore (open
    /// anyway - detection can't know every device) / Abort. True = go ahead.
    /// </summary>
    private bool EquipmentReady(string what, Func<bool> present, string missing)
    {
        while (!present())
        {
            var r = MessageBox.Show(FindForm(), missing + "\n\nRetry = check again\nIgnore = open it anyway\nAbort = cancel",
                what, MessageBoxButtons.AbortRetryIgnore, MessageBoxIcon.Warning);
            if (r == DialogResult.Ignore) return true;
            if (r != DialogResult.Retry) return false;
        }
        return true;
    }

    /// <summary>The Python hunter's packages (OpenCV, NumPy, PyYAML, mss): offers to install the missing ones with pip.</summary>
    private async Task<bool> EnsurePythonPackages()
    {
        var python = DependencyService.PathOf("Python 3");
        var botDir = AppPaths.FindPythonBotFolder(AppSettings.Current.PythonBotFolder);
        if (python == null || botDir == null) return true; // reported by the hunt start itself
        Status("Checking the Python hunter's packages...");
        var missing = await DependencyService.MissingPythonPackages(python);
        Status("");
        if (missing is not { Count: > 0 }) return true;
        if (MessageBox.Show(FindForm(), $"The Python hunter needs these Python packages: {string.Join(", ", missing)}.\n\nInstall them now (pip)?",
                "Python hunter", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        Status("Installing Python packages...");
        var result = await DependencyService.InstallPythonPackages(python, botDir);
        missing = await DependencyService.MissingPythonPackages(python);
        Status(missing is { Count: 0 } ? "Python packages installed." : result);
        if (missing is { Count: > 0 })
        {
            MessageBox.Show(FindForm(), $"These packages still aren't installed: {string.Join(", ", missing)}.\n\npip said: {result}", "Python hunter",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    private async void ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
            _recButton.Text = "Start recording";
            _recButton.Invalidate();
            return;
        }
        if (!await SysBotCurrent()) return;
        if (!EnsureDependency("FFmpeg")) return;
        if (_game.IsSwitch && !EnsureDependency("Streamlabs Desktop")) return;
        if (string.IsNullOrWhiteSpace(_recFolder.Text) || !Directory.Exists(_recFolder.Text))
        {
            var f = PickFolder("Choose the folder for the recordings (files are numbered automatically)", _recFolder.Text);
            if (f == null) return;
            _recFolder.Text = f;
        }
        _recButton.Enabled = false;
        try { if (!await EnsureGameForRecording()) return; }
        finally { _recButton.Enabled = true; }
        var source = _source.SelectedItem as string ?? "";
        if (source.Contains("3DS Capture"))
        {
            if (!Launch3DSCapture()) return;
            await Task.Delay(1500);
        }
        else if (source.StartsWith(RecordingService.DevicePrefix))
        {
            var device = source[RecordingService.DevicePrefix.Length..];
            if (!EquipmentReady("Record Gameplay", () => Equipment.VideoSources().Contains(device),
                    $"The capture device \"{device}\" isn't connected. Plug in the capture card (and the console), or choose another video source.")) return;
        }
        _gs.RecordingFolder = _recFolder.Text;
        _gs.SegmentMinutes = int.Parse(_segments.SelectedItem as string ?? "10");
        _gs.VideoSource = source;
        _gs.AudioDevice = _audio.SelectedItem as string ?? "";
        var spec = CaptureSpec();
        _gs.Overlay = spec.Overlay;
        _gs.CropToGame = spec.CropToGame;
        AppSettings.Current.Save();
        try
        {
            _recorder.Start(spec, _gs.RecordingFolder, _gs.SegmentMinutes);
            _recButton.Text = "Stop recording";
            _recButton.Invalidate();
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, "Recording", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // ================================================================ collection tracker
    private Card BuildTrackerCard()
    {
        var card = new Card(Theme.GlyphSheet, "Collection Tracker → Pokédex spreadsheet") { Dock = DockStyle.Fill, Margin = new Padding(8), AccentColor = Theme.Good };
        var wb = SpreadsheetService.FindWorkbook(_game);
        _sheetLabel = Info(SpreadsheetService.Describe(_game, wb), wb != null ? Theme.Text : Theme.Warn);
        card.Body.Controls.Add(_sheetLabel);
        var change = new FancyButton("Choose spreadsheet", Theme.GlyphSheet, Theme.PanelHi);
        change.Click += (_, _) =>
        {
            var cur = SpreadsheetService.FindWorkbook(_game);
            using var dlg = new OpenFileDialog
            {
                Filter = "Excel workbooks|*.xlsm;*.xlsx",
                InitialDirectory = cur != null ? Path.GetDirectoryName(cur) : Directory.Exists(AppSettings.Current.SpreadsheetRoot) ? AppSettings.Current.SpreadsheetRoot : SpreadsheetService.BundledRoot,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            _gs.SpreadsheetPath = dlg.FileName;
            AppSettings.Current.Save();
            RefreshSheetLabel();
        };
        var autoSheet = new FancyButton("Auto-recognize", Theme.GlyphSync, Theme.PanelHi);
        autoSheet.Click += (_, _) => { _gs.SpreadsheetPath = ""; AppSettings.Current.Save(); RefreshSheetLabel(); };
        var openSheet = new FancyButton("Open", Theme.GlyphSheet, Theme.PanelHi);
        openSheet.Click += (_, _) =>
        {
            if (!EnsureDependency("Microsoft Excel")) return;
            var cur = SpreadsheetService.FindWorkbook(_game);
            if (cur != null) Process.Start(new ProcessStartInfo(cur) { UseShellExecute = true });
        };
        var dex = new FancyButton("Pokédex (pop-out)", Theme.GlyphSparkle, Theme.Accent2);
        dex.Click += (_, _) => OpenDex();
        card.Body.Controls.Add(Row(change, autoSheet, openSheet));
        _downloadSheet = new FancyButton("Download tracker", Theme.GlyphDownload, Theme.Accent) { Visible = wb == null && !string.IsNullOrWhiteSpace(_game.Spreadsheet) };
        _downloadSheet.Click += async (_, _) => await DownloadTracker();
        card.Body.Controls.Add(Row(dex, _downloadSheet));

        var sources = new List<string> { "Save file" };
        if (_game.Bot == BotKind.FrlgSwitch)
            sources.Insert(0, "Live from the Switch (party + PC boxes)");
        _trackSource = Combo(360, sources, null);
        _trackLive = (_trackSource.SelectedItem as string ?? "").StartsWith("Live");
        _trackSource.SelectedIndexChanged += (_, _) => _trackLive = (_trackSource.SelectedItem as string ?? "").StartsWith("Live");
        card.Body.Controls.Add(Row(Theme.Label("Read from  ", Theme.Body, Theme.Text), _trackSource));

        // Which save file the tracker uses: one you picked, else the save found by the Pokémon HOME card / game folder.
        _trackSaveLabel = Info("Save file: searching...", Theme.Muted);
        var pickSave = new FancyButton("Choose save file...", Theme.GlyphSave, Theme.PanelHi);
        pickSave.Click += (_, _) => ChooseTrackerSave();
        var auto = new FancyButton("Use auto-detected", Theme.GlyphSync, Theme.PanelHi);
        auto.Click += (_, _) =>
        {
            _gs.SaveFilePath = "";
            AppSettings.Current.Save();
            UpdateTrackerSaveLabel();
        };
        card.Body.Controls.Add(_trackSaveLabel);
        card.Body.Controls.Add(Row(pickSave, auto));

        var sync = new FancyButton("Sync now", Theme.GlyphSync, Theme.Accent);
        sync.Click += async (_, _) => await SyncTracker(manual: true);
        _autoMonitor = new CheckBox { Text = "Monitor automatically", ForeColor = Theme.Text, Font = Theme.Body, AutoSize = true, BackColor = Theme.Panel, Margin = new Padding(12, 10, 0, 0) };
        _autoMonitor.CheckedChanged += (_, _) => SetMonitoring(_autoMonitor.Checked);
        card.Body.Controls.Add(Row(sync, _autoMonitor));
        card.Body.Controls.Add(Info("Marks 'In Box', 'OT' (caught by you) and 'Shiny' as Y for every Pokémon in your party and boxes. " +
                                    "Only N → Y changes are made; your own marks are never removed.", Theme.Muted));
        _trackLog = new TextBox { Multiline = true, ReadOnly = true, Width = 520, Height = 120, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono };
        Theme.StyleInput(_trackLog);
        _trackLog.Font = Theme.Mono;
        card.Body.Controls.Add(_trackLog);
        return card;
    }

    private void Log(string s) => Ui(() => _trackLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}"));

    private void RefreshSheetLabel()
    {
        var wb = SpreadsheetService.FindWorkbook(_game);
        _sheetLabel.Text = SpreadsheetService.Describe(_game, wb);
        _sheetLabel.ForeColor = wb != null ? Theme.Text : Theme.Warn;
        _downloadSheet.Visible = wb == null && !string.IsNullOrWhiteSpace(_game.Spreadsheet);
    }

    private FancyButton _downloadSheet = null!;

    /// <summary>Downloads this game's Pokédex tracker from ShinyForge's GitHub page (when the installer's download was skipped, or portable).</summary>
    private async Task DownloadTracker()
    {
        _downloadSheet.Enabled = false;
        try
        {
            _sheetLabel.ForeColor = Theme.Muted;
            _sheetLabel.Text = "Looking up the tracker on ShinyForge's GitHub page...";
            var entry = SheetsDownloadService.EntryFor(_game, await SheetsDownloadService.ManifestAsync());
            if (entry == null) { _sheetLabel.Text = $"No published tracker for Pokémon {_game.Title} was found."; return; }
            if (MessageBox.Show(FindForm(), $"Download the Pokémon {_game.Title} tracker ({entry.Size / 1048576.0:N0} MB) into\n{SpreadsheetService.BundledRoot}?",
                    "Pokédex tracker", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { RefreshSheetLabel(); return; }
            var path = await SheetsDownloadService.DownloadAsync(entry, s => Ui(() => _sheetLabel.Text = s));
            Status("Tracker downloaded: " + path);
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), "Couldn't download the tracker: " + ex.Message, "Pokédex tracker"); }
        finally { _downloadSheet.Enabled = true; RefreshSheetLabel(); }
    }

    private DexForm? _dexForm;
    private volatile bool _trackLive;

    /// <summary>Opens (or brings back) this game's Pokédex window, fed by the same source as the tracker (live game or save file).</summary>
    private void OpenDex()
    {
        if (_dexForm is { IsDisposed: false })
        {
            if (_dexForm.WindowState == FormWindowState.Minimized) _dexForm.WindowState = FormWindowState.Normal;
            _dexForm.Activate();
            return;
        }
        _dexForm = new DexForm(_game, LoadCollectionForDex);
        _dexForm.Show();
    }

    /// <summary>Reads what you own for the Pokédex window: the live Switch game if that source is selected, else the tracker's save file.</summary>
    private Collection? LoadCollectionForDex(Action<string> progress)
    {
        if (_trackLive)
        {
            // Switch not answering (new IP?): ask for its address on the UI thread, then read.
            if (!HealthMonitor.SwitchReachable(2500))
            {
                var main = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
                var ok = false;
                if (main is { IsDisposed: false }) main.Invoke(() => ok = IpPrompt.EnsureSwitch(Application.OpenForms.OfType<DexForm>().LastOrDefault() ?? (IWin32Window)main));
                if (!ok) throw new InvalidOperationException($"Can't reach the Switch at {AppSettings.Current.SwitchIP}.");
            }
            progress("Reading party and PC boxes from the Switch...");
            var s = AppSettings.Current;
            using var lf = new LiveFrlg(s.SwitchIP, s.SwitchPort);
            return lf.ReadCollection();
        }
        progress("Reading the save file...");
        var save = ResolveTrackerSave();
        return save == null ? null : Collection.FromSave(save.Save, save.Path);
    }

    /// <summary>The save the tracker reads: the one you chose, else the one the Pokémon HOME card found, else the newest in the game folder.</summary>
    private FoundSave? ResolveTrackerSave()
    {
        if (!string.IsNullOrWhiteSpace(_gs.SaveFilePath) && File.Exists(_gs.SaveFilePath) &&
            PKHeX.Core.SaveUtil.TryGetSaveFile(_gs.SaveFilePath, out var chosen) && chosen != null)
            return new FoundSave(_gs.SaveFilePath, chosen);
        return _save ?? SaveService.FindSaves(_gs.GameFolder, _game).FirstOrDefault();
    }

    private void UpdateTrackerSaveLabel()
    {
        if (_trackSaveLabel == null) return;
        Task.Run(ResolveTrackerSave).ContinueWith(t => Ui(() =>
        {
            var s = t.IsFaulted ? null : t.Result;
            var picked = !string.IsNullOrWhiteSpace(_gs.SaveFilePath) && s?.Path == _gs.SaveFilePath;
            _trackSaveLabel.ForeColor = s != null ? Theme.Good : Theme.Warn;
            _trackSaveLabel.Text = s == null
                ? "Save file: none found - choose one, or set the game folder in the Pokémon HOME card."
                : $"Save file ({(picked ? "chosen by you" : "auto-detected")}): {Path.GetFileName(s.Path)} - {s.Save.OT}, {s.Save.Version}, " +
                  $"{s.Save.PlayedHours}h {s.Save.PlayedMinutes:00}m\n    {Path.GetDirectoryName(s.Path)}";
        }));
    }

    private void ChooseTrackerSave()
    {
        using var dlg = new OpenFileDialog
        {
            Title = $"Choose a Pokémon {_game.Title} save file",
            Filter = "Save files|*.sav;*.dsv;*.sa1;*.srm;*.bin;main;*.*|All files|*.*",
            InitialDirectory = Directory.Exists(_gs.GameFolder) ? _gs.GameFolder : "",
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (!PKHeX.Core.SaveUtil.TryGetSaveFile(dlg.FileName, out var sav) || sav == null)
        {
            MessageBox.Show(FindForm(), "That file isn't a Pokémon save file PKHeX can read.", "Choose save file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_game.Versions.Contains(sav.Version) &&
            MessageBox.Show(FindForm(), $"This save is from Pokémon {sav.Version}, not {_game.Title}. Use it anyway?", "Choose save file", MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        _gs.SaveFilePath = dlg.FileName;
        if (string.IsNullOrWhiteSpace(_gs.GameFolder))
        {
            _gs.GameFolder = Path.GetDirectoryName(dlg.FileName)!;
            _folderBox.Text = _gs.GameFolder;
        }
        AppSettings.Current.Save();
        UpdateTrackerSaveLabel();
    }

    private void SetMonitoring(bool on)
    {
        _gs.AutoMonitor = on;
        AppSettings.Current.Save();
        _watcher?.Dispose();
        _watcher = null;
        _liveTimer?.Dispose();
        _liveTimer = null;
        if (!on) { Log("Automatic monitoring off."); return; }

        if ((_trackSource.SelectedItem as string ?? "").StartsWith("Live"))
        {
            _liveTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
            _liveTimer.Tick += async (_, _) => await SyncTracker(manual: false);
            _liveTimer.Start();
            Log("Monitoring the live game every 5 minutes.");
            return;
        }
        if (!Directory.Exists(_gs.GameFolder))
        {
            Log("Set the game folder (Pokémon HOME card) first, then turn monitoring on.");
            _autoMonitor.Checked = false;
            return;
        }
        _watcher = new FileSystemWatcher(_gs.GameFolder) { IncludeSubdirectories = true, EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        _watcher.Changed += OnSaveChanged;
        _watcher.Created += OnSaveChanged;
        Log($"Watching {_gs.GameFolder} - the tracker updates whenever a new save is exported there.");
    }

    private void OnSaveChanged(object? sender, FileSystemEventArgs e)
    {
        if (e.FullPath.Contains("Pokemon Home for") || e.Name?.EndsWith(".txt") == true) return;
        if ((DateTime.Now - _lastAutoSync).TotalSeconds < 20) return;
        _lastAutoSync = DateTime.Now;
        Ui(async () => { await Task.Delay(5000); await SyncTracker(manual: false); });
    }

    private async Task SyncTracker(bool manual)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var wb = SpreadsheetService.FindWorkbook(_game);
            if (wb == null) { Log("No spreadsheet for this game - use 'Choose spreadsheet'."); return; }
            if (!EnsureDependency("Microsoft Excel")) return;

            Collection owned;
            if ((_trackSource.SelectedItem as string ?? "").StartsWith("Live"))
            {
                if (manual ? !IpPrompt.EnsureSwitch(FindForm()) : !HealthMonitor.SwitchReachable(2500))
                {
                    Log($"Can't reach the Switch at {AppSettings.Current.SwitchIP}.");
                    _trackError = $"can't reach the Switch at {AppSettings.Current.SwitchIP}";
                    return;
                }
                Log("Reading party and PC boxes from the Switch...");
                var s = AppSettings.Current;
                owned = await Task.Run(() => { using var live = new LiveFrlg(s.SwitchIP, s.SwitchPort); return live.ReadCollection(); });
            }
            else
            {
                var save = await Task.Run(ResolveTrackerSave);
                if (save == null) { Log("No save file found - use 'Choose save file...' or set the game folder in the Pokémon HOME card."); return; }
                Log($"Reading {save.Path}");
                owned = Collection.FromSave(save.Save, save.Path);
            }
            Log($"{owned.Total} Pokémon, {owned.BySpecies.Count} species ({owned.BySpecies.Values.Count(e => e.Shiny)} shiny) - {owned.Source}");
            // Safety copy of the spreadsheet (and the save it was read from) before anything is changed.
            await Task.Run(() =>
            {
                if (BackupService.Backup(wb, spreadsheet: true) is { } b) Log("Backup made: " + Path.GetFileName(Path.GetDirectoryName(b)) + "\\" + Path.GetFileName(b));
                if (File.Exists(owned.Source)) BackupService.Backup(owned.Source, spreadsheet: false);
            });
            var result = await RunSta(() => SpreadsheetService.Update(wb, owned, Log));
            Log($"Done: {result.MarkedInBox} new 'In Box', {result.MarkedOT} new 'OT', {result.MarkedShiny} new 'Shiny' across {result.Sheets} sheet(s).");
            _trackError = null;
            Status($"Tracker updated: {Path.GetFileName(wb)}");
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            _trackError = ex.Message;
            if (manual) MessageBox.Show(FindForm(), ex.Message, "Collection Tracker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _syncing = false; }
    }

    // ================================================================ save export / game info
    private Card BuildSaveCard()
    {
        var card = new Card(Theme.GlyphSave, "Pokémon HOME folder + Game Info") { Dock = DockStyle.Fill, Margin = new Padding(8), AccentColor = Theme.Accent2 };
        card.Body.Controls.Add(Info("Point ShinyForge at the folder where this game's files / exported saves are stored.", Theme.Muted));
        _folderBox = new TextBox { Width = 380, Text = _gs.GameFolder };
        Theme.StyleInput(_folderBox);
        var browse = new FancyButton("Browse", Theme.GlyphFolder, Theme.PanelHi);
        browse.Click += (_, _) =>
        {
            var f = PickFolder("Folder with this game's files / exported save", _folderBox.Text);
            if (f == null) return;
            _folderBox.Text = f;
            _gs.GameFolder = f;
            AppSettings.Current.Save();
            ScanSaves();
        };
        card.Body.Controls.Add(Row(_folderBox, browse));

        var scan = new FancyButton("Find save", Theme.GlyphSync, Theme.PanelHi);
        scan.Click += (_, _) => ScanSaves();
        var export = new FancyButton("Export Pokémon + Game Info", Theme.GlyphDownload, Theme.Accent);
        export.Click += (_, _) => ExportSave();
        card.Body.Controls.Add(Row(scan, export));

        var pkhex = new FancyButton("Open in PKHeX", Theme.GlyphTools, Theme.PanelHi);
        pkhex.Click += async (_, _) => { if (await SysBotCurrent()) OpenInPKHeX(); };
        var info = new FancyButton("View Game Info", Theme.GlyphInfo, Theme.PanelHi);
        info.Click += (_, _) =>
        {
            var p = Path.Combine(_gs.GameFolder, "game info.txt");
            if (File.Exists(p)) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
            else MessageBox.Show(FindForm(), "Export first - the Game Info document is created together with the Pokémon HOME folder.", "Game Info");
        };
        card.Body.Controls.Add(Row(pkhex, info));
        var checkpoint = new FancyButton("Find in Checkpoint", Theme.GlyphSave, Theme.PanelHi);
        checkpoint.Click += async (_, _) => await FindInCheckpoint();
        // Checkpoint backs up 3DS / DS / Virtual Console / Switch saves - not GBA games (a 3DS can't run them in a way it sees)
        checkpoint.Visible = _game.Platform != Platform.GBA;
        var buttons = Row(checkpoint);
        if (_game.IsSwitch)
        {
            var trades = new FancyButton("Wireless trades (pokeldn)", Theme.GlyphWifi, Theme.PanelHi);
            trades.Click += async (_, _) => { if (!await SysBotCurrent()) return; using var f = new PokeldnForm(); f.ShowDialog(FindForm()); };
            buttons.Controls.Add(trades);
        }
        card.Body.Controls.Add(buttons);
        _saveStatus = Info("", Theme.Text);
        card.Body.Controls.Add(_saveStatus);
        if (Directory.Exists(_gs.GameFolder))
            Ui(ScanSaves);
        else
            Ui(UpdateTrackerSaveLabel);
        return card;
    }

    // ---------------------------------------------------------------- Checkpoint (SD card) saves
    private static readonly HashSet<string> CheckpointWarned = new();

    /// <summary>
    /// Finds this game's Checkpoint backups on the SD card (any drive) by title ID / name. Warns first that the user must
    /// save and back up themselves; on a rescan offers to make a fresh backup first; lets the user pick when there are
    /// several; copies the chosen backup to the PC and uses that copy for the HOME folder, Game Info and the tracker.
    /// </summary>
    private bool _switchFtpStarted; // ShinyForge opened the FTP app on the Switch during this Checkpoint step

    private async Task FindInCheckpoint()
    {
        if (!await SysBotCurrent()) return;
        try { await FindInCheckpointSteps(); }
        finally
        {
            // whatever happened after it (picked, cancelled, failed): offer to close the FTP app and go back to the game
            if (_switchFtpStarted) { _switchFtpStarted = false; await OfferBackToGame(); }
        }
    }

    private async Task FindInCheckpointSteps()
    {
        var owner = FindForm();
        var rescan = !string.IsNullOrEmpty(_gs.CheckpointBackup);
        if (!CheckpointWarned.Contains(_game.Id))
        {
            if (MessageBox.Show(owner,
                    "Before ShinyForge uses your Checkpoint saves:\n\n" +
                    "  • Save your game in-game first.\n" +
                    "  • Back up the save YOURSELF with Checkpoint on your console. ShinyForge only reads those backups - it can't make them, " +
                    "and it never changes anything on your SD card.\n" +
                    "  • Keep your own copies of your saves somewhere safe.\n\n" +
                    $"ShinyForge copies the backup you pick to\n{CheckpointService.GameFolder(_game)}\n" +
                    "and uses that copy for the Pokémon HOME folder, Game Info and the Pokédex tracker.\n\n" +
                    "Are you ready to continue?",
                    "ShinyForge - Checkpoint saves", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            CheckpointWarned.Add(_game.Id);
        }
        var console = _game.IsSwitch ? "Switch" : "3DS";
        var source = MessageBox.Show(owner,
            "Where are the Checkpoint backups?\n\n" +
            $"Yes = on the SD card INSIDE the {console} - read over WiFi (Checkpoint's FTP server, or the ftpd app)\n" +
            "No = an SD card put into this PC (or a copy of it)",
            "ShinyForge - Checkpoint saves", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (source == DialogResult.Cancel) return;
        var wifi = source == DialogResult.Yes;
        if (rescan)
        {
            var r = MessageBox.Show(owner,
                "Would you like to back up a fresh save before ShinyForge rescans?\n\n" +
                "Yes = I'll make a new Checkpoint backup now (the steps are shown next)\n" +
                "No = rescan the backups that are already on the SD card",
                "ShinyForge - fresh backup?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            if (r == DialogResult.Yes &&
                MessageBox.Show(owner,
                    $"1. Save your game in Pokémon {_game.Title}, then close the game.\n" +
                    "2. Open Checkpoint on the console and select the game.\n" +
                    "3. Choose Backup and confirm (the suggested date-and-time name is fine).\n" +
                    (wifi ? "4. Leave Checkpoint open with its FTP server on (Checkpoint's settings).\n\n" +
                            "Click OK when the fresh backup is made."
                          : "4. Put the console's SD card into this PC (or reconnect it).\n\n" +
                            "Click OK when the fresh backup is on the SD card and the card is connected."),
                    "ShinyForge - make a fresh backup", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;
        }

        _saveStatus.ForeColor = Theme.Muted;
        List<string> roots;
        var where = _game.IsSwitch ? @"switch\Checkpoint\saves" : @"3ds\Checkpoint\saves";
        if (wifi)
        {
            var root = await MirrorFromConsole(owner);
            if (root == null) return;
            roots = new List<string> { root };
        }
        else
        {
            _saveStatus.Text = "Looking for Checkpoint on the SD card...";
            roots = await Task.Run(() => CheckpointService.FindRoots(_game.IsSwitch));
        }
        if (!wifi && roots.Count == 0)
        {
            if (MessageBox.Show(owner, $"No Checkpoint folder ({where}) was found on any drive.\n\n" +
                                       "Put the console's SD card into this PC - or, if you copied it to the PC, choose that folder.\n\nChoose a folder now?",
                    "ShinyForge - Checkpoint", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            { _saveStatus.Text = "No Checkpoint folder found."; return; }
            var f = PickFolder("The SD card (or the Checkpoint / saves folder copied from it)", AppSettings.Current.CheckpointFolder);
            if (f == null) { _saveStatus.Text = ""; return; }
            AppSettings.Current.CheckpointFolder = f;
            AppSettings.Current.Save();
            roots = await Task.Run(() => CheckpointService.FindRoots(_game.IsSwitch));
            if (roots.Count == 0) { _saveStatus.ForeColor = Theme.Warn; _saveStatus.Text = $"That folder doesn't contain {where}."; return; }
        }

        _saveStatus.Text = "Reading the Checkpoint backups of this game...";
        var backups = await Task.Run(() => CheckpointService.FindBackups(_game, roots));
        if (backups.Count == 0)
        {
            var ids = CheckpointService.TitleIds.TryGetValue(_game.Id, out var x) ? $" (title ID {string.Join(" / ", x)})" : "";
            _saveStatus.ForeColor = Theme.Warn;
            _saveStatus.Text = $"No Checkpoint backup of Pokémon {_game.Title} was found.";
            MessageBox.Show(owner, $"No Checkpoint backup of Pokémon {_game.Title}{ids} with a readable save was found in:\n\n  {string.Join("\n  ", roots)}\n\n" +
                                   "Back the game up with Checkpoint on the console first, then try again.", "ShinyForge - Checkpoint");
            return;
        }
        var chosen = backups[0];
        if (backups.Count > 1)
        {
            using var picker = new CheckpointPicker(_game.Title, backups);
            if (picker.ShowDialog(owner) != DialogResult.OK || picker.Chosen == null) { _saveStatus.Text = ""; return; }
            chosen = picker.Chosen;
        }

        _saveStatus.Text = "Copying the backup to this PC...";
        FoundSave copied;
        try { copied = await Task.Run(() => CheckpointService.CopyToPc(chosen, _game)); }
        catch (Exception ex) { _saveStatus.ForeColor = Theme.Bad; _saveStatus.Text = "Couldn't copy the backup: " + ex.Message; return; }
        _gs.CheckpointBackup = chosen.Folder;
        _gs.GameFolder = CheckpointService.GameFolder(_game);
        _gs.SaveFilePath = copied.Path;
        AppSettings.Current.Save();
        _folderBox.Text = _gs.GameFolder;
        _save = copied;
        _saveStatus.ForeColor = Theme.Good;
        _saveStatus.Text = $"✔ Checkpoint backup \"{chosen.BackupName}\" ({chosen.Time:yyyy-MM-dd HH:mm}) copied to this PC.\n" +
                           $"    Trainer {copied.Save.OT}  •  TID {copied.Save.DisplayTID}  •  {copied.Save.Version}  •  {copied.Save.PlayedHours}h {copied.Save.PlayedMinutes:00}m";
        UpdateTrackerSaveLabel();
    }

    /// <summary>
    /// Reads this game's Checkpoint backups from the SD card inside the console over WiFi (FTP) into the local mirror.
    /// Returns the mirrored saves folder, or null if cancelled / it failed (the status line says why).
    /// </summary>
    private async Task<string?> MirrorFromConsole(IWin32Window? owner)
    {
        var console = _game.IsSwitch ? "Switch" : "3DS";
        var ip = _game.IsSwitch ? IpPrompt.GetOrAskSwitch(owner) : IpPrompt.GetOrAsk(owner);
        if (ip == null) { _saveStatus.Text = ""; return null; }
        int? port;
        var startedApp = false;
        if (!_game.IsSwitch)
        {
            // The 3DS can't be controlled from here: the user closes the game and opens the FTP app.
            var host3 = ip;
            if (await Task.Run(() => ConsoleSdService.FindPort(host3)) == null &&
                MessageBox.Show(owner,
                    "Before ShinyForge continues, on the 3DS:\n\n" +
                    "  1. Save your game, then close it (HOME → Close).\n" +
                    "  2. Open Checkpoint (with its FTP server turned on in Checkpoint's settings) or the ftpd app.\n" +
                    "  3. Leave it open while ShinyForge reads the saves.\n\n" +
                    "Click OK when the FTP app is running.",
                    "ShinyForge - open the FTP app on the 3DS", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            { _saveStatus.Text = ""; return null; }
        }
        while (true)
        {
            _saveStatus.Text = $"Connecting to the {console} at {ip} (FTP)...";
            var host = ip;
            port = await Task.Run(() => ConsoleSdService.FindPort(host));
            if (port != null) break;
            if (_game.IsSwitch && !startedApp)
            {
                startedApp = true;
                var (started, cancelled) = await StartSwitchFtpApp(owner, ip);
                if (cancelled) { _saveStatus.Text = ""; return null; }
                if (started != null) { port = started; _switchFtpStarted = true; break; }
            }
            var r = MessageBox.Show(owner, ConsoleSdService.HowToTurnOn(_game.IsSwitch, ip) +
                                           "\n\nYes = try again    No = enter a different IP address",
                "ShinyForge - read the console's SD card", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
            if (r == DialogResult.Cancel) { _saveStatus.Text = ""; return null; }
            if (r == DialogResult.No)
            {
                ip = _game.IsSwitch ? IpPrompt.AskSwitch(owner) : IpPrompt.Ask(owner);
                if (ip == null) { _saveStatus.Text = ""; return null; }
            }
        }
        try
        {
            var (h, p) = (ip, port.Value);
            var result = await Task.Run(() => ConsoleSdService.Mirror(_game, h, p, msg => Ui(() => _saveStatus.Text = msg)));
            _saveStatus.Text = $"Read {result.TitleFolders} Checkpoint folder(s) of this game from the {console} ({result.Downloaded} file(s) downloaded).";
            return result.Root;
        }
        catch (Exception ex)
        {
            _saveStatus.ForeColor = Theme.Bad;
            _saveStatus.Text = $"Couldn't read the {console}'s SD card: {ex.Message}";
            AppPaths.Log("Console SD read failed: " + ex);
            return null;
        }
    }

    /// <summary>
    /// Switch only: makes sure no game is open (the user closes it - ShinyForge waits for their OK and checks), then opens the
    /// Homebrew Menu by starting a game while holding R, finds ftpd / Checkpoint there and starts it (sys-botbase).
    /// Returns the FTP port, or null when it couldn't (Cancelled = the user stopped).
    /// </summary>
    private async Task<(int? Port, bool Cancelled)> StartSwitchFtpApp(IWin32Window? owner, string ip)
    {
        var botPort = AppSettings.Current.SwitchPort;
        if (!HealthMonitor.SwitchReachable(2500)) return (null, false); // no sys-botbase: the user starts the FTP app
        if (HuntService.IsPythonHuntRunning())
        {
            MessageBox.Show(owner, "The FireRed/LeafGreen hunter is running on the Switch. Stop the hunt first - ShinyForge won't press " +
                                   "buttons on the Switch while it's hunting.", "ShinyForge - open the FTP app", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return (null, true);
        }
        while (true)
        {
            _saveStatus.Text = "Checking whether a game is open on the Switch...";
            string? running;
            try { running = await Task.Run(() => ConsoleFtpLauncher.RunningGame(ip, botPort)); }
            catch (Exception ex)
            {
                // never press buttons without knowing that no game is open
                AppPaths.Log("Switch game check failed: " + ex.Message);
                return (null, false);
            }
            if (running == null) break;
            if (MessageBox.Show(owner,
                    "A game is open on the Switch. It has to be closed before ShinyForge can open the FTP app.\n\n" +
                    "  1. Save your game.\n" +
                    "  2. Press HOME, then X on the game, and choose Close.\n\n" +
                    "Click OK once the game is closed.",
                    "ShinyForge - close the game first", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return (null, true);
        }
        if (MessageBox.Show(owner,
                "The game is closed. ShinyForge will now, through sys-botbase:\n\n" +
                "  • start a game while holding R - the Homebrew Menu opens instead of the game,\n" +
                "  • find ftpd (or Checkpoint) in the Homebrew Menu and start it.\n\n" +
                "Don't touch the Switch's controller until it's done (about a minute).",
                "ShinyForge - open the FTP app", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            return (null, true);
        try
        {
            var port = await Task.Run(() => ConsoleFtpLauncher.StartFtpApp(ip, botPort, msg => Ui(() => _saveStatus.Text = msg)));
            return (port, false);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Starting the Switch FTP app failed: " + ex);
            MessageBox.Show(owner, "ShinyForge couldn't start the FTP app on the Switch:\n\n" + ex.Message +
                                   "\n\nYou can start it yourself instead (the steps are shown next).",
                "ShinyForge - open the FTP app", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return (null, false);
        }
    }

    /// <summary>After the WiFi save read: close the FTP app, open the game and (FireRed / LeafGreen) load the save and start the hunt.</summary>
    private async Task OfferBackToGame()
    {
        var hunt = _game.Bot == BotKind.FrlgSwitch && !HuntService.IsPythonHuntRunning();
        if (MessageBox.Show(FindForm(),
                "ShinyForge is done with the Switch's SD card.\n\n" +
                $"Yes = close the FTP app and open Pokémon {_game.Title}" + (hunt ? ", load your save and start the shiny hunt" : "") + "\n" +
                "No = leave the FTP app open (close it yourself: HOME, X, Close)",
                "ShinyForge - back to the game", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        if (await OpenGameOnSwitch(t => _saveStatus.Text = t) && hunt && EnsureDependency("Python 3"))
            StartHuntNow("python");
    }

    /// <summary>
    /// Before a FireRed / LeafGreen hunt: is the game open on the Switch with the save loaded? If not (nothing open, the FTP
    /// app / Homebrew Menu still open), offer to open it. False = don't start the hunt.
    /// </summary>
    private async Task<bool> EnsureGameOpen()
    {
        var s = AppSettings.Current;
        var ids = SwitchGameLauncher.TitleIds(_game);
        if (ids.Length == 0) return true;
        Status("Checking the Switch...");
        string? running;
        bool ftp;
        try
        {
            running = await Task.Run(() => ConsoleFtpLauncher.RunningGame(s.SwitchIP, s.SwitchPort));
            ftp = running != null && await Task.Run(() => ConsoleSdService.FindPort(s.SwitchIP)) != null;
        }
        catch { return true; } // can't tell - the hunter reports problems itself
        if (running != null && ids.Contains(running) && !ftp) return true;
        if (running != null && !ftp)
        {
            MessageBox.Show(FindForm(), $"Another game is open on the Switch ({running}). Save and close it, then start the hunt again.",
                "Shiny Hunt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        var r = MessageBox.Show(FindForm(),
            (ftp ? "The FTP app is still open on the Switch." : $"Pokémon {_game.Title} isn't open on the Switch.") + "\n\n" +
            $"Yes = open Pokémon {_game.Title} and load your save first (ShinyForge finds it on the HOME menu or in All Software by its icon)\n" +
            "No = start the hunt anyway",
            "Shiny Hunt", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return r == DialogResult.No || (r == DialogResult.Yes && await OpenGameOnSwitch(Status));
    }

    /// <summary>
    /// Before recording a Switch game: is this game the one on the Switch's screen? Nothing / the FTP app open → it's opened.
    /// Running but behind the HOME menu → brought back. Another game open:
    ///   - a Pokémon game ShinyForge can save → asks "save it first?" (Yes = save + close, No = close), then opens this one,
    ///   - a Pokémon game it can't save → the user saves it, then ShinyForge closes it,
    ///   - any other game → the user saves and closes it themselves (ShinyForge never closes games it doesn't know).
    /// Switch not reachable (no IP, offline) → record anyway. False = don't record.
    /// </summary>
    private async Task<bool> EnsureGameForRecording()
    {
        var s = AppSettings.Current;
        if (SwitchGameLauncher.TitleIds(_game).Length == 0 || string.IsNullOrWhiteSpace(s.SwitchIP) || HuntService.IsPythonHuntRunning()) return true;
        Status("Checking which game is on the Switch...");
        (string? TitleId, bool Homebrew) running;
        try { running = await Task.Run(() => SwitchGameLauncher.Running(s.SwitchIP, s.SwitchPort)); }
        catch (Exception ex) { Status("Couldn't reach the Switch - recording without checking the game."); AppPaths.Log("Recording: Switch check failed: " + ex.Message); return true; }
        var mode = SwitchGameLauncher.OtherGame.Refuse;
        if (running.TitleId is { } id && !running.Homebrew && !SwitchGameLauncher.TitleIds(_game).Contains(id))
        {
            var name = SwitchGameLauncher.GameName(id);
            var head = $"{name} is open on the Switch, not Pokémon {_game.Title}.\n\n";
            if (SwitchGameLauncher.GameOf(id) == null)
            {
                if (MessageBox.Show(FindForm(), head + "Save your progress in it and close it yourself (HOME, X, Close), then press OK - " +
                                    $"ShinyForge then starts Pokémon {_game.Title} and records.",
                        "Record Gameplay", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return false;
                string? still;
                try { still = (await Task.Run(() => SwitchGameLauncher.Running(s.SwitchIP, s.SwitchPort))).TitleId; }
                catch { still = null; } // can't tell - opening the game reports it if it's still open
                if (still == id)
                {
                    MessageBox.Show(FindForm(), $"{name} is still open on the Switch - close it, then start recording again.",
                        "Record Gameplay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            else if (SwitchGameLauncher.CanSave(id))
            {
                var r = MessageBox.Show(FindForm(), head + $"Should ShinyForge save {name} before closing it?\n\n" +
                    $"Yes = save it, close it, then start Pokémon {_game.Title} and record\n" +
                    "No = close it WITHOUT saving (unsaved progress is lost)",
                    "Record Gameplay", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return false;
                mode = r == DialogResult.Yes ? SwitchGameLauncher.OtherGame.SaveAndClose : SwitchGameLauncher.OtherGame.Close;
            }
            else
            {
                if (MessageBox.Show(FindForm(), head + $"ShinyForge can't save {name} itself. Save your progress in it now, then press OK - " +
                                    $"ShinyForge closes it, starts Pokémon {_game.Title} and records.",
                        "Record Gameplay", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return false;
                mode = SwitchGameLauncher.OtherGame.Close;
            }
        }
        return await OpenGameOnSwitch(Status, mode);
    }

    /// <summary>Opens this game on the Switch (closing the FTP app first) and, for FireRed / LeafGreen, loads the save.</summary>
    private async Task<bool> OpenGameOnSwitch(Action<string> status, SwitchGameLauncher.OtherGame otherGame = SwitchGameLauncher.OtherGame.Refuse)
    {
        var s = AppSettings.Current;
        if (HuntService.IsPythonHuntRunning())
        {
            MessageBox.Show(FindForm(), "The hunter is running - stop it first.", "ShinyForge - open the game", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        try
        {
            await Task.Run(() => SwitchGameLauncher.OpenGame(s.SwitchIP, s.SwitchPort, _game, m => Ui(() => status(m)), default, otherGame));
            status($"Pokémon {_game.Title} is open on the Switch" + (_game.Bot == BotKind.FrlgSwitch ? " with your save loaded." : "."));
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Opening the game on the Switch failed: " + ex);
            status(ex.Message);
            MessageBox.Show(FindForm(), ex.Message, "ShinyForge - open the game", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    /// <summary>Starts the chosen hunt bot (and, for the Python hunter, the rolling shiny-clip buffer).</summary>
    private void StartHuntNow(string key)
    {
        try
        {
            _gs.HuntBot = key;
            AppSettings.Current.Save();
            HuntService.ResetAutoFixes();
            HuntService.Start(_game, key);
            Status(key == "python" ? "Python hunter started in its own window." : "SysBot.exe opened with the routine pre-selected - press Start All there.");
            if (key == "python" && HuntService.IsPythonHuntRunning()) StartClipBuffer();
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, "Shiny Hunt", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        RefreshHuntStatus();
    }

    private void ScanSaves()
    {
        _gs.GameFolder = _folderBox.Text;
        AppSettings.Current.Save();
        _saveStatus.Text = "Searching for save files...";
        Task.Run(() => SaveService.FindSaves(_gs.GameFolder, _game)).ContinueWith(t => Ui(() =>
        {
            var saves = t.Result;
            _save = saves.FirstOrDefault();
            _saveStatus.ForeColor = _save != null ? Theme.Good : Theme.Warn;
            _saveStatus.Text = _save == null
                ? "No save file for this game was found in that folder (searched 3 folder levels deep)."
                : $"✔ Save found: {Path.GetFileName(_save.Path)}\n    Trainer {_save.Save.OT}  •  TID {_save.Save.DisplayTID}  •  {_save.Save.Version}  •  " +
                  $"{_save.Save.PlayedHours}h {_save.Save.PlayedMinutes:00}m" + (saves.Count > 1 ? $"  ({saves.Count} saves found, newest used)" : "");
            UpdateTrackerSaveLabel(); // the tracker defaults to the save found here
        }));
    }

    private void ExportSave()
    {
        if (_save == null) { ScanSaves(); MessageBox.Show(FindForm(), "No save file loaded yet - choose the game folder first.", "Export"); return; }
        try
        {
            var baseFolder = Path.GetDirectoryName(_save.Path)!;
            if (!string.IsNullOrWhiteSpace(_gs.GameFolder) && Directory.Exists(_gs.GameFolder)) baseFolder = _gs.GameFolder;
            BackupService.Backup(_save.Path, spreadsheet: false);
            var (folder, count) = SaveService.ExportPokemon(_save.Save, _game, baseFolder);
            var infoPath = SaveService.WriteGameInfo(_save.Save, _game, _save.Path, baseFolder);
            File.Copy(infoPath, Path.Combine(folder, "game info.txt"), true);
            _saveStatus.ForeColor = Theme.Good;
            _saveStatus.Text = $"✔ Exported {count} Pokémon to '{Path.GetFileName(folder)}' and wrote 'game info.txt'.";
            Status($"Exported {count} Pokémon from {_game.Title}.");
            Process.Start("explorer.exe", folder);
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void OpenInPKHeX()
    {
        if (!EnsureDependency("PKHeX")) return;
        var exe = DependencyService.PathOf("PKHeX");
        if (exe == null) return;
        var args = _save != null ? $"\"{_save.Path}\"" : "";
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _recorder.Stop();
            _hunt3ds?.Cancel();
            _poll.Dispose();
            _watcher?.Dispose();
            _liveTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
