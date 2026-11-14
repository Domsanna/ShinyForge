using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// Wireless trades and Mystery Gift (pokeldn): checks the ESP32-S3 board, its radio firmware, the pokeldn app, esptool and
/// prod.keys; installs or updates what's missing only with the user's OK, and starts pokeldn.
/// </summary>
public sealed class PokeldnWindow : Window
{
    private readonly StackPanel _rows = new() { Spacing = 8, Margin = new Thickness(18, 8) };
    private readonly TextBlock _status = Skin.T("", 13, Skin.Muted);
    private readonly FancyButton _fix = new("Install / update everything", Skin.GlyphDownload, Skin.Accent);
    private readonly FancyButton _flash = new("Install firmware on the board", Skin.GlyphDownload, Skin.PanelHi);
    private readonly FancyButton _keys = new("Choose prod.keys...", Skin.GlyphFolder, Skin.PanelHi);
    private readonly FancyButton _open = new("Open pokeldn", Skin.GlyphPlay, Skin.Accent2);
    private readonly FancyButton _recheck = new("Check again", Skin.GlyphSync, Skin.PanelHi);
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

    public PokeldnWindow()
    {
        Title = "ShinyForge - wireless trades and Mystery Gift (pokeldn)";
        Icon = UI.App.Icon;
        Width = 880; Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Skin.B(Skin.Bg);
        var head = new Border
        {
            Background = Skin.B(Skin.Panel), Padding = new Thickness(18, 12),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Skin.Title("Wireless trades and Mystery Gift"),
                    Skin.Small("Trade with and send Mystery Gifts to Pokémon games on a real Switch over local wireless - nothing is installed on the console.\n" +
                                "Powered by pokeldn (Decryptu, AGPL-3.0). Needs an ESP32-S3 board on a USB data cable and your Switch's prod.keys.\n" +
                                "Games: FireRed/LeafGreen, Let's Go, Sword/Shield, BD/SP, Legends: Arceus, Scarlet/Violet, Legends: Z-A."),
                },
            },
        };
        var bottom = new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(14, 8), Child = new StackPanel { Spacing = 6, Children = { Skin.Wrap(_open, _fix, _flash, _keys, _recheck), _status } } };
        _recheck.Click += async (_, _) => await Check();
        _keys.Click += async (_, _) => await ChooseKeys();
        _open.Click += async (_, _) => { try { PokeldnService.Launch(); } catch (Exception ex) { await Dialogs.Info(this, ex.Message, "pokeldn"); } };
        _fix.Click += async (_, _) => await InstallEverything();
        _flash.Click += async (_, _) => await Flash();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new DockPanel { Children = { head, bottom, new ScrollViewer { Content = _rows } } };
        Opened += async (_, _) => await FirstRun();
    }

    /// <summary>First time: the ESP32-S3 notice; then the checks and offers; then (once) the prod.keys question.</summary>
    private async Task FirstRun()
    {
        if (UI.App.ScreenshotDir != null) { await Check(); return; }
        if (!AppSettings.Current.PokeldnSetupDone)
        {
            if (!await Dialogs.YesNo(this,
                    "This part of ShinyForge needs an ESP32-S3 board.\n\n" +
                    "The ESP32-S3 is a small, inexpensive microcontroller board (for example the Seeed XIAO ESP32S3 or an ESP32-S3 DevKit). It acts as the wireless radio that talks to your Switch. " +
                    "It connects to this computer with a USB DATA cable (charge-only cables don't work); use the board's native USB socket.\n\nDo you have an ESP32-S3 board?",
                    "ShinyForge - ESP32-S3 needed"))
            {
                await Dialogs.Info(this, "No problem - come back here once you have an ESP32-S3 board. Everything else in ShinyForge works without it.\n\npokeldn's list of supported boards opens in your browser.");
                try { Browser.Open("https://github.com/Decryptu/pokeldn/blob/main/docs/hardware_esp32.md#supported-boards"); } catch { }
                Close();
                return;
            }
        }
        await Check();
        if (AppSettings.Current.PokeldnSetupDone) return;
        if (_s.Board == null)
        {
            await Dialogs.Info(this, "Plug the ESP32-S3 into this computer now (native USB socket, data cable), then click OK.", "ShinyForge - plug in the board");
            await Check();
        }
        if (NeedsInstall()) await InstallEverything();
        if (string.IsNullOrEmpty(AppSettings.Current.ProdKeysPath) || !File.Exists(AppSettings.Current.ProdKeysPath))
        {
            await Dialogs.Info(this, "Last step: where is your Switch's prod.keys file on this computer?\n\npokeldn needs the keys dumped from YOUR OWN Switch for the local wireless encryption. " +
                                     "ShinyForge only remembers where the file is - it never copies, uploads or shows it.", "ShinyForge - prod.keys");
            await ChooseKeys();
        }
        AppSettings.Current.PokeldnSetupDone = true;
        AppSettings.Current.Save();
        await Check();
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
                var s = new State { App = PokeldnService.FindApp(), Esptool = PokeldnService.FindEsptool(), FirmwareFile = PokeldnService.FindFirmware(), Boards = PokeldnService.FindBoards() };
                s.AppTag = PokeldnService.InstalledAppTag(s.App);
                s.FirmwareFileVersion = PokeldnService.FirmwareVersionOf(s.FirmwareFile);
                s.Board = s.Boards.FirstOrDefault(b => b.NativeUsb) ?? s.Boards.FirstOrDefault();
                if (s.Board != null) s.Firmware = PokeldnService.QueryFirmware(s.Board.Port);
                try { s.LatestTag = (await PokeldnService.LatestAsync()).Tag; } catch (Exception e) { s.LatestError = e.Message; }
                return s;
            });
        }
        finally { SetBusy(false, ""); }
        Render();
    }

    private void Render()
    {
        _rows.Children.Clear();
        var s = _s;
        Row(s.Board is { NativeUsb: true } ? Skin.Good : s.Board != null ? Skin.Warn : Skin.Bad, "ESP32-S3 board",
            s.Board == null ? "Not found. Plug it into the board's native USB socket with a data cable." :
            s.Board.NativeUsb ? $"{s.Board.Kind} on {s.Board.Port}" + (s.Boards.Count > 1 ? $"  ({s.Boards.Count} boards plugged in - using this one)" : "")
                              : $"{s.Board.Kind} on {s.Board.Port} - ShinyForge sets up ESP32-S3 boards; use pokeldn's Board page for other chips.");
        Row(s.Firmware is { Answered: true } && !s.FirmwareOutdated ? Skin.Good : s.Board == null ? Skin.Muted : Skin.Warn, "Radio firmware",
            s.Board == null ? "Plug in the board to check its firmware." :
            s.Firmware is { Answered: true } f ? $"pokeldn firmware {f.Version ?? "(unversioned)"}{(f.Target.Length > 0 ? $" for {f.Target}" : "")}" +
                (f.Target.Length > 0 && f.Target != "esp32s3" ? " - this board isn't an ESP32-S3" : s.FirmwareOutdated ? $" - update available ({s.FirmwareFileVersion})" : " - up to date") :
            $"Not installed or not answering ({s.Firmware?.Error}).");
        Row(s.App != null && !s.AppOutdated ? Skin.Good : s.App != null ? Skin.Warn : Skin.Bad, "pokeldn app",
            s.App == null ? "Not installed." + (s.LatestTag != null ? $" Latest: {s.LatestTag}." : "") :
            $"{s.AppTag ?? "installed"}  •  {s.App}" + (s.AppOutdated ? $"  - update available ({s.LatestTag})" : s.LatestTag != null ? " - up to date" : $"  (couldn't check for updates: {s.LatestError})"));
        Row(s.FirmwareFile != null ? Skin.Good : Skin.Bad, "Firmware file", s.FirmwareFile == null ? "Not downloaded yet." : $"{PokeldnService.FirmwareAsset} {s.FirmwareFileVersion}  •  {s.FirmwareFile}");
        Row(s.Esptool != null ? Skin.Good : Skin.Bad, "esptool (firmware installer)", s.Esptool ?? "Not installed.");
        var keys = AppSettings.Current.ProdKeysPath;
        var missing = File.Exists(keys) ? PokeldnService.MissingKeys(keys) : null;
        Row(missing is { Count: 0 } ? Skin.Good : Skin.Bad, "prod.keys",
            string.IsNullOrEmpty(keys) ? "Not chosen yet." : !File.Exists(keys) ? $"Not found any more: {keys}" :
            missing!.Count == 0 ? $"{keys}  (all {PokeldnService.KeysNeeded.Length} needed keys present)" : $"{keys} - missing: {string.Join(", ", missing)}");
        _fix.IsEnabled = NeedsInstall();
        _flash.IsEnabled = s.Board is { NativeUsb: true } && s.FirmwareFile != null && s.Esptool != null;
        _open.IsEnabled = s.App != null;
        _status.Text = !NeedsInstall() && missing is { Count: 0 } && s.Firmware is { Answered: true }
            ? "✔ Everything is ready - click Open pokeldn, choose a game and a tool, and follow the steps on the console."
            : "Fix the items marked above - ShinyForge can download and install them for you.";
    }

    private void Row(Color c, string name, string detail)
    {
        var icon = Skin.T(c == Skin.Good ? Skin.GlyphCheck : c == Skin.Muted ? Skin.GlyphInfo : Skin.GlyphWarn, 20, c, wrap: false);
        icon.Width = 34;
        icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);
        _rows.Children.Add(new Border
        {
            Background = Skin.B(Skin.Panel), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10),
            Child = new DockPanel { Children = { icon, new StackPanel { Spacing = 2, Children = { Skin.H2(name), Skin.Small(detail) } } } },
        });
    }

    private async Task InstallEverything()
    {
        var s = _s;
        var todo = new List<string>();
        if (s.App == null) todo.Add("download and install the pokeldn app (" + (s.LatestTag ?? "latest") + ")");
        else if (s.AppOutdated) todo.Add($"update pokeldn {s.AppTag} → {s.LatestTag}");
        if (s.FirmwareFile == null || s.AppOutdated) todo.Add("download the ESP32-S3 radio firmware");
        if (s.Esptool == null) todo.Add("download esptool (Espressif's official firmware installer)");
        var mayFlash = s.Board is { NativeUsb: true } && (s.FirmwareMissing || s.FirmwareOutdated || (s.FirmwareFile == null && s.Firmware is { Answered: true }));
        if (mayFlash) todo.Add($"install or update the radio firmware on the board ({s.Board!.Port}) if it isn't current");
        if (todo.Count == 0) return;
        if (!await Dialogs.YesNo(this, "Some of the software needed for this feature is missing or out of date.\n\nAllow ShinyForge to:\n  • " + string.Join("\n  • ", todo) +
                                       "\n\nDownloads come from the official GitHub releases (pokeldn by Decryptu, esptool by Espressif) and are checked against their published checksums.", "ShinyForge - install / update"))
            return;
        SetBusy(true, "Working...");
        var results = new List<string>();
        void P(string m) => Dialogs.Ui(() => _status.Text = m);
        try
        {
            if (s.App == null || s.AppOutdated || s.FirmwareFile == null) results.Add(await PokeldnService.InstallAppAndFirmware(P));
            if (PokeldnService.FindEsptool() == null) results.Add(await PokeldnService.InstallEsptool(P));
            if (mayFlash)
            {
                var fileVersion = PokeldnService.FirmwareVersionOf(PokeldnService.FindFirmware());
                var current = s.Firmware is { Answered: true, Version: { } v } && fileVersion != null && Version.TryParse(v, out var have) && Version.TryParse(fileVersion, out var want) && have >= want;
                results.Add(current ? $"The board already has firmware {s.Firmware!.Version} - nothing to install." : await PokeldnService.FlashFirmware(s.Board!.Port, P));
            }
            AppPaths.Log("pokeldn setup: " + string.Join(" | ", results));
        }
        catch (Exception ex) { results.Add("✗ " + ex.Message); AppPaths.Log("pokeldn setup failed: " + ex); }
        finally { SetBusy(false, ""); }
        await Dialogs.Info(this, string.Join("\n\n", results), "ShinyForge - pokeldn");
        await Check();
    }

    private async Task Flash()
    {
        if (_s.Board is not { NativeUsb: true } b) return;
        if (!await Dialogs.YesNo(this, $"Install the pokeldn radio firmware {_s.FirmwareFileVersion} on the board on {b.Port}?\n\nThis replaces whatever program is on the board. Keep it plugged in until it's finished (about a minute).", "ShinyForge - install firmware")) return;
        SetBusy(true, "Installing firmware...");
        string msg;
        try { msg = await PokeldnService.FlashFirmware(b.Port, m => Dialogs.Ui(() => _status.Text = m)); }
        catch (Exception ex) { msg = ex.Message; }
        finally { SetBusy(false, ""); }
        await Dialogs.Info(this, msg, "ShinyForge - firmware");
        await Check();
    }

    private async Task ChooseKeys()
    {
        var start = File.Exists(AppSettings.Current.ProdKeysPath) ? Path.GetDirectoryName(AppSettings.Current.ProdKeysPath) : Path.Combine(OsInfo.Home, ".switch");
        if (await Dialogs.PickFile(this, "Locate your Switch's prod.keys", start, ("Switch keys", new[] { "prod.keys", "*.keys" })) is not { } file) return;
        try
        {
            var missing = PokeldnService.MissingKeys(file);
            if (missing.Count > 0 && !await Dialogs.YesNo(this, $"This file doesn't contain {string.Join(", ", missing)}, which pokeldn needs.\n\nUse it anyway?", "prod.keys")) return;
            PokeldnService.SetKeysPath(file);
            if (System.Diagnostics.Process.GetProcessesByName("pokeldn").Length > 0) await Dialogs.Info(this, "Saved. pokeldn is open - restart it so it picks up the keys.", "prod.keys");
        }
        catch (Exception ex) { await Dialogs.Info(this, ex.Message, "prod.keys"); }
        Render();
    }

    private void SetBusy(bool busy, string text)
    {
        _busy = busy;
        foreach (var b in new[] { _fix, _flash, _keys, _open, _recheck }) b.IsEnabled = !busy;
        if (text.Length > 0 || busy) _status.Text = text;
    }
}
