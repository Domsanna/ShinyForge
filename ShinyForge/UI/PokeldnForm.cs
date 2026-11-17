using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// Local wireless trades and Mystery Gift (pokeldn): checks the ESP32-S3 board, its radio firmware, the pokeldn app,
/// esptool and prod.keys; offers to install or update what's missing (only with the user's OK) and starts pokeldn.
/// </summary>
public sealed class PokeldnForm : Form
{
    private readonly FlowLayoutPanel _rows = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(18, 8, 18, 8) };
    private readonly Label _status = Theme.Label("", Theme.Body, Theme.Muted);
    private readonly FancyButton _fix = new("Install / update everything", Theme.GlyphDownload, Theme.Accent);
    private readonly FancyButton _flash = new("Install firmware on the board", Theme.GlyphDownload, Theme.PanelHi);
    private readonly FancyButton _keys = new("Choose prod.keys...", Theme.GlyphFolder, Theme.PanelHi);
    private readonly FancyButton _open = new("Open pokeldn", Theme.GlyphPlay, Theme.Accent2);
    private readonly FancyButton _recheck = new("Check again", Theme.GlyphSync, Theme.PanelHi);
    private State _s = new();
    private bool _busy;

    private sealed class State
    {
        public List<UsbBoard> Boards = new();
        public UsbBoard? Board;
        public FirmwareInfo? Firmware;
        public string? FirmwareFile, FirmwareFileVersion, App, AppTag, Esptool, LatestTag, LatestError;
        public bool AppOutdated => LatestTag != null && (AppTag == null || AppInfo.ParseTag(AppTag) < AppInfo.ParseTag(LatestTag));
        public bool FirmwareOutdated => Firmware is { Answered: true, Version: { } v } && FirmwareFileVersion != null && Version.TryParse(v, out var a) && Version.TryParse(FirmwareFileVersion, out var b) && a < b;
        public bool FirmwareMissing => Board != null && Firmware is { Answered: false } f && !(f.Error ?? "").Contains("in use");
    }

    public PokeldnForm()
    {
        Text = "ShinyForge - wireless trades & Mystery Gift (pokeldn)";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Size = new Size(860, 720);
        MinimumSize = new Size(700, 560);
        StartPosition = FormStartPosition.CenterParent;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var head = new Panel { Dock = DockStyle.Top, Height = 118, BackColor = Theme.Panel };
        var title = Theme.Label("Wireless trades and Mystery Gift", Theme.Title);
        title.Location = new Point(18, 10);
        var sub = Theme.Label("Trade with and send Mystery Gifts to Pokémon games on a real Switch over local wireless - nothing is installed on the console.\n" +
                              "Powered by pokeldn (Decryptu, AGPL-3.0). Needs an ESP32-S3 board on a USB data cable and your Switch's prod.keys.\n" +
                              "Games: FireRed/LeafGreen, Let's Go, Sword/Shield, BD/SP, Legends: Arceus, Scarlet/Violet, Legends: Z-A.", Theme.Small, Theme.Muted);
        sub.Location = new Point(20, 54);
        head.Controls.AddRange(new Control[] { title, sub });

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 104, Padding = new Padding(14, 8, 14, 4), BackColor = Theme.Panel };
        _status.Margin = new Padding(4, 10, 0, 0);
        _status.MaximumSize = new Size(800, 40);
        bottom.Controls.AddRange(new Control[] { _open, _fix, _flash, _keys, _recheck, _status });
        bottom.SetFlowBreak(_recheck, true);

        _recheck.Click += async (_, _) => await Check();
        _keys.Click += (_, _) => ChooseKeys();
        _open.Click += (_, _) => { try { PokeldnService.Launch(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "pokeldn"); } };
        _fix.Click += async (_, _) => await InstallEverything(askFirst: true);
        _flash.Click += async (_, _) => await Flash();

        Controls.Add(_rows);
        Controls.Add(bottom);
        Controls.Add(head);
        Shown += async (_, _) => await FirstRun();
    }

    /// <summary>First time: the ESP32-S3 notice; then the checks; then (once) the prod.keys question.</summary>
    private async Task FirstRun()
    {
        if (!AppSettings.Current.PokeldnSetupDone)
        {
            var r = MessageBox.Show(this,
                "This part of ShinyForge needs an ESP32-S3 board.\n\n" +
                "The ESP32-S3 is a small, inexpensive microcontroller board (for example the Seeed XIAO ESP32S3 or an ESP32-S3 DevKit). " +
                "It acts as the wireless radio that talks to your Switch. It connects to this PC with a USB DATA cable " +
                "(charge-only cables don't work); use the board's native USB socket.\n\n" +
                "Do you have an ESP32-S3 board?",
                "ShinyForge - ESP32-S3 needed", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r != DialogResult.Yes)
            {
                MessageBox.Show(this, "No problem - come back here once you have an ESP32-S3 board. Everything else in ShinyForge works without it.\n\n" +
                                      "pokeldn's list of supported boards opens in your browser.", "ShinyForge");
                try { Browser.Open("https://github.com/Decryptu/pokeldn/blob/main/docs/hardware_esp32.md#supported-boards"); } catch { }
                Close();
                return;
            }
        }
        await Check();
        if (!AppSettings.Current.PokeldnSetupDone)
        {
            if (_s.Board == null)
                MessageBox.Show(this, "Plug the ESP32-S3 into this PC now (native USB socket, data cable), then click OK.", "ShinyForge - plug in the board");
            if (_s.Board == null) await Check();
            if (NeedsInstall())
                await InstallEverything(askFirst: true);
            if (string.IsNullOrEmpty(AppSettings.Current.ProdKeysPath) || !File.Exists(AppSettings.Current.ProdKeysPath))
            {
                MessageBox.Show(this,
                    "Last step: where is your Switch's prod.keys file on this computer?\n\n" +
                    "pokeldn needs the keys dumped from YOUR OWN Switch for the local wireless encryption. " +
                    "ShinyForge only remembers where the file is - it never copies, uploads or shows it.",
                    "ShinyForge - prod.keys", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ChooseKeys();
            }
            AppSettings.Current.PokeldnSetupDone = true;
            AppSettings.Current.Save();
            await Check();
        }
    }

    private bool NeedsInstall() => _s.App == null || _s.AppOutdated || _s.Esptool == null || _s.FirmwareFile == null || _s.FirmwareMissing || _s.FirmwareOutdated;

    private async Task Check()
    {
        if (_busy) return;
        SetBusy(true, "Checking the board, firmware and pokeldn...");
        try
        {
            _s = await Task.Run(async () =>
            {
                var s = new State
                {
                    App = PokeldnService.FindApp(),
                    Esptool = PokeldnService.FindEsptool(),
                    FirmwareFile = PokeldnService.FindFirmware(),
                    Boards = PokeldnService.FindBoards(),
                };
                s.AppTag = PokeldnService.InstalledAppTag(s.App);
                s.FirmwareFileVersion = PokeldnService.FirmwareVersionOf(s.FirmwareFile);
                s.Board = s.Boards.FirstOrDefault(b => b.NativeUsb) ?? s.Boards.FirstOrDefault();
                if (s.Board != null) s.Firmware = PokeldnService.QueryFirmware(s.Board.Port);
                try { s.LatestTag = await PokeldnService.LatestTagAsync(); } catch (Exception e) { s.LatestError = e.Message; }
                return s;
            });
        }
        finally { SetBusy(false, ""); }
        Render();
    }

    private void Render()
    {
        _rows.Controls.Clear();
        var s = _s;
        Row(s.Board != null && s.Board.NativeUsb ? Ok : s.Board != null ? Warn : Bad, "ESP32-S3 board",
            s.Board == null ? "Not found. Plug it into the board's native USB socket with a data cable." :
            s.Board.NativeUsb ? $"{s.Board.Kind} on {s.Board.Port}" + (s.Boards.Count > 1 ? $"  ({s.Boards.Count} boards plugged in - using this one)" : "")
                              : $"{s.Board.Kind} on {s.Board.Port} - ShinyForge sets up ESP32-S3 boards; use pokeldn's Board page for other chips.");
        Row(s.Firmware is { Answered: true } && !s.FirmwareOutdated ? Ok : s.Board == null ? Muted : Warn, "Radio firmware",
            s.Board == null ? "Plug in the board to check its firmware." :
            s.Firmware is { Answered: true } f ? $"pokeldn firmware {f.Version ?? "(unversioned)"}{(f.Target.Length > 0 ? $" for {f.Target}" : "")}" +
                (f.Target.Length > 0 && f.Target != "esp32s3" ? " - this board isn't an ESP32-S3" : s.FirmwareOutdated ? $" - update available ({s.FirmwareFileVersion})" : " - up to date") :
            $"Not installed or not answering ({s.Firmware?.Error}).");
        Row(s.App != null && !s.AppOutdated ? Ok : s.App != null ? Warn : Bad, "pokeldn app",
            s.App == null ? "Not installed." + (s.LatestTag != null ? $" Latest: {s.LatestTag}." : "") :
            $"{s.AppTag ?? "installed"}  •  {s.App}" + (s.AppOutdated ? $"  - update available ({s.LatestTag})" : s.LatestTag != null ? " - up to date" : $"  (couldn't check for updates: {s.LatestError})"));
        Row(s.FirmwareFile != null ? Ok : Bad, "Firmware file",
            s.FirmwareFile == null ? "Not downloaded yet." : $"{PokeldnService.FirmwareAsset} {s.FirmwareFileVersion}  •  {s.FirmwareFile}");
        Row(s.Esptool != null ? Ok : Bad, "esptool (firmware installer)", s.Esptool ?? "Not installed.");
        var keys = AppSettings.Current.ProdKeysPath;
        var missing = File.Exists(keys) ? PokeldnService.MissingKeys(keys) : null;
        Row(missing is { Count: 0 } ? Ok : Bad, "prod.keys",
            string.IsNullOrEmpty(keys) ? "Not chosen yet." : !File.Exists(keys) ? $"Not found any more: {keys}" :
            missing!.Count == 0 ? $"{keys}  (all {PokeldnService.KeysNeeded.Length} needed keys present)" : $"{keys} - missing: {string.Join(", ", missing)}");

        _fix.Enabled = NeedsInstall();
        _flash.Enabled = s.Board is { NativeUsb: true } && s.FirmwareFile != null && s.Esptool != null;
        _open.Enabled = s.App != null;
        _status.Text = !NeedsInstall() && missing is { Count: 0 } && s.Firmware is { Answered: true }
            ? "✔ Everything is ready - click Open pokeldn, choose a game and a tool, and follow the steps on the console."
            : "Fix the items marked above - ShinyForge can download and install them for you.";
    }

    private static readonly Color Ok = Theme.Good, Warn = Theme.Warn, Bad = Theme.Bad, Muted = Theme.Muted;

    private void Row(Color c, string name, string detail)
    {
        var p = new Panel { Size = new Size(790, 64), BackColor = Theme.Panel, Margin = new Padding(0, 0, 0, 8) };
        var icon = Theme.Label(c == Ok ? Theme.GlyphCheck : c == Muted ? Theme.GlyphInfo : Theme.GlyphWarn, Theme.Icons, c);
        icon.Location = new Point(14, 20);
        var n = Theme.Label(name, Theme.H2, Theme.Text);
        n.Location = new Point(48, 6);
        var d = Theme.Label(detail, Theme.Small, Theme.Muted);
        d.Location = new Point(50, 36);
        d.MaximumSize = new Size(720, 26);
        d.AutoEllipsis = true;
        p.Controls.AddRange(new Control[] { icon, n, d });
        _rows.Controls.Add(p);
    }

    private async Task InstallEverything(bool askFirst)
    {
        var s = _s;
        var todo = new List<string>();
        if (s.App == null) todo.Add("download and install the pokeldn app (" + (s.LatestTag ?? "latest") + ")");
        else if (s.AppOutdated) todo.Add($"update pokeldn {s.AppTag} → {s.LatestTag}");
        if (s.FirmwareFile == null || s.AppOutdated) todo.Add("download the ESP32-S3 radio firmware");
        if (s.Esptool == null) todo.Add("download esptool (Espressif's official firmware installer)");
        // The board is flashed only if it has no pokeldn firmware or an older one (decided again once the file is downloaded).
        var mayFlash = s.Board is { NativeUsb: true } && (s.FirmwareMissing || s.FirmwareOutdated || (s.FirmwareFile == null && s.Firmware is { Answered: true }));
        if (mayFlash) todo.Add($"install or update the radio firmware on the board ({s.Board!.Port}) if it isn't current");
        if (todo.Count == 0) return;
        if (askFirst && MessageBox.Show(this,
                "Some of the software needed for this feature is missing or out of date.\n\nAllow ShinyForge to:\n  • " + string.Join("\n  • ", todo) +
                "\n\nDownloads come from the official GitHub releases (pokeldn by Decryptu, esptool by Espressif) and are checked against their published checksums.",
                "ShinyForge - install / update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        SetBusy(true, "Working...");
        var results = new List<string>();
        void P(string m) => BeginInvoke(() => _status.Text = m);
        try
        {
            if (s.App == null || s.AppOutdated || s.FirmwareFile == null)
                results.Add(await PokeldnService.InstallAppAndFirmware(P));
            if (PokeldnService.FindEsptool() == null)
                results.Add(await PokeldnService.InstallEsptool(P));
            if (mayFlash)
            {
                var fileVersion = PokeldnService.FirmwareVersionOf(PokeldnService.FindFirmware());
                var current = s.Firmware is { Answered: true, Version: { } v } && fileVersion != null &&
                              Version.TryParse(v, out var have) && Version.TryParse(fileVersion, out var want) && have >= want;
                results.Add(current ? $"The board already has firmware {s.Firmware!.Version} - nothing to install." : await PokeldnService.FlashFirmware(s.Board!.Port, P));
            }
            AppPaths.Log("pokeldn setup: " + string.Join(" | ", results));
        }
        catch (Exception ex)
        {
            results.Add("✗ " + ex.Message);
            AppPaths.Log("pokeldn setup failed: " + ex);
        }
        finally { SetBusy(false, ""); }
        MessageBox.Show(this, string.Join("\n\n", results), "ShinyForge - pokeldn");
        await Check();
    }

    private async Task Flash()
    {
        if (_s.Board is not { NativeUsb: true } b) return;
        if (MessageBox.Show(this, $"Install the pokeldn radio firmware {_s.FirmwareFileVersion} on the board on {b.Port}?\n\n" +
                                  "This replaces whatever program is on the board. Keep it plugged in until it's finished (about a minute).",
                "ShinyForge - install firmware", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        SetBusy(true, "Installing firmware...");
        string msg;
        try { msg = await PokeldnService.FlashFirmware(b.Port, m => BeginInvoke(() => _status.Text = m)); }
        catch (Exception ex) { msg = ex.Message; }
        finally { SetBusy(false, ""); }
        MessageBox.Show(this, msg, "ShinyForge - firmware");
        await Check();
    }

    private void ChooseKeys()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Locate your Switch's prod.keys", Filter = "Switch keys (prod.keys)|prod.keys;*.keys|All files|*.*",
            InitialDirectory = File.Exists(AppSettings.Current.ProdKeysPath) ? Path.GetDirectoryName(AppSettings.Current.ProdKeysPath)
                : Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".switch"))
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".switch") : "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var missing = PokeldnService.MissingKeys(dlg.FileName);
            if (missing.Count > 0 && MessageBox.Show(this, $"This file doesn't contain {string.Join(", ", missing)}, which pokeldn needs.\n\nUse it anyway?",
                    "prod.keys", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            PokeldnService.SetKeysPath(dlg.FileName);
            if (System.Diagnostics.Process.GetProcessesByName("pokeldn").Length > 0)
                MessageBox.Show(this, "Saved. pokeldn is open - restart it so it picks up the keys.", "prod.keys");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "prod.keys"); }
        Render();
    }

    private void SetBusy(bool busy, string text)
    {
        _busy = busy;
        UseWaitCursor = busy;
        foreach (var b in new[] { _fix, _flash, _keys, _open, _recheck }) b.Enabled = !busy;
        if (text.Length > 0 || busy) _status.Text = text;
    }
}
