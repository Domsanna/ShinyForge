using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PKHeX.Core;
using ShinyForge.Core;
using ShinyForge.Services;
using ShinyForge.Services.ThreeDS;

namespace ShinyForge.UI;

/// <summary>One game: shiny hunting, recording, the Pokédex tracker, and saves (HOME folder, Game Info, Checkpoint, wireless trades).</summary>
public sealed class GamePage : UserControl, IDisposable
{
    private readonly Game _game;
    private readonly GameSettings _gs;
    private readonly RecordingService _recorder = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ShinyClipService _clips = new();
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _liveTimer;
    private DateTime _lastAutoSync = DateTime.MinValue;
    private bool _syncing, _disposed;
    private readonly string _healthKey;

    private TextBlock _huntStatus = Skin.T(""), _recStatus = null!, _saveStatus = null!, _sheetLabel = null!, _trackSaveLabel = null!;
    private TextBox _folderBox = null!, _recFolder = null!;
    private LogView _trackLog = null!;
    private ComboBox? _source, _audio;
    private ComboBox _segments = null!, _trackSource = null!;
    private FancyButton _recButton = null!, _downloadSheet = null!;
    private CheckBox _autoMonitor = null!;
    private FoundSave? _save;
    private volatile bool _trackLive;
    private volatile string? _trackError;
    private DexWindow? _dex;

    public event Action<string>? StatusMessage;
    public bool IsRecording => _recorder.IsRecording;

