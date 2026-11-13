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
            if (!HealthMonitor.CanReach(s.SwitchIP, s.SwitchPort, 2500))
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
        card.Body.Controls.Add(Info($"Switch: {AppSettings.Current.SwitchIP}:{AppSettings.Current.SwitchPort} (sys-botbase) - change in Settings.", Theme.Muted));

        var start = new FancyButton("Start hunting", Theme.GlyphPlay, Theme.Accent);
        var stop = new FancyButton("Stop Python hunter", Theme.GlyphStop, Color.FromArgb(150, 40, 60));
        start.Click += (_, _) =>
        {
            var key = radios.First(r => r.Checked).Tag as string ?? "";
            if (key == "python" && !EnsureDependency("Python 3")) return;
            if (key == "sysbot" && !EnsureDependency("SysBot.exe")) return;
            // The Switch must answer at its saved IP - if its address changed, ask for the new one first.
            if (!IpPrompt.EnsureSwitch(FindForm())) return;
            try
            {
                _gs.HuntBot = key;
                AppSettings.Current.Save();
                HuntService.ResetAutoFixes();
                HuntService.Start(_game, key);
                Status(key == "python" ? "Python hunter started in its own window." : "SysBot.exe opened with the routine pre-selected - press Start All there.");
            }
            catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, "Shiny Hunt", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            RefreshHuntStatus();
        };
        stop.Click += (_, _) => { HuntService.StopPythonHunt(); _clips.Stop(); RefreshHuntStatus(); };
        card.Body.Controls.Add(Row(start, stop));
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
            optionsButton.Click += (_, _) => { using var f = new HuntOptionsForm(rngAvailable: true); f.ShowDialog(FindForm()); RefreshHuntStatus(); };
            var stats = new FancyButton("Statistics", Theme.GlyphInfo, Theme.PanelHi);
            stats.Click += (_, _) => new HuntStatsForm("Python").Show(FindForm());
            card.Body.Controls.Add(Row(pause, optionsButton, stats));
            // When the hunter starts: rolling shiny-clip buffer (if enabled and not already recording)
            start.Click += (_, _) => { if (HuntService.IsPythonHuntRunning()) StartClipBuffer(); };
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

        start.Click += (_, _) =>
        {
            var ip = IpPrompt.GetChecked3DS(FindForm());
            if (ip == null) return;
            var options = new Services.ThreeDS.Bot3DSOptions
            {
                Host = ip,
                Mode = mode.SelectedIndex switch { 1 => Services.ThreeDS.Hunt3DSMode.StaticSoftReset, 2 => Services.ThreeDS.Hunt3DSMode.WildWalking, _ => Services.ThreeDS.Hunt3DSMode.Auto },
                WalkHorizontal = axis.SelectedIndex == 0,
                StopRule = StopRuleFor3DS(),
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
            Task.Run(() =>
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
        opts3.Click += (_, _) => { using var f = new HuntOptionsForm(rngAvailable: false); f.ShowDialog(FindForm()); };
        var stats3 = new FancyButton("Statistics", Theme.GlyphInfo, Theme.PanelHi);
        stats3.Click += (_, _) => new HuntStatsForm(_game.Title).Show(FindForm());
        card.Body.Controls.Add(Row(opts3, stats3));
        card.Body.Controls.Add(log);
        card.Body.Controls.Add(Info($"Encounters are logged to {Path.Combine(AppPaths.DataDir, "3ds_encounters.csv")}", Theme.Muted));
    }

    /// <summary>The stop rules from Hunt options, as a check for the 3DS bot (null = shinies only).</summary>
    private static Func<PKHeX.Core.PKM, string?>? StopRuleFor3DS()
    {
        var o = AppSettings.Current.Hunt;
        if (!o.HasStopRules) return null;
        var species = o.StopSpecies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return pk =>
        {
            var checks = new List<(string Name, bool Ok)>();
            var name = PKHeX.Core.SpeciesName.GetSpeciesName(pk.Species, 2);
            if (species.Count > 0) checks.Add(("species " + name, species.Contains(name, StringComparer.OrdinalIgnoreCase)));
            var nature = ((PKHeX.Core.Nature)pk.Nature).ToString();
            if (o.StopNatures.Count > 0) checks.Add(("nature " + nature, o.StopNatures.Contains(nature, StringComparer.OrdinalIgnoreCase)));
            var ivs = new Dictionary<string, int> { ["HP"] = pk.IV_HP, ["Atk"] = pk.IV_ATK, ["Def"] = pk.IV_DEF, ["SpA"] = pk.IV_SPA, ["SpD"] = pk.IV_SPD, ["Spe"] = pk.IV_SPE };
            if (o.MinIvTotal > 0) checks.Add(($"IV total {ivs.Values.Sum()}", ivs.Values.Sum() >= o.MinIvTotal));
            foreach (var (k, v) in o.MinIvs.Where(kv => kv.Value > 0)) checks.Add(($"{k} IV {ivs[k]}", ivs[k] >= v));
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
        _clips.Start(source, _audio?.SelectedItem as string ?? RecordingService.NoAudio, _game.Id,
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
        {
            sources.Add(RecordingService.WindowPrefix + "3DS Capture - Top");
            sources.Add(RecordingService.WindowPrefix + "3DS Capture - Bottom");
        }
        sources.AddRange(video.Select(v => RecordingService.DevicePrefix + v));
        if (_game.IsSwitch)
            sources.Add(RecordingService.WindowPrefix + "Streamlabs Desktop");
        var defaultSource = !string.IsNullOrEmpty(_gs.VideoSource) ? _gs.VideoSource
            : sources.FirstOrDefault(s => _game.IsSwitch ? s.Contains("Game Capture") : s.Contains("3DS Capture - Top"));
        _source = Combo(420, sources, defaultSource);

        var audios = new List<string> { RecordingService.NoAudio };
        audios.AddRange(audio);
        var defaultAudio = !string.IsNullOrEmpty(_gs.AudioDevice) ? _gs.AudioDevice
            : _game.IsSwitch ? audios.FirstOrDefault(a => a.Contains("Game Capture") && a.EndsWith("Audio")) : RecordingService.NoAudio;
        _audio = Combo(420, audios, defaultAudio);
        _segments = Combo(120, new[] { "5", "10", "15", "20", "30" }, _gs.SegmentMinutes.ToString());

        _recFolder = new TextBox { Width = 330, Text = _gs.RecordingFolder };
        Theme.StyleInput(_recFolder);
        var browse = new FancyButton("Browse", Theme.GlyphFolder, Theme.PanelHi);
        browse.Click += (_, _) => { var f = PickFolder("Where should recordings be saved?", _recFolder.Text); if (f != null) _recFolder.Text = f; };

        card.Body.Controls.Add(Row(Theme.Label("Video   ", Theme.Body, Theme.Text), _source));
        card.Body.Controls.Add(Row(Theme.Label("Audio   ", Theme.Body, Theme.Text), _audio));
        card.Body.Controls.Add(Row(Theme.Label("Segment length (minutes)  ", Theme.Body, Theme.Text), _segments));
        card.Body.Controls.Add(Row(Theme.Label("Folder  ", Theme.Body, Theme.Text), _recFolder, browse));

        _recButton = new FancyButton("Start recording", Theme.GlyphRecord, Color.FromArgb(190, 30, 50));
        _recButton.Click += (_, _) => ToggleRecording();
        var open = new FancyButton("Open folder", Theme.GlyphFolder, Theme.PanelHi);
        open.Click += (_, _) => { if (Directory.Exists(_recFolder.Text)) Process.Start("explorer.exe", _recFolder.Text); };
        var buttons = Row(_recButton, open);
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

    private void Launch3DSCapture()
    {
        if (!EnsureDependency("3DS Capture")) return;
        var cap = DependencyService.PathOf("3DS Capture");
        if (cap != null && Process.GetProcessesByName("3ds_capture").Length == 0)
            Process.Start(new ProcessStartInfo(cap) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(cap)! });
    }

    private void ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
            _recButton.Text = "Start recording";
            _recButton.Invalidate();
            return;
        }
        if (!EnsureDependency("FFmpeg")) return;
        if (_game.IsSwitch && !EnsureDependency("Streamlabs Desktop")) return;
        if (string.IsNullOrWhiteSpace(_recFolder.Text) || !Directory.Exists(_recFolder.Text))
        {
            var f = PickFolder("Choose the folder for the recordings (files are numbered automatically)", _recFolder.Text);
            if (f == null) return;
            _recFolder.Text = f;
        }
        var source = _source.SelectedItem as string ?? "";
        if (source.Contains("3DS Capture"))
        {
            Launch3DSCapture();
            Thread.Sleep(1500);
        }
        _gs.RecordingFolder = _recFolder.Text;
        _gs.SegmentMinutes = int.Parse(_segments.SelectedItem as string ?? "10");
        _gs.VideoSource = source;
        _gs.AudioDevice = _audio.SelectedItem as string ?? "";
        AppSettings.Current.Save();
        try
        {
            _recorder.Start(source, _gs.AudioDevice, _gs.RecordingFolder, _gs.SegmentMinutes);
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
            var cur = SpreadsheetService.FindWorkbook(_game);
            if (cur != null) Process.Start(new ProcessStartInfo(cur) { UseShellExecute = true });
        };
        var dex = new FancyButton("Pokédex (pop-out)", Theme.GlyphSparkle, Theme.Accent2);
        dex.Click += (_, _) => OpenDex();
        card.Body.Controls.Add(Row(change, autoSheet, openSheet));
        card.Body.Controls.Add(Row(dex));

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
            if (!HealthMonitor.CanReach(AppSettings.Current.SwitchIP, AppSettings.Current.SwitchPort, 2500))
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
                if (manual ? !IpPrompt.EnsureSwitch(FindForm()) : !HealthMonitor.CanReach(AppSettings.Current.SwitchIP, AppSettings.Current.SwitchPort, 2500))
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
        pkhex.Click += (_, _) => OpenInPKHeX();
        var info = new FancyButton("View Game Info", Theme.GlyphInfo, Theme.PanelHi);
        info.Click += (_, _) =>
        {
            var p = Path.Combine(_gs.GameFolder, "game info.txt");
            if (File.Exists(p)) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
            else MessageBox.Show(FindForm(), "Export first - the Game Info document is created together with the Pokémon HOME folder.", "Game Info");
        };
        card.Body.Controls.Add(Row(pkhex, info));
        _saveStatus = Info("", Theme.Text);
        card.Body.Controls.Add(_saveStatus);
        if (Directory.Exists(_gs.GameFolder))
            Ui(ScanSaves);
        else
            Ui(UpdateTrackerSaveLabel);
        return card;
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