    public GamePage(Game game)
    {
        _game = game;
        _gs = AppSettings.Current.For(game.Id);
        _healthKey = "game-" + game.Id;

        var info = Skin.T(game.AutomationOnly
                ? $"{game.PlatformLabel}\n\nSort your Pokémon HOME boxes automatically with Pokémon Automation's programs."
                : $"{game.PlatformLabel}  •  Generation {game.Gen}\n\n" +
                  (HuntService.OptionsFor(game).Count > 0 || Games3DS.All.Any(p => p.ShinyForgeId == game.Id) ? "★ Automatic shiny hunting available.\n" : "") +
                  "Record gameplay in timed segments, keep your Pokédex tracker spreadsheet up to date, back up every Pokémon " +
                  "from your save into a 'Pokemon Home' folder and get a Game Info report.", 13, Skin.Muted);
        info.Margin = new Thickness(20, 24, 20, 0);
        info.MaxWidth = 640;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 4, 0, 0), Children = { new GameTile(game, large: true), info } };
        if (game.AutomationOnly)
        {
            Content = new ScrollViewer { Content = new StackPanel { Children = { header, BuildAutomationCard() } } };
            return;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("500,610"), Margin = new Thickness(10, 6, 10, 20) };
        void Put(Control c, int col, int row) { Grid.SetColumn(c, col); Grid.SetRow(c, row); grid.Children.Add(c); }
        Put(BuildHuntCard(), 0, 0);
        Put(BuildRecordCard(), 1, 0);
        Put(BuildTrackerCard(), 0, 1);
        Put(BuildSaveCard(), 1, 1);
        Content = new ScrollViewer { Content = new StackPanel { Children = { header, grid } } };

        _recorder.Status += s => Dialogs.Ui(() => _recStatus.Text = s);
        _poll.Tick += (_, _) => RefreshHuntStatus();
        _poll.Start();
        if (_gs.AutoMonitor && App.ScreenshotDir == null) Dispatcher.UIThread.Post(() => _autoMonitor.IsChecked = true);
        HealthMonitor.Register(_healthKey, CheckHealth);
        if (game.Bot == BotKind.FrlgSwitch) HuntService.Found += OnFound;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HealthMonitor.Unregister(_healthKey);
        HuntService.Found -= OnFound;
        _clips.Stop();
        _recorder.Stop();
        _hunt3ds?.Cancel();
        _poll.Stop();
        _watcher?.Dispose();
        _liveTimer?.Stop();
    }

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
                items.Add(HealthMonitor.Error($"Collection tracker ({_game.Title})", $"Live monitoring can't reach the Switch at {s.SwitchIP} - its IP may have changed."));
        }
        if (_hunt3ds != null && (DateTime.Now - _last3dsActivity).TotalMinutes > 4)
            items.Add(HealthMonitor.Warn($"3DS hunt ({_game.Title})", $"No activity from the 3DS bot for {(int)(DateTime.Now - _last3dsActivity).TotalMinutes} minutes - is the game still running?"));
        if (_trackError != null)
            items.Add(HealthMonitor.Warn($"Collection tracker ({_game.Title})", "Last sync failed: " + _trackError));
        if (_clips.Problem() is { } cp)
        {
            items.Add(HealthMonitor.Warn($"Shiny clips ({_game.Title})", cp + " - restarting it."));
            Dialogs.Ui(StartClipBuffer);
        }
        return items;
    }

    // ================================================================ helpers
    private static TextBlock Info(string t, Color? c = null) { var b = Skin.T(t, 13, c ?? Skin.Muted); b.Margin = new Thickness(0, 4); return b; }
    private void Status(string s) => StatusMessage?.Invoke(s);
    private static string? Sel(ComboBox? c) => c?.SelectedItem as string;

    private async Task<bool> EnsureDependency(string name)
    {
        var dep = DependencyService.All.FirstOrDefault(d => d.Name == name);
        if (dep == null) return true; // not a tool on this OS
        DependencyService.Refresh(dep);
        if (dep.State != DepState.Missing) return true;
        await ToolsWindow.AskInstall(this, dep);
        DependencyService.Refresh(dep);
        return dep.State != DepState.Missing;
    }

    // ================================================================ Switch tools
    /// <summary>The connection line + sys-botbase (version, install sys-botbase-cpp) + Pokémon Automation. USB is Windows-only.</summary>
    private void AddSwitchTools(Card card)
    {
        var s = AppSettings.Current;
        card.Body.Children.Add(Info($"Switch: {s.SwitchIP}:{s.SwitchPort} over WiFi (sys-botbase) - IP in Settings."));
        var sbb = new FancyButton("sys-botbase", Skin.GlyphSettings, Skin.PanelHi);
        sbb.Click += async (_, _) => await SysBotBaseMenu();
        var pa = new FancyButton("Pokémon Automation", Skin.GlyphPlay, Skin.PanelHi);
        pa.Click += async (_, _) => await PokemonAutomationMenu();
        card.Body.Children.Add(Skin.Wrap(sbb, pa));
    }

    /// <summary>The whole page of an app like Pokémon HOME: only Pokémon Automation.</summary>
    private Card BuildAutomationCard()
    {
        var card = new Card(Skin.GlyphPlay, "Pokémon Automation") { Margin = new Thickness(20, 12, 20, 20), MaxWidth = 600, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        card.Body.Children.Add(Info($"Programs for Pokémon HOME: {PokemonAutomation.ProgramsSummary(_game)}. Open Pokémon HOME on the Switch first."));
        var pa = new FancyButton("Pokémon Automation", Skin.GlyphPlay, Skin.Accent);
        pa.Click += async (_, _) => await PokemonAutomationMenu();
        card.Body.Children.Add(Skin.Wrap(pa));
        return card;
    }

    /// <summary>Shows which sys-botbase the Switch runs and offers to install / update Pokémon Automation's sys-botbase-cpp.</summary>
    private async Task SysBotBaseMenu()
    {
        var s = AppSettings.Current;
        Status("Asking the Switch which sys-botbase it runs...");
        var version = await Task.Run(() => SysBotBaseInstaller.InstalledVersion(s.SwitchIP, s.SwitchPort));
        var shots = await Task.Run(() => SysBotBaseInstaller.ScreenshotWorks(s.SwitchIP, s.SwitchPort, version));
        Status("");
        var backup = SysBotBaseInstaller.LatestBackup();
        var cpp = SysBotBaseInstaller.IsCpp(version);
        var frozen = version == SysBotBaseInstaller.Frozen;
        const string restore = "Restore previous";
        if (frozen)
        {
            await Dialogs.Info(this, SysBotBaseInstaller.FrozenHelp, "sys-botbase"); // a restart is all it needs
            return;
        }
        if (backup != null && (cpp || version == null) && (version == null || !shots))
        {
            // nothing answers, or sys-botbase-cpp can't take screenshots (ShinyForge needs them): offer the backed-up one
            if (await Dialogs.Show(this, (version == null ? $"Nothing answers at {s.SwitchIP}:{s.SwitchPort}."
                        : SysBotBaseInstaller.CppNeedsCaptureCard) +
                    "\n\nRestore the previous sys-botbase? Start the FTP app yourself first (on the HOME menu hold R and press A on any game until " +
                    "the Homebrew Menu appears, then choose ftpd), then restart the Switch afterwards.", "sys-botbase", restore, Dialogs.Cancel) != restore) return;
            try
            {
                await Task.Run(() => SysBotBaseInstaller.Restore(s.SwitchIP, backup, m => Dialogs.Ui(() => Status(m))));
                await Dialogs.Info(this, "The previous sys-botbase is back on the SD card. Restart the Switch to start it.", "sys-botbase");
            }
            catch (Exception ex) { Status(ex.Message); await Dialogs.Info(this, ex.Message, "sys-botbase"); }
            return;
        }
        if (version == null)
        {
            await Dialogs.Info(this, $"Nothing answers at {s.SwitchIP}:{s.SwitchPort}. Is the Switch on, on WiFi, and running Atmosphère?", "sys-botbase");
            return;
        }
        var install = cpp ? "Reinstall / update" : "Install sys-botbase-cpp";
        var r = await Dialogs.Show(this,
            (cpp ? $"The Switch runs sys-botbase-cpp {version} (Pokémon Automation)." : $"The Switch runs sys-botbase {version} (the original).") + "\n\n" +
            "sys-botbase-cpp (github.com/PokemonAutomation/sys-botbase-cpp, GPL-3.0) is a rewrite with the same commands: lighter on memory, " +
            "a fix for random crashes, exact button timing and a log file on the SD card. Installing copies it onto the SD card over WiFi with " +
            "the FTP app (the old files are backed up first); then restart the Switch. No game may be open - save and close it first.",
            "sys-botbase", install, Dialogs.Cancel);
        if (r != install) return;
        if (HuntService.IsPythonHuntRunning()) { await Dialogs.Info(this, "The hunter is running - stop it first.", "sys-botbase"); return; }
        try
        {
            var saved = await SysBotBaseInstaller.Install(s.SwitchIP, s.SwitchPort, "wifi", m => Dialogs.Ui(() => Status(m)));
            Status("sys-botbase-cpp is on the SD card - restart the Switch.");
            await Dialogs.Info(this, "sys-botbase-cpp is on the SD card. Restart the Switch (hold POWER → Power Options → Restart) to start it.\n\n" +
                                     $"The old sys-botbase files are backed up in:\n{saved}\n\nAfter the restart, open sys-botbase again: ShinyForge " +
                                     "checks that screenshots work (it needs them) and offers to put the old one back if they don't.", "sys-botbase");
        }
        catch (Exception ex)
        {
            AppPaths.Log("sys-botbase install failed: " + ex);
            Status(ex.Message);
            await Dialogs.Info(this, ex.Message, "sys-botbase");
        }
    }

    /// <summary>Starts Pokémon Automation's Computer Control (Windows) or links its download, and opens this game's program list.</summary>
    private async Task PokemonAutomationMenu()
    {
        var found = PokemonAutomation.Find() != null;
        var start = found ? "Start it + programs" : "Download + setup guide";
        var r = await Dialogs.Show(this,
            $"Pokémon Automation's Computer Control has tested programs for this game ({PokemonAutomation.ProgramsSummary(_game)}). It runs on an " +
            "UNMODDED Switch through a microcontroller (e.g. an ESP32-S3 with their firmware - not at the same time as pokeldn) plus a capture " +
            "card, or on a modded Switch through sys-botbase. It's their program, for personal, non-profit use." +
            (found ? "" : "\n\nIt isn't on this computer yet: download it from their GitHub and unzip it (not into OneDrive)."),
            "Pokémon Automation", start, "Only the programs", Dialogs.Cancel);
        if (r == null || r == Dialogs.Cancel) return;
        try
        {
            if (r == start && found && await PaEquipmentReady()) PokemonAutomation.Launch();
            if (r == start && !found) { PokemonAutomation.Open(PokemonAutomation.Releases); PokemonAutomation.Open(PokemonAutomation.SetupGuide); }
            PokemonAutomation.Open(PokemonAutomation.ProgramsUrl(_game));
        }
        catch (Exception ex) { await Dialogs.Info(this, ex.Message, "Pokémon Automation"); }
    }

    // ================================================================ shiny hunt
    private Card BuildHuntCard()
    {
        var card = new Card(Skin.GlyphSparkle, "Shiny Hunt (automatic)");
        var profile3ds = Games3DS.All.FirstOrDefault(g => g.ShinyForgeId == _game.Id);
        if (profile3ds != null) { Build3DSHunt(card, profile3ds); return card; }
        var options = HuntService.OptionsFor(_game);
        if (options.Count == 0)
        {
            card.Body.Children.Add(Info(HuntService.UnsupportedReason(_game)));
            if (_game.IsSwitch) AddSwitchTools(card);
            return card;
        }
        var radios = new List<RadioButton>();
        foreach (var o in options)
        {
            var rb = new RadioButton { Content = Skin.T(o.Title, 13.5, Skin.Text, FontWeight.SemiBold), Tag = o.Key, GroupName = "bot-" + _game.Id, IsChecked = radios.Count == 0 || _gs.HuntBot == o.Key };
            radios.Add(rb);
            card.Body.Children.Add(rb);
            var d = Info(o.Description);
            d.Margin = new Thickness(28, 0, 0, 4);
            card.Body.Children.Add(d);
        }
        if (_game.IsSwitch) AddSwitchTools(card);
        var start = new FancyButton("Start hunting", Skin.GlyphPlay, Skin.Accent);
        var stop = new FancyButton("Stop Python hunter", Skin.GlyphStop, Skin.StopRed);
        start.Click += async (_, _) =>
        {
            var key = radios.First(r => r.IsChecked == true).Tag as string ?? "";
            if (key == "python" && !await EnsureDependency("Python 3")) return;
            if (key == "python" && !await EnsurePythonPackages()) return;
            if (key == "sysbot" && !await EnsureDependency("SysBot.exe")) return;
            if (!await IpPrompt.EnsureSwitch(this)) return;
            // The game must be open (FireRed / LeafGreen: with the save loaded - the hunter decides its mode from where you stand).
            // SysBot.exe too: started without the game it stops at once ("Title ID ... Disconnecting").
            if ((key == "python" || key == "sysbot") && !await EnsureGameOpen()) return;
            await StartHuntNow(key);
        };
        stop.Click += (_, _) => { HuntService.StopPythonHunt(); _clips.Stop(); RefreshHuntStatus(); Status("Python hunter stopped."); };
        card.Body.Children.Add(options.Any(o => o.Key == "python") ? Skin.Wrap(start, stop) : Skin.Wrap(start));
        if (_game.Bot == BotKind.FrlgSwitch)
        {
            var pause = new FancyButton("Pause / Resume", Skin.GlyphStop, Skin.PanelHi);
            pause.Click += (_, _) =>
            {
                HuntService.SetPaused(!HuntService.IsPaused);
                Status(HuntService.IsPaused ? "Python hunter will pause after the current attempt." : "Python hunter resumed.");
                RefreshHuntStatus();
            };
            var opts = new FancyButton("Hunt options", Skin.GlyphSettings, Skin.PanelHi);
            opts.Click += async (_, _) => { await new HuntOptionsWindow(_game, rngAvailable: true).ShowDialog(Dialogs.Owner(this)!); RefreshHuntStatus(); };
            var stats = new FancyButton("Statistics", Skin.GlyphInfo, Skin.PanelHi);
            stats.Click += (_, _) => new HuntStatsWindow("Python").Show(Dialogs.Owner(this)!);
            card.Body.Children.Add(Skin.Wrap(pause, opts, stats));
        }
        _huntStatus = Info("", Skin.Good);
        card.Body.Children.Add(_huntStatus);
        return card;
    }

    // ---------------------------------------------------------------- 3DS hunting (Luma3DS)
    private CancellationTokenSource? _hunt3ds;
    private DateTime _last3dsActivity = DateTime.Now;
    private Bot3DS? _bot3ds;
    private static int _3dsHunts;
    /// <summary>Is a 3DS hunt running on any game page (for keep-awake)?</summary>
    public static bool Any3DSHunt => _3dsHunts > 0;

    private void Build3DSHunt(Card card, Game3DS profile)
    {
        card.Body.Children.Add(Info("Controls your 3DS over WiFi (Luma3DS: Rosalina → InputRedirection + Debugger options → Enable debugger). Recognises the game, logs every encounter and stops on a shiny."));
        var mode = Skin.Combo(new[] { "Auto (detects static vs. wild)", "Soft reset (static / legendary)", "Wild encounters (walk + run)" }, null, 330);
        var axis = Skin.Combo(new[] { "Left / Right", "Up / Down" }, null, 150);
        card.Body.Children.Add(Skin.Row(Skin.T("Mode", 13, Skin.Text, wrap: false), mode));
        card.Body.Children.Add(Skin.Row(Skin.T("Walk", 13, Skin.Text, wrap: false), axis));
        var log = Skin.LogBox(150);
        void Say(string s) => Dialogs.Ui(() => Skin.Append(log, $"[{DateTime.Now:HH:mm:ss}] {s}"));
        var start = new FancyButton("Start hunting", Skin.GlyphPlay, Skin.Accent);
        var stop = new FancyButton("Stop", Skin.GlyphStop, Skin.StopRed) { IsEnabled = false };
        var test = new FancyButton("Test connection", Skin.GlyphWifi, Skin.PanelHi);

        test.Click += async (_, _) =>
        {
            var ip = await IpPrompt.GetChecked3DS(this);
            if (ip == null) return;
            Say($"Testing {ip}...");
            await Task.Run(() =>
            {
                var bot = new Bot3DS(new Bot3DSOptions { Host = ip });
                bot.Log += Say;
                try
                {
                    var g = bot.Connect(profile.ShinyForgeId);
                    var pk = bot.ReadWild();
                    Say(pk == null ? "Connected. No wild Pokémon in memory right now." : "Last/current wild Pokémon:\n" + Bot3DS.Format(pk));
                    if (g.ShinyForgeId != profile.ShinyForgeId) Say($"Note: the 3DS is running {g.Name}, not {_game.Title}.");
                }
                catch (Exception ex) { Say("Connection test failed: " + ex.Message); }
                finally { bot.Close(); }
            });
        };

        async Task StartHunt()
        {
            var ip = await IpPrompt.GetChecked3DS(this);
            if (ip == null) return;
            var options = new Bot3DSOptions
            {
                Host = ip,
                Mode = mode.SelectedIndex switch { 1 => Hunt3DSMode.StaticSoftReset, 2 => Hunt3DSMode.WildWalking, _ => Hunt3DSMode.Auto },
                WalkHorizontal = axis.SelectedIndex == 0,
                StopRule = StopRuleFor3DS(_game),
            };
            _bot3ds = new Bot3DS(options);
            _bot3ds.Log += s => { _last3dsActivity = DateTime.Now; Say(s); };
            _bot3ds.Warning += w => HealthMonitor.Report($"3DS hunt ({_game.Title})", HealthState.Warning, w);
            _bot3ds.Encounter += (pk, isStop) =>
            {
                if (!isStop) return;
                var name = SpeciesName.GetSpeciesName(pk.Species, 2);
                OnFound(name, pk.IsShiny);
                if (AppSettings.Current.NotifyShiny)
                    NotifyService.Send((pk.IsShiny ? "✨ Shiny " : "Found ") + name + " on the 3DS!",
                        $"{_game.Title}: {name} Lv.{pk.CurrentLevel}, {(Nature)pk.Nature}, IVs {pk.IV_HP + pk.IV_ATK + pk.IV_DEF + pk.IV_SPA + pk.IV_SPD + pk.IV_SPE}/186. " +
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
            start.IsEnabled = false; stop.IsEnabled = true;
            Interlocked.Increment(ref _3dsHunts);
            var bot = _bot3ds;
            _ = Task.Run(() =>
            {
                Exception? failure = null;
                try { bot.Connect(profile.ShinyForgeId); bot.Run(token); }
                catch (Exception ex) { failure = ex; Say("Stopped: " + ex.Message); }
                finally
                {
                    bot.Close();
                    _hunt3ds = null;
                    Interlocked.Decrement(ref _3dsHunts);
                    WebServer.RemoteAction = null;
                    WebServer.RemoteStatus = null;
                    Dialogs.Ui(() => { start.IsEnabled = true; stop.IsEnabled = false; _clips.Stop(); });
                }
                // Lost the 3DS and it no longer answers at that IP: ask for its new address and restart.
                if (failure is IOException or System.Net.Sockets.SocketException or TimeoutException ||
                    failure?.InnerException is IOException or System.Net.Sockets.SocketException)
                {
                    HealthMonitor.Report($"3DS hunt ({_game.Title})", HealthState.Error, "Lost the connection to the 3DS: " + failure!.Message);
                    if (!HealthMonitor.CanReach(AppSettings.Current.ThreeDSIP, 4000, 2500))
                        Dialogs.Ui(async () =>
                        {
                            if (await IpPrompt.Ask(this, $"Lost the connection to the 3DS at {AppSettings.Current.ThreeDSIP}. Its IP address may have changed.") != null)
                                await StartHunt();
                        });
                }
            });
        }
        start.Click += async (_, _) => await StartHunt();
        stop.Click += (_, _) => { _hunt3ds?.Cancel(); Say("Stopping..."); };
        card.Body.Children.Add(Skin.Wrap(start, stop, test));
        var rules = new FancyButton("Stop rules", Skin.GlyphSettings, Skin.PanelHi);
        rules.Click += async (_, _) => await new HuntOptionsWindow(_game, rngAvailable: false).ShowDialog(Dialogs.Owner(this)!);
        var stats = new FancyButton("Statistics", Skin.GlyphInfo, Skin.PanelHi);
        stats.Click += (_, _) => new HuntStatsWindow(_game.Title).Show(Dialogs.Owner(this)!);
        card.Body.Children.Add(Skin.Wrap(rules, stats));
        card.Body.Children.Add(log);
        card.Body.Children.Add(Info($"Encounters are logged to {Path.Combine(AppPaths.DataDir, "3ds_encounters.csv")}"));
    }

    /// <summary>The stop rules from Hunt options, as a check for the 3DS bot (null = shinies only).</summary>
    private static Func<PKM, string?>? StopRuleFor3DS(Game game)
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
            var nature = ((Nature)pk.Nature).ToString();
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
        if (_game.Bot != BotKind.FrlgSwitch) return;
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
            Dialogs.Ui(() => _huntStatus.Text = (running ? (HuntService.IsPaused ? "⏸ Python hunter is PAUSED" : "● Python hunter is running") : "") +
                                               (extras.Count > 0 ? $"{(running ? "  •  " : "")}{string.Join(", ", extras)}" : ""));
            if (!running && _clips.Running && _hunt3ds == null) Dialogs.Ui(() => _clips.Stop());
        });
    }

    /// <summary>Starts the rolling clip buffer from this game's capture source (Record card) - unless already recording.</summary>
    private void StartClipBuffer()
    {
        if (!AppSettings.Current.ShinyClips || _recorder.IsRecording || _clips.Running) return;
        var source = Sel(_source) ?? _gs.VideoSource;
        if (string.IsNullOrWhiteSpace(source) || source.StartsWith("(")) return;
        _clips.Start(CaptureSpec(source), _game.Id,
            Directory.Exists(_gs.RecordingFolder) ? Path.Combine(_gs.RecordingFolder, "Shiny Clips") : ShinyClipService.DefaultFolder);
        if (_clips.Running) Status("Shiny clip buffer running - the last " + AppSettings.Current.ShinyClipSeconds + " s are saved when a shiny appears.");
    }

    /// <summary>Something was found: save the clip (or point at the running recording).</summary>
    private void OnFound(string species, bool shiny)
    {
        Dialogs.Ui(() => Status($"{(shiny ? "★ Shiny " : "Found ")}{species}! {(_recorder.IsRecording ? "It's in the current recording." : "")}"));
        if (_clips.Running)
            _ = Task.Run(async () =>
            {
                var clip = await _clips.SaveClipAsync((shiny ? "Shiny_" : "Found_") + species); // a non-shiny stop target isn't called "Shiny"
                if (clip == null) return;
                Dialogs.Ui(() => Status("Clip saved: " + clip));
                if (AppSettings.Current.NotifyShiny) NotifyService.Send("Clip saved", $"{species}: {Path.GetFileName(clip)}");
            });
    }

    // ================================================================ recording
    private Card BuildRecordCard()
    {
        var card = new Card(Skin.GlyphRecord, "Record Gameplay", Skin.Bad);
        card.Body.Children.Add(Info("Records continuously, split into numbered files with no gap between them."));
        _recStatus = Info("Looking for capture devices...");
        _recFolder = Skin.Input(_gs.RecordingFolder, 330);
        _segments = Skin.Combo(new[] { "5", "10", "15", "20", "30" }, _gs.SegmentMinutes.ToString(), 100);
        var sourceRow = Skin.Row(Skin.T("Video", 13, Skin.Text, wrap: false));
        var audioRow = Skin.Row(Skin.T("Audio", 13, Skin.Text, wrap: false));
        card.Body.Children.Add(sourceRow);
        card.Body.Children.Add(audioRow);
        var frames = ConsoleFrames.For(_game.Platform).ToList();
        // automatically the game's console (Switch games: Switch 1 or Switch 2 from the capture card)
        string FrameName(ConsoleFrame f) => f.Label + " frame" + (ConsoleFrames.Exists(f) ? "" : " (picture missing)");
        _overlayKeys = new List<string> { ConsoleFrames.Auto };
        _overlayKeys.AddRange(frames.Select(f => f.Key));
        _overlayKeys.Add(ConsoleFrames.None);
        var overlayNames = new List<string> { _game.IsSwitch ? "Automatic: Switch 1 or Switch 2 frame (from the capture card)"
            : "Automatic: " + (ConsoleFrames.Pick(ConsoleFrames.Auto, _game.Platform, "") is { } auto ? FrameName(auto) : "no frame") };
        overlayNames.AddRange(frames.Select(FrameName));
        overlayNames.Add("No frame");
        _overlay = Skin.Combo(overlayNames, overlayNames[Math.Max(0, _overlayKeys.IndexOf(_gs.Overlay))], 420);
        card.Body.Children.Add(Skin.Row(Skin.T("Frame", 13, Skin.Text, wrap: false), _overlay));
        _crop = new CheckBox { Content = "Cut the black border around DS / Game Boy pictures", IsChecked = _gs.CropToGame };
        if (!_game.IsSwitch && _game.Platform != Platform.N3DS) card.Body.Children.Add(_crop);
        card.Body.Children.Add(Skin.Row(Skin.T("Segment length (minutes)", 13, Skin.Text, wrap: false), _segments));
        var browse = new FancyButton("Browse", Skin.GlyphFolder, Skin.PanelHi);
        browse.Click += async (_, _) => { if (await Dialogs.PickFolder(this, "Where should recordings be saved?", _recFolder.Text) is { } f) _recFolder.Text = f; };
        card.Body.Children.Add(Skin.Row(Skin.T("Folder", 13, Skin.Text, wrap: false), _recFolder, browse));
        _recButton = new FancyButton("Start recording", Skin.GlyphRecord, Color.FromRgb(190, 30, 50));
        _recButton.Click += async (_, _) => await ToggleRecording();
        if (_game.IsSwitch && AppSettings.Current.SwitchUsb)
        {
            // USB needs the Switch out of the dock: the capture card gets no picture
            _recButton.IsEnabled = false;
            card.Body.Children.Add(Info("Recording is off while sys-botbase uses USB (the Switch is out of the dock, so the capture card has no picture). " +
                                        "Shiny Hunt card → sys-botbase → Use WiFi.", Skin.Warn));
        }
        var open = new FancyButton("Open folder", Skin.GlyphFolder, Skin.PanelHi);
        open.Click += (_, _) => { if (Directory.Exists(_recFolder.Text)) OsInfo.OpenFolder(_recFolder.Text!); };
        var buttons = Skin.Wrap(_recButton, open);
        if (!_game.IsSwitch && OsInfo.IsWindows)
        {
            var launch = new FancyButton("Open 3DS Capture", Skin.GlyphUsb, Skin.PanelHi);
            launch.Click += async (_, _) => await Launch3DSCapture();
            buttons.Children.Add(launch);
        }
        card.Body.Children.Add(buttons);
        card.Body.Children.Add(_recStatus);

        // Capture devices are listed by ffmpeg in the background (it can take a moment).
        _ = Task.Run(RecordingService.ListDevices).ContinueWith(t => Dialogs.Ui(() =>
        {
            var (video, audio) = t.IsFaulted ? (new List<string>(), new List<string>()) : t.Result;
            var sources = new List<string>();
            if (OsInfo.IsWindows && !_game.IsSwitch) sources.Add(RecordingService.HasTwoScreens(_game.Platform) ? RecordingService.Both3DS : RecordingService.Top3DS);
            sources.AddRange(OsInfo.IsWindows ? video.Select(v => RecordingService.DevicePrefix + v) : video);
            if (OsInfo.IsWindows && _game.IsSwitch) sources.Add(RecordingService.StreamlabsWindow);
            if (sources.Count == 0) sources.Add("(no capture device found - install FFmpeg and plug in the capture card)");
            var def = !string.IsNullOrEmpty(_gs.VideoSource) && sources.Contains(_gs.VideoSource) ? _gs.VideoSource
                : sources.FirstOrDefault(s => s.Contains("Capture", StringComparison.OrdinalIgnoreCase) && !s.Contains("screen", StringComparison.OrdinalIgnoreCase)) ?? sources[0];
            _source = Skin.Combo(sources, def, 420);
            var audios = new List<string> { RecordingService.NoAudio };
            if (OsInfo.IsWindows && !_game.IsSwitch) audios.Add(RecordingService.AppAudio3DS);
            audios.AddRange(audio);
            var defAudio = !string.IsNullOrEmpty(_gs.AudioDevice) && audios.Contains(_gs.AudioDevice) ? _gs.AudioDevice : RecordingService.DefaultAudio(def, audios);
            _audio = Skin.Combo(audios, defAudio, 420);
            _source.SelectionChanged += (_, _) => _audio.SelectedItem = RecordingService.DefaultAudio(Sel(_source) ?? "", audios);
            sourceRow.Children.Add(_source);
            audioRow.Children.Add(_audio);
            _recStatus.Text = "Not recording.";
        }));
        return card;
    }

    private ComboBox? _overlay;
    private CheckBox _crop = null!;
    private List<string> _overlayKeys = new();

    /// <summary>What the Record card is set to: source, sound, console frame.</summary>
    private RecordingService.CaptureSpec CaptureSpec(string source)
    {
        var overlay = _overlay == null ? _gs.Overlay : _overlayKeys[Math.Max(0, _overlay.SelectedIndex)];
        return new RecordingService.CaptureSpec(source, Sel(_audio) ?? RecordingService.NoAudio, _game.Platform, overlay, _crop?.IsChecked ?? _gs.CropToGame);
    }

    /// <summary>
    /// Is the hardware for <paramref name="what"/> there? If not: what's missing, then Check again / Open anyway (detection
    /// can't know every device) / Cancel. True = go ahead.
    /// </summary>
    private async Task<bool> EquipmentReady(string what, Func<bool> present, string missing)
    {
        while (!present())
        {
            var r = await Dialogs.Show(this, missing, what, "Check again", "Open anyway", Dialogs.Cancel);
            if (r == "Open anyway") return true;
            if (r != "Check again") return false;
        }
        return true;
    }

    /// <summary>Pokémon Automation needs a capture card and a microcontroller (or sys-botbase on a modded Switch).</summary>
    private async Task<bool> PaEquipmentReady()
    {
        var s = AppSettings.Current;
        return await EquipmentReady("Pokémon Automation", () => Equipment.VideoSources().Count > 0,
                   "No capture card was found. Pokémon Automation watches the game through a capture card - connect it (and the Switch dock).")
               && await EquipmentReady("Pokémon Automation", () => Equipment.Microcontroller() != false || HealthMonitor.SwitchReachable(1500),
                   "No controller for Pokémon Automation was found: plug in its microcontroller (ESP32-S3, Raspberry Pi Pico W... with their firmware), " +
                   $"or turn on the modded Switch so sys-botbase answers at {s.SwitchIP}.");
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
        if (!await Dialogs.YesNo(this, $"The Python hunter needs these Python packages: {string.Join(", ", missing)}.\n\nInstall them now (pip)?", "Python hunter")) return false;
        Status("Installing Python packages...");
        var result = await DependencyService.InstallPythonPackages(python, botDir);
        missing = await DependencyService.MissingPythonPackages(python);
        Status(missing is { Count: 0 } ? "Python packages installed." : result);
        if (missing is { Count: > 0 })
        {
            await Dialogs.Info(this, $"These packages still aren't installed: {string.Join(", ", missing)}.\n\npip said: {result}", "Python hunter");
            return false;
        }
        return true;
    }

    private async Task Launch3DSCapture()
    {
        if (!await EnsureDependency("3DS Capture")) return;
        var cap = DependencyService.PathOf("3DS Capture");
        if (cap != null && System.Diagnostics.Process.GetProcessesByName("3ds_capture").Length == 0)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cap) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(cap)! });
    }

    private async Task ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
            _recButton.Text = "Start recording";
            return;
        }
        if (!await EnsureDependency("FFmpeg")) return;
        if (_source == null || (Sel(_source) ?? "").StartsWith("(")) { await Dialogs.Info(this, "No capture device was found. Install FFmpeg, plug in the capture card, then open this game again.", "Recording"); return; }
        if (string.IsNullOrWhiteSpace(_recFolder.Text) || !Directory.Exists(_recFolder.Text))
        {
            if (await Dialogs.PickFolder(this, "Choose the folder for the recordings (files are numbered automatically)", _recFolder.Text) is not { } f) return;
            _recFolder.Text = f;
        }
        // Recording is the user's own: it never checks, opens or presses anything on the console (hunts do that).
        var source = Sel(_source) ?? "";
        if (source.Contains("3DS Capture")) { await Launch3DSCapture(); await Task.Delay(1500); }
        else if (source.StartsWith(RecordingService.DevicePrefix))
        {
            var device = source[RecordingService.DevicePrefix.Length..];
            if (!await EquipmentReady("Record Gameplay", () => Equipment.VideoSources().Contains(device),
                    $"The capture device \"{device}\" isn't connected. Plug in the capture card (and the console), or choose another video source.")) return;
        }
        _gs.RecordingFolder = _recFolder.Text!;
        _gs.SegmentMinutes = int.Parse(Sel(_segments) ?? "10");
        _gs.VideoSource = source;
        _gs.AudioDevice = Sel(_audio) ?? "";
        var spec = CaptureSpec(source);
        _gs.Overlay = spec.Overlay;
        _gs.CropToGame = spec.CropToGame;
        AppSettings.Current.Save();
        try
        {
            _clips.Stop(); // one capture device can't be opened twice
            _recorder.Start(spec, _gs.RecordingFolder, _gs.SegmentMinutes);
            _recButton.Text = "Stop recording";
        }
        catch (Exception ex) { await Dialogs.Info(this, ex.Message, "Recording"); }
    }

    // ================================================================ collection tracker
    private Card BuildTrackerCard()
    {
        var card = new Card(Skin.GlyphSheet, "Collection Tracker → Pokédex spreadsheet", Skin.Good);
        var wb = SpreadsheetService.FindWorkbook(_game);
        _sheetLabel = Info(SpreadsheetService.Describe(_game, wb), wb != null ? Skin.Text : Skin.Warn);
        card.Body.Children.Add(_sheetLabel);
        var change = new FancyButton("Choose spreadsheet", Skin.GlyphSheet, Skin.PanelHi);
        change.Click += async (_, _) =>
        {
            var cur = SpreadsheetService.FindWorkbook(_game);
            var start = cur != null ? Path.GetDirectoryName(cur) : Directory.Exists(AppSettings.Current.SpreadsheetRoot) ? AppSettings.Current.SpreadsheetRoot : SpreadsheetService.BundledRoot;
            if (await Dialogs.PickFile(this, "Choose the Pokédex tracker", start, ("Excel workbooks", new[] { "*.xlsm", "*.xlsx" })) is not { } file) return;
            _gs.SpreadsheetPath = file;
            AppSettings.Current.Save();
            RefreshSheetLabel();
        };
        var auto = new FancyButton("Auto-recognize", Skin.GlyphSync, Skin.PanelHi);
        auto.Click += (_, _) => { _gs.SpreadsheetPath = ""; AppSettings.Current.Save(); RefreshSheetLabel(); };
        var open = new FancyButton("Open", Skin.GlyphSheet, Skin.PanelHi);
        open.Click += (_, _) => { if (SpreadsheetService.FindWorkbook(_game) is { } cur) OsInfo.Open(cur); };
        card.Body.Children.Add(Skin.Wrap(change, auto, open));
        var dex = new FancyButton("Pokédex (pop-out)", Skin.GlyphSparkle, Skin.Accent2);
        dex.Click += (_, _) => OpenDex();
        _downloadSheet = new FancyButton("Download tracker", Skin.GlyphDownload, Skin.Accent) { IsVisible = wb == null && !string.IsNullOrWhiteSpace(_game.Spreadsheet) };
        _downloadSheet.Click += async (_, _) => await DownloadTracker();
        card.Body.Children.Add(Skin.Wrap(dex, _downloadSheet));

        var sources = new List<string> { "Save file" };
        if (_game.Bot == BotKind.FrlgSwitch) sources.Insert(0, "Live from the Switch (party + PC boxes)");
        _trackSource = Skin.Combo(sources, null, 360);
        _trackLive = (Sel(_trackSource) ?? "").StartsWith("Live");
        _trackSource.SelectionChanged += (_, _) => _trackLive = (Sel(_trackSource) ?? "").StartsWith("Live");
        card.Body.Children.Add(Skin.Row(Skin.T("Read from", 13, Skin.Text, wrap: false), _trackSource));

        _trackSaveLabel = Info("Save file: searching...");
        var pickSave = new FancyButton("Choose save file...", Skin.GlyphSave, Skin.PanelHi);
        pickSave.Click += async (_, _) => await ChooseTrackerSave();
        var autoSave = new FancyButton("Use auto-detected", Skin.GlyphSync, Skin.PanelHi);
        autoSave.Click += (_, _) => { _gs.SaveFilePath = ""; AppSettings.Current.Save(); UpdateTrackerSaveLabel(); };
        card.Body.Children.Add(_trackSaveLabel);
        card.Body.Children.Add(Skin.Wrap(pickSave, autoSave));

        var sync = new FancyButton("Sync now", Skin.GlyphSync, Skin.Accent) { IsEnabled = SpreadsheetService.CanUpdate };
        sync.Click += async (_, _) => await SyncTracker(manual: true);
        _autoMonitor = Skin.Check("Monitor automatically", false);
        _autoMonitor.IsEnabled = SpreadsheetService.CanUpdate;
        _autoMonitor.IsCheckedChanged += (_, _) => SetMonitoring(_autoMonitor.IsChecked == true);
        card.Body.Children.Add(Skin.Wrap(sync, _autoMonitor));
        card.Body.Children.Add(Info(SpreadsheetService.CanUpdate
            ? "Marks 'In Box', 'OT' (caught by you) and 'Shiny' as Y for every Pokémon in your party and boxes. Only N → Y changes are made; your own marks are never removed."
            : $"Updating the tracker uses Microsoft Excel on Windows (it keeps the trackers' macros and pictures intact), so Sync isn't available on {OsInfo.Name}. " +
              "Open the tracker with Open, and see what you own in the Pokédex window."));
        _trackLog = Skin.LogBox(110);
        card.Body.Children.Add(_trackLog);
        Dispatcher.UIThread.Post(UpdateTrackerSaveLabel);
        return card;
    }

    private void Log(string s) => Dialogs.Ui(() => Skin.Append(_trackLog, $"[{DateTime.Now:HH:mm:ss}] {s}"));

    private void RefreshSheetLabel()
    {
        var wb = SpreadsheetService.FindWorkbook(_game);
        _sheetLabel.Text = SpreadsheetService.Describe(_game, wb);
        _sheetLabel.Foreground = Skin.B(wb != null ? Skin.Text : Skin.Warn);
        _downloadSheet.IsVisible = wb == null && !string.IsNullOrWhiteSpace(_game.Spreadsheet);
    }

    /// <summary>Downloads this game's Pokédex tracker from ShinyForge's GitHub page.</summary>
    private async Task DownloadTracker()
    {
        _downloadSheet.IsEnabled = false;
        try
        {
            _sheetLabel.Foreground = Skin.B(Skin.Muted);
            _sheetLabel.Text = "Looking up the tracker on ShinyForge's GitHub page...";
            var entry = SheetsDownloadService.EntryFor(_game, await SheetsDownloadService.ManifestAsync());
            if (entry == null) { _sheetLabel.Text = $"No published tracker for Pokémon {_game.Title} was found."; return; }
            if (!await Dialogs.YesNo(this, $"Download the Pokémon {_game.Title} tracker ({entry.Size / 1048576.0:N0} MB) into\n{SpreadsheetService.BundledRoot}?", "Pokédex tracker")) return;
            var path = await SheetsDownloadService.DownloadAsync(entry, s => Dialogs.Ui(() => _sheetLabel.Text = s));
            Status("Tracker downloaded: " + path);
        }
        catch (Exception ex) { await Dialogs.Info(this, "Couldn't download the tracker: " + ex.Message, "Pokédex tracker"); }
        finally { _downloadSheet.IsEnabled = true; RefreshSheetLabel(); }
    }

    private void OpenDex()
    {
        if (_dex is { IsVisible: true }) { _dex.Activate(); return; }
        _dex = new DexWindow(_game, LoadCollectionForDex);
        _dex.Show();
    }

    /// <summary>What you own for the Pokédex window: the live Switch game if that source is selected, else the tracker's save file.</summary>
    private Collection? LoadCollectionForDex(Action<string> progress)
    {
        if (_trackLive)
        {
            if (!HealthMonitor.SwitchReachable(2500))
                throw new InvalidOperationException($"Can't reach the Switch at {AppSettings.Current.SwitchIP} - check its IP (Settings, or start a hunt).");
            progress("Reading party and PC boxes from the Switch...");
            var s = AppSettings.Current;
            try
            {
                using var lf = new LiveFrlg(s.SwitchIP, s.SwitchPort);
                return lf.ReadCollection(_game);
            }
            catch (Exception e) { return SaveInstead(e, progress); }
        }
        progress("Reading the save file...");
        var save = ResolveTrackerSave();
        return save == null ? null : Collection.FromSave(save.Save, save.Path);
    }

    /// <summary>
    /// The live read failed (title screen, Switch busy...): use the save file instead of showing an empty Pokédex, and say so.
    /// No save file either → the live error is shown.
    /// </summary>
    private Collection? SaveInstead(Exception live, Action<string> progress)
    {
        progress("The Switch game couldn't be read - using the save file...");
        var save = ResolveTrackerSave();
        if (save == null) throw live;
        var c = Collection.FromSave(save.Save, save.Path);
        c.Source = $"the save file (the Switch couldn't be read: {live.Message.TrimEnd('.')})";
        return c;
    }

    private FoundSave? ResolveTrackerSave()
    {
        if (!string.IsNullOrWhiteSpace(_gs.SaveFilePath) && File.Exists(_gs.SaveFilePath) && SaveUtil.TryGetSaveFile(_gs.SaveFilePath, out var chosen) && chosen != null)
            return new FoundSave(_gs.SaveFilePath, chosen);
        return _save ?? SaveService.FindSaves(_gs.GameFolder, _game).FirstOrDefault();
    }

    private void UpdateTrackerSaveLabel()
    {
        Task.Run(ResolveTrackerSave).ContinueWith(t => Dialogs.Ui(() =>
        {
            var s = t.IsFaulted ? null : t.Result;
            var picked = !string.IsNullOrWhiteSpace(_gs.SaveFilePath) && s?.Path == _gs.SaveFilePath;
            _trackSaveLabel.Foreground = Skin.B(s != null ? Skin.Good : Skin.Warn);
            _trackSaveLabel.Text = s == null
                ? "Save file: none found - choose one, use Find in Checkpoint, or set the game folder in the Pokémon HOME card."
                : $"Save file ({(picked ? "chosen by you" : "auto-detected")}): {Path.GetFileName(s.Path)} - {s.Save.OT}, {s.Save.Version}, {s.Save.PlayedHours}h {s.Save.PlayedMinutes:00}m\n    {Path.GetDirectoryName(s.Path)}";
        }));
    }

    private async Task ChooseTrackerSave()
    {
        if (await Dialogs.PickFile(this, $"Choose a Pokémon {_game.Title} save file", Directory.Exists(_gs.GameFolder) ? _gs.GameFolder : null,
                ("Save files", new[] { "*.sav", "*.dsv", "*.sa1", "*.srm", "*.bin", "main" })) is not { } file) return;
        if (!SaveUtil.TryGetSaveFile(file, out var sav) || sav == null) { await Dialogs.Info(this, "That file isn't a Pokémon save file PKHeX can read.", "Choose save file"); return; }
        if (!_game.Versions.Contains(sav.Version) && !await Dialogs.YesNo(this, $"This save is from Pokémon {sav.Version}, not {_game.Title}. Use it anyway?", "Choose save file")) return;
        _gs.SaveFilePath = file;
        if (string.IsNullOrWhiteSpace(_gs.GameFolder)) { _gs.GameFolder = Path.GetDirectoryName(file)!; _folderBox.Text = _gs.GameFolder; }
        AppSettings.Current.Save();
        UpdateTrackerSaveLabel();
    }

    private void SetMonitoring(bool on)
    {
        _gs.AutoMonitor = on;
        AppSettings.Current.Save();
        _watcher?.Dispose();
        _watcher = null;
        _liveTimer?.Stop();
        _liveTimer = null;
        if (!on) { Log("Automatic monitoring off."); return; }
        if (_trackLive)
        {
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _liveTimer.Tick += async (_, _) => await SyncTracker(manual: false);
            _liveTimer.Start();
            Log("Monitoring the live game every 5 minutes.");
            return;
        }
        if (!Directory.Exists(_gs.GameFolder)) { Log("Set the game folder (Pokémon HOME card) first, then turn monitoring on."); _autoMonitor.IsChecked = false; return; }
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
        Dialogs.Ui(async () => { await Task.Delay(5000); await SyncTracker(manual: false); });
    }

    private async Task SyncTracker(bool manual)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var wb = SpreadsheetService.FindWorkbook(_game);
            if (wb == null) { Log("No spreadsheet for this game - use 'Choose spreadsheet' or 'Download tracker'."); return; }
            if (!await EnsureDependency("Microsoft Excel")) return;
            Collection owned;
            if (_trackLive)
            {
                if (manual ? !await IpPrompt.EnsureSwitch(this) : !HealthMonitor.SwitchReachable(2500))
                {
                    Log($"Can't reach the Switch at {AppSettings.Current.SwitchIP}.");
                    _trackError = $"can't reach the Switch at {AppSettings.Current.SwitchIP}";
                    return;
                }
                Log("Reading party and PC boxes from the Switch...");
                var s = AppSettings.Current;
                owned = await Task.Run(() => { using var live = new LiveFrlg(s.SwitchIP, s.SwitchPort); return live.ReadCollection(_game); });
            }
            else
            {
                var save = await Task.Run(ResolveTrackerSave);
                if (save == null) { Log("No save file found - use 'Choose save file...' or set the game folder in the Pokémon HOME card."); return; }
                Log($"Reading {save.Path}");
                owned = Collection.FromSave(save.Save, save.Path);
            }
            Log($"{owned.Total} Pokémon, {owned.BySpecies.Count} species ({owned.BySpecies.Values.Count(e => e.Shiny)} shiny) - {owned.Source}");
            await Task.Run(() =>
            {
                if (BackupService.Backup(wb, spreadsheet: true) is { } b) Log("Backup made: " + Path.GetFileName(b));
                if (File.Exists(owned.Source)) BackupService.Backup(owned.Source, spreadsheet: false);
            });
            var result = await Task.Run(() => SpreadsheetService.Update(wb, owned, Log));
            Log($"Done: {result.MarkedInBox} new 'In Box', {result.MarkedOT} new 'OT', {result.MarkedShiny} new 'Shiny' across {result.Sheets} sheet(s).");
            _trackError = null;
            Status($"Tracker updated: {Path.GetFileName(wb)}");
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            _trackError = ex.Message;
            if (manual) await Dialogs.Info(this, ex.Message, "Collection Tracker");
        }
        finally { _syncing = false; }
    }

    // ================================================================ save export / game info
    private Card BuildSaveCard()
    {
        var card = new Card(Skin.GlyphSave, "Pokémon HOME folder + Game Info", Skin.Accent2);
        card.Body.Children.Add(Info("Point ShinyForge at the folder where this game's files / exported saves are stored."));
        _folderBox = Skin.Input(_gs.GameFolder, 380);
        var browse = new FancyButton("Browse", Skin.GlyphFolder, Skin.PanelHi);
        browse.Click += async (_, _) =>
        {
            if (await Dialogs.PickFolder(this, "Folder with this game's files / exported save", _folderBox.Text) is not { } f) return;
            _folderBox.Text = f;
            _gs.GameFolder = f;
            AppSettings.Current.Save();
            ScanSaves();
        };
        card.Body.Children.Add(Skin.Row(_folderBox, browse));
        var scan = new FancyButton("Find save", Skin.GlyphSync, Skin.PanelHi);
        scan.Click += (_, _) => ScanSaves();
        var export = new FancyButton("Export Pokémon + Game Info", Skin.GlyphDownload, Skin.Accent);
        export.Click += async (_, _) => await ExportSave();
        card.Body.Children.Add(Skin.Wrap(scan, export));
        var info = new FancyButton("View Game Info", Skin.GlyphInfo, Skin.PanelHi);
        info.Click += async (_, _) =>
        {
            var p = Path.Combine(_gs.GameFolder, "game info.txt");
            if (File.Exists(p)) OsInfo.Open(p);
            else await Dialogs.Info(this, "Export first - the Game Info document is created together with the Pokémon HOME folder.", "Game Info");
        };
        var row = Skin.Wrap(info);
        if (OsInfo.IsWindows)
        {
            var pkhex = new FancyButton("Open in PKHeX", Skin.GlyphTools, Skin.PanelHi);
            pkhex.Click += async (_, _) => await OpenInPKHeX();
            row.Children.Insert(0, pkhex);
        }
        card.Body.Children.Add(row);
        var checkpoint = new FancyButton("Find in Checkpoint", Skin.GlyphSave, Skin.PanelHi);
        checkpoint.Click += async (_, _) => await FindInCheckpoint();
        // Checkpoint backs up 3DS / DS / Virtual Console / Switch saves - not GBA games (a 3DS can't run them in a way it sees)
        checkpoint.IsVisible = _game.Platform != Platform.GBA;
        var buttons = Skin.Wrap(checkpoint);
        if (_game.IsSwitch)
        {
            var trades = new FancyButton("Wireless trades (pokeldn)", Skin.GlyphWifi, Skin.PanelHi);
            trades.Click += async (_, _) => await new PokeldnWindow().ShowDialog(Dialogs.Owner(this)!);
            buttons.Children.Add(trades);
        }
        card.Body.Children.Add(buttons);
        _saveStatus = Info("", Skin.Text);
        card.Body.Children.Add(_saveStatus);
        if (Directory.Exists(_gs.GameFolder)) Dispatcher.UIThread.Post(ScanSaves);
        return card;
    }

    // ---------------------------------------------------------------- Checkpoint (SD card) saves
    private static readonly HashSet<string> CheckpointWarned = new();

    /// <summary>Finds this game's Checkpoint backups on the SD card, warns first, offers a fresh backup on a rescan, lets the user pick, copies to the computer.</summary>
    private bool _switchFtpStarted; // ShinyForge opened the FTP app on the Switch during this Checkpoint step

    private async Task FindInCheckpoint()
    {
        try { await FindInCheckpointSteps(); }
        finally
        {
            // whatever happened after it (picked, cancelled, failed): offer to close the FTP app and go back to the game
            if (_switchFtpStarted) { _switchFtpStarted = false; await OfferBackToGame(); }
        }
    }

    private async Task FindInCheckpointSteps()
    {
        var rescan = !string.IsNullOrEmpty(_gs.CheckpointBackup);
        if (!CheckpointWarned.Contains(_game.Id))
        {
            if (!await Dialogs.YesNo(this,
                    "Before ShinyForge uses your Checkpoint saves:\n\n" +
                    "  • Save your game in-game first.\n" +
                    "  • Back up the save YOURSELF with Checkpoint on your console. ShinyForge only reads those backups - it can't make them, and it never changes anything on your SD card.\n" +
                    "  • Keep your own copies of your saves somewhere safe.\n\n" +
                    $"ShinyForge copies the backup you pick to\n{CheckpointService.GameFolder(_game)}\nand uses that copy for the Pokémon HOME folder, Game Info and the Pokédex tracker.\n\n" +
                    "Are you ready to continue?", "ShinyForge - Checkpoint saves"))
                return;
            CheckpointWarned.Add(_game.Id);
        }
        var console = _game.IsSwitch ? "Switch" : "3DS";
        var source = await Dialogs.YesNoCancel(this,
            "Where are the Checkpoint backups?\n\n" +
            $"Yes = on the SD card INSIDE the {console} - read over WiFi (Checkpoint's FTP server, or the ftpd app)\n" +
            "No = an SD card put into this computer (or a copy of it)", "ShinyForge - Checkpoint saves");
        if (source == Dialogs.Cancel) return;
        var wifi = source == Dialogs.Yes;
        if (rescan)
        {
            var r = await Dialogs.YesNoCancel(this,
                "Would you like to back up a fresh save before ShinyForge rescans?\n\n" +
                "Yes = I'll make a new Checkpoint backup now (the steps are shown next)\nNo = rescan the backups that are already on the SD card", "ShinyForge - fresh backup?");
            if (r == Dialogs.Cancel) return;
            if (r == Dialogs.Yes && await Dialogs.Show(this,
                    $"1. Save your game in Pokémon {_game.Title}, then close the game.\n2. Open Checkpoint on the console and select the game.\n" +
                    "3. Choose Backup and confirm (the suggested date-and-time name is fine).\n" +
                    (wifi ? "4. Leave Checkpoint open with its FTP server on (Checkpoint's settings).\n\nClick OK when the fresh backup is made."
                          : "4. Put the console's SD card into this computer (or reconnect it).\n\nClick OK when the fresh backup is on the SD card and the card is connected."), "ShinyForge - make a fresh backup", Dialogs.Ok, Dialogs.Cancel) != Dialogs.Ok)
                return;
        }
        _saveStatus.Foreground = Skin.B(Skin.Muted);
        List<string> roots;
        var where = _game.IsSwitch ? "switch/Checkpoint/saves" : "3ds/Checkpoint/saves";
        if (wifi)
        {
            var root = await MirrorFromConsole();
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
            if (!await Dialogs.YesNo(this, $"No Checkpoint folder ({where}) was found on any drive.\n\nPut the console's SD card into this computer - or, if you copied it, choose that folder.\n\nChoose a folder now?", "ShinyForge - Checkpoint"))
            { _saveStatus.Text = "No Checkpoint folder found."; return; }
            if (await Dialogs.PickFolder(this, "The SD card (or the Checkpoint / saves folder copied from it)", AppSettings.Current.CheckpointFolder) is not { } f) { _saveStatus.Text = ""; return; }
            AppSettings.Current.CheckpointFolder = f;
            AppSettings.Current.Save();
            roots = await Task.Run(() => CheckpointService.FindRoots(_game.IsSwitch));
            if (roots.Count == 0) { _saveStatus.Foreground = Skin.B(Skin.Warn); _saveStatus.Text = $"That folder doesn't contain {where}."; return; }
        }
        _saveStatus.Text = "Reading the Checkpoint backups of this game...";
        var backups = await Task.Run(() => CheckpointService.FindBackups(_game, roots));
        if (backups.Count == 0)
        {
            var ids = CheckpointService.TitleIds.TryGetValue(_game.Id, out var x) ? $" (title ID {string.Join(" / ", x)})" : "";
            _saveStatus.Foreground = Skin.B(Skin.Warn);
            _saveStatus.Text = $"No Checkpoint backup of Pokémon {_game.Title} was found.";
            await Dialogs.Info(this, $"No Checkpoint backup of Pokémon {_game.Title}{ids} with a readable save was found in:\n\n  {string.Join("\n  ", roots)}\n\nBack the game up with Checkpoint on the console first, then try again.", "ShinyForge - Checkpoint");
            return;
        }
        var chosen = backups[0];
        if (backups.Count > 1)
        {
            var picker = new CheckpointPicker(_game.Title, backups);
            await picker.ShowDialog(Dialogs.Owner(this)!);
            if (picker.Chosen == null) { _saveStatus.Text = ""; return; }
            chosen = picker.Chosen;
        }
        _saveStatus.Text = "Copying the backup to this computer...";
        FoundSave copied;
        try { copied = await Task.Run(() => CheckpointService.CopyToPc(chosen, _game)); }
        catch (Exception ex) { _saveStatus.Foreground = Skin.B(Skin.Bad); _saveStatus.Text = "Couldn't copy the backup: " + ex.Message; return; }
        _gs.CheckpointBackup = chosen.Folder;
        _gs.GameFolder = CheckpointService.GameFolder(_game);
        _gs.SaveFilePath = copied.Path;
        AppSettings.Current.Save();
        _folderBox.Text = _gs.GameFolder;
        _save = copied;
        _saveStatus.Foreground = Skin.B(Skin.Good);
        _saveStatus.Text = $"✔ Checkpoint backup \"{chosen.BackupName}\" ({chosen.Time:yyyy-MM-dd HH:mm}) copied to this computer.\n" +
                           $"    Trainer {copied.Save.OT}  •  TID {copied.Save.DisplayTID}  •  {copied.Save.Version}  •  {copied.Save.PlayedHours}h {copied.Save.PlayedMinutes:00}m";
        UpdateTrackerSaveLabel();
    }

    /// <summary>Reads this game's Checkpoint backups from the SD card inside the console over WiFi (FTP) into the local mirror.
    /// Returns the mirrored saves folder, or null if cancelled / it failed (the status line says why).</summary>
    private async Task<string?> MirrorFromConsole()
    {
        var console = _game.IsSwitch ? "Switch" : "3DS";
        var ip = _game.IsSwitch ? await IpPrompt.GetOrAskSwitch(this) : await IpPrompt.GetOrAsk(this);
        if (ip == null) { _saveStatus.Text = ""; return null; }
        int? port;
        var startedApp = false;
        if (!_game.IsSwitch)
        {
            // The 3DS can't be controlled from here: the user closes the game and opens the FTP app.
            var host3 = ip;
            if (await Task.Run(() => ConsoleSdService.FindPort(host3)) == null &&
                await Dialogs.Show(this,
                    "Before ShinyForge continues, on the 3DS:\n\n" +
                    "  1. Save your game, then close it (HOME → Close).\n" +
                    "  2. Open Checkpoint (with its FTP server turned on in Checkpoint's settings) or the ftpd app.\n" +
                    "  3. Leave it open while ShinyForge reads the saves.\n\n" +
                    "Click OK when the FTP app is running.", "ShinyForge - open the FTP app on the 3DS", Dialogs.Ok, Dialogs.Cancel) != Dialogs.Ok)
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
                var (started, cancelled) = await StartSwitchFtpApp(ip);
                if (cancelled) { _saveStatus.Text = ""; return null; }
                if (started != null) { port = started; _switchFtpStarted = true; break; }
            }
            var r = await Dialogs.YesNoCancel(this, ConsoleSdService.HowToTurnOn(_game.IsSwitch, ip) +
                                                    "\n\nYes = try again    No = enter a different IP address", "ShinyForge - read the console's SD card");
            if (r == Dialogs.Cancel) { _saveStatus.Text = ""; return null; }
            if (r == Dialogs.No)
            {
                ip = _game.IsSwitch ? await IpPrompt.AskSwitch(this) : await IpPrompt.Ask(this);
                if (ip == null) { _saveStatus.Text = ""; return null; }
            }
        }
        try
        {
            var (h, p) = (ip, port.Value);
            var result = await Task.Run(() => ConsoleSdService.Mirror(_game, h, p, msg => Dialogs.Ui(() => _saveStatus.Text = msg)));
            _saveStatus.Text = $"Read {result.TitleFolders} Checkpoint folder(s) of this game from the {console} ({result.Downloaded} file(s) downloaded).";
            return result.Root;
        }
        catch (Exception ex)
        {
            _saveStatus.Foreground = Skin.B(Skin.Bad);
            _saveStatus.Text = $"Couldn't read the {console}'s SD card: {ex.Message}";
            AppPaths.Log("Console SD read failed: " + ex);
            return null;
        }
    }

    /// <summary>Switch only: makes sure no game is open (the user closes it; ShinyForge waits for their OK and checks), then opens
    /// the Homebrew Menu by starting a game while holding R, finds ftpd / Checkpoint there and starts it (sys-botbase).
    /// Returns the FTP port, or null when it couldn't (Cancelled = the user stopped).</summary>
    private async Task<(int? Port, bool Cancelled)> StartSwitchFtpApp(string ip)
    {
        var botPort = AppSettings.Current.SwitchPort;
        if (!HealthMonitor.SwitchReachable(2500)) return (null, false); // no sys-botbase: the user starts the FTP app
        if (HuntService.IsPythonHuntRunning())
        {
            await Dialogs.Info(this, "The FireRed/LeafGreen hunter is running on the Switch. Stop the hunt first - ShinyForge won't press " +
                                     "buttons on the Switch while it's hunting.", "ShinyForge - open the FTP app");
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
            var how = await AskAboutOtherGame(running, "open the FTP app", "ShinyForge - close the game first");
            if (how == null) return (null, true);
            if (how == SwitchGameLauncher.OtherGame.Refuse) continue; // the user closed it - check again
            try { await Task.Run(() => SwitchGameLauncher.CloseGame(ip, botPort, how.Value, msg => Dialogs.Ui(() => _saveStatus.Text = msg))); }
            catch (Exception ex)
            {
                AppPaths.Log("Closing the Switch game failed: " + ex.Message);
                await Dialogs.Info(this, ex.Message, "ShinyForge - close the game first");
                return (null, true);
            }
        }
        if (await Dialogs.Show(this,
                "The game is closed. ShinyForge will now, through sys-botbase:\n\n" +
                "  • start a game while holding R - the Homebrew Menu opens instead of the game,\n" +
                "  • find ftpd (or Checkpoint) in the Homebrew Menu and start it.\n\n" +
                "Don't touch the Switch's controller until it's done (about a minute).",
                "ShinyForge - open the FTP app", Dialogs.Ok, Dialogs.Cancel) != Dialogs.Ok)
            return (null, true);
        try
        {
            var port = await Task.Run(() => ConsoleFtpLauncher.StartFtpApp(ip, botPort, msg => Dialogs.Ui(() => _saveStatus.Text = msg)));
            return (port, false);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Starting the Switch FTP app failed: " + ex);
            await Dialogs.Info(this, "ShinyForge couldn't start the FTP app on the Switch:\n\n" + ex.Message +
                                     "\n\nYou can start it yourself instead (the steps are shown next).", "ShinyForge - open the FTP app");
            return (null, false);
        }
    }

    /// <summary>After the WiFi save read: close the FTP app, open the game and (FireRed / LeafGreen) load the save and start the hunt.</summary>
    private async Task OfferBackToGame()
    {
        var hunt = _game.Bot == BotKind.FrlgSwitch && !HuntService.IsPythonHuntRunning();
        if (!await Dialogs.YesNo(this,
                "ShinyForge is done with the Switch's SD card.\n\n" +
                $"Yes = close the FTP app and open Pokémon {_game.Title}" + (hunt ? ", load your save and start the shiny hunt" : "") + "\n" +
                "No = leave the FTP app open (close it yourself: HOME, X, Close)", "ShinyForge - back to the game"))
            return;
        if (await OpenGameOnSwitch(t => _saveStatus.Text = t) && hunt && await EnsureDependency("Python 3"))
            await StartHuntNow("python");
    }

    /// <summary>Before a FireRed / LeafGreen hunt: is the game open on the Switch with the save loaded? If not (nothing open, the
    /// FTP app / Homebrew Menu still open), offer to open it. False = don't start the hunt.</summary>
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
            var how = await AskAboutOtherGame(running, $"start Pokémon {_game.Title} and the hunt", "Shiny Hunt");
            return how != null && await OpenGameOnSwitch(Status, how.Value);
        }
        var r = await Dialogs.YesNoCancel(this,
            (ftp ? "The FTP app is still open on the Switch." : $"Pokémon {_game.Title} isn't open on the Switch.") + "\n\n" +
            $"Yes = open Pokémon {_game.Title}" + (_game.Bot == BotKind.FrlgSwitch ? " and load your save" : "") +
            " first (ShinyForge finds it on the HOME menu or in All Software by its icon)" +
            (_game.Bot == BotKind.FrlgSwitch ? "" : " - then get into the game where the bot should start") + "\n" +
            "No = start the hunt anyway", "Shiny Hunt");
        return r == Dialogs.No || (r == Dialogs.Yes && await OpenGameOnSwitch(Status));
    }

    /// <summary>
    /// Another game (<paramref name="id"/>) is open and ShinyForge wants to <paramref name="then"/>: by name; save + close for games
    /// ShinyForge can save, the user saves then ShinyForge closes for other Pokémon games, the user closes any other game itself
    /// (→ Refuse once closed). Null = cancelled / still open.
    /// </summary>
    private async Task<SwitchGameLauncher.OtherGame?> AskAboutOtherGame(string id, string then, string caption)
    {
        var s = AppSettings.Current;
        var name = SwitchGameLauncher.GameName(id);
        var head = $"{name} is open on the Switch.\n\n";
        if (SwitchGameLauncher.GameOf(id) == null)
        {
            if (await Dialogs.Show(this, head + $"Save your progress in it and close it yourself (HOME, X, Close), then press OK - ShinyForge then goes on to {then}.",
                    caption, Dialogs.Ok, Dialogs.Cancel) != Dialogs.Ok) return null;
            string? still;
            try { still = (await Task.Run(() => SwitchGameLauncher.Running(s.SwitchIP, s.SwitchPort))).TitleId; }
            catch { still = null; }
            if (still != id) return SwitchGameLauncher.OtherGame.Refuse;
            await Dialogs.Info(this, $"{name} is still open on the Switch - close it, then try again.", caption);
            return null;
        }
        if (SwitchGameLauncher.CanSave(id))
        {
            var r = await Dialogs.YesNoCancel(this, head + $"Should ShinyForge save {name} before closing it?\n\n" +
                $"Yes = save it, close it, then {then}\nNo = close it WITHOUT saving (unsaved progress is lost)", caption);
            return r == Dialogs.Yes ? SwitchGameLauncher.OtherGame.SaveAndClose : r == Dialogs.No ? SwitchGameLauncher.OtherGame.Close : null;
        }
        return await Dialogs.Show(this, head + $"ShinyForge can't save {name} itself. Save your progress in it now, then press OK - ShinyForge closes it and goes on to {then}.",
                caption, Dialogs.Ok, Dialogs.Cancel) == Dialogs.Ok ? SwitchGameLauncher.OtherGame.Close : null;
    }

    /// <summary>Opens this game on the Switch (closing the FTP app first) and, for FireRed / LeafGreen, loads the save.</summary>
    private async Task<bool> OpenGameOnSwitch(Action<string> status, SwitchGameLauncher.OtherGame otherGame = SwitchGameLauncher.OtherGame.Refuse)
    {
        var s = AppSettings.Current;
        if (HuntService.IsPythonHuntRunning())
        {
            await Dialogs.Info(this, "The hunter is running - stop it first.", "ShinyForge - open the game");
            return false;
        }
        try
        {
            await Task.Run(() => SwitchGameLauncher.OpenGame(s.SwitchIP, s.SwitchPort, _game, m => Dialogs.Ui(() => status(m)), default, otherGame));
            status($"Pokémon {_game.Title} is open on the Switch" + (_game.Bot == BotKind.FrlgSwitch ? " with your save loaded." : "."));
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Opening the game on the Switch failed: " + ex);
            status(ex.Message);
            await Dialogs.Info(this, ex.Message, "ShinyForge - open the game");
            return false;
        }
    }

    /// <summary>Starts the chosen hunt bot (and, for the Python hunter, the rolling shiny-clip buffer).</summary>
    private async Task StartHuntNow(string key)
    {
        try
        {
            _gs.HuntBot = key;
            AppSettings.Current.Save();
            HuntService.ResetAutoFixes();
            HuntService.Start(_game, key);
            Status(key == "python"
                ? OsInfo.IsWindows ? "Python hunter started in its own window." : "Python hunter started - its output goes to hunt_console.log in the bot folder."
                : "SysBot.exe opened with the routine pre-selected - press Start All there.");
            if (key == "python") StartClipBuffer();
        }
        catch (Exception ex) { await Dialogs.Info(this, ex.Message, "Shiny Hunt"); }
        RefreshHuntStatus();
    }

    private void ScanSaves()
    {
        _gs.GameFolder = _folderBox.Text ?? "";
        AppSettings.Current.Save();
        _saveStatus.Foreground = Skin.B(Skin.Muted);
        _saveStatus.Text = "Searching for save files...";
        Task.Run(() => SaveService.FindSaves(_gs.GameFolder, _game)).ContinueWith(t => Dialogs.Ui(() =>
        {
            var saves = t.IsFaulted ? new List<FoundSave>() : t.Result;
            _save = saves.FirstOrDefault();
            _saveStatus.Foreground = Skin.B(_save != null ? Skin.Good : Skin.Warn);
            _saveStatus.Text = _save == null
                ? "No save file for this game was found in that folder (searched 3 folder levels deep)."
                : $"✔ Save found: {Path.GetFileName(_save.Path)}\n    Trainer {_save.Save.OT}  •  TID {_save.Save.DisplayTID}  •  {_save.Save.Version}  •  " +
                  $"{_save.Save.PlayedHours}h {_save.Save.PlayedMinutes:00}m" + (saves.Count > 1 ? $"  ({saves.Count} saves found, newest used)" : "");
            UpdateTrackerSaveLabel();
        }));
    }

    private async Task ExportSave()
    {
        if (_save == null) { ScanSaves(); await Dialogs.Info(this, "No save file loaded yet - choose the game folder first.", "Export"); return; }
        try
        {
            var baseFolder = Path.GetDirectoryName(_save.Path)!;
            if (!string.IsNullOrWhiteSpace(_gs.GameFolder) && Directory.Exists(_gs.GameFolder)) baseFolder = _gs.GameFolder;
            BackupService.Backup(_save.Path, spreadsheet: false);
            var (folder, count) = SaveService.ExportPokemon(_save.Save, _game, baseFolder);
            var infoPath = SaveService.WriteGameInfo(_save.Save, _game, _save.Path, baseFolder);
            File.Copy(infoPath, Path.Combine(folder, "game info.txt"), true);
            _saveStatus.Foreground = Skin.B(Skin.Good);
            _saveStatus.Text = $"✔ Exported {count} Pokémon to '{Path.GetFileName(folder)}' and wrote 'game info.txt'.";
            Status($"Exported {count} Pokémon from {_game.Title}.");
            OsInfo.OpenFolder(folder);
        }
        catch (Exception ex) { await Dialogs.Info(this, ex.Message, "Export"); }
    }

    private async Task OpenInPKHeX()
    {
        if (!await EnsureDependency("PKHeX")) return;
        if (DependencyService.PathOf("PKHeX") is not { } exe) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, _save != null ? $"\"{_save.Path}\"" : "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }
}
