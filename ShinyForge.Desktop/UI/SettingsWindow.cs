using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>Settings, grouped like the Windows version (Windows-only programs are left out on macOS / Linux).</summary>
public sealed class SettingsWindow : Window
{
    private readonly List<Action> _apply = new();

    public SettingsWindow()
    {
        Title = "ShinyForge - Settings";
        Icon = App.Icon;
        Width = 880; Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Skin.B(Skin.Bg);
        var s = AppSettings.Current;
        var body = new StackPanel { Margin = new Thickness(18), Spacing = 4 };
        void Group(string name) { var h = Skin.H2(name, Skin.Gold); h.Margin = new Thickness(0, 14, 0, 4); body.Children.Add(h); }
        void Help(string? help) { if (help != null) { var t = Skin.Small(help); t.Margin = new Thickness(0, 0, 0, 6); body.Children.Add(t); } }
        void Text(string label, Func<string> get, Action<string> set, string? help = null, bool folder = false, bool readOnly = false)
        {
            var box = Skin.Input(get(), 400);
            box.IsReadOnly = readOnly;
            var row = Skin.Row(Label(label), box);
            if (folder)
            {
                var b = new FancyButton("Browse", Skin.GlyphFolder, Skin.PanelHi) { Height = 32 };
                b.Click += async (_, _) => { if (await Dialogs.PickFolder(this, label, box.Text) is { } f) box.Text = f; };
                row.Children.Add(b);
            }
            body.Children.Add(row);
            Help(help);
            if (!readOnly) _apply.Add(() => set((box.Text ?? "").Trim()));
        }
        void Number(string label, Func<int> get, Action<int> set, int min, int max, string? help = null)
        {
            var n = Skin.Number(min, max, get(), 140);
            body.Children.Add(Skin.Row(Label(label), n));
            Help(help);
            _apply.Add(() => set((int)(n.Value ?? min)));
        }
        void Bool(string label, Func<bool> get, Action<bool> set, string? help = null)
        {
            var c = Skin.Check(label, get());
            body.Children.Add(c);
            Help(help);
            _apply.Add(() => set(c.IsChecked == true));
        }

        Group("Switch");
        Text("Switch IP address", () => s.SwitchIP, v => s.SwitchIP = v, "IP of the modded Switch running sys-botbase.");
        Number("sys-botbase port", () => s.SwitchPort, v => s.SwitchPort = v, 1, 65535);
        Group("3DS");
        Text("3DS IP address", () => s.ThreeDSIP, v => s.ThreeDSIP = v, "Shown in Rosalina's menu. Asked once each time ShinyForge starts and remembered until it closes (not saved).");
        Group("Paths");
        Text("Pokédex spreadsheets folder", () => s.SpreadsheetRoot, v => s.SpreadsheetRoot = v, "Folder containing the Gen 1 ... Gen 9 tracker workbooks.", folder: true);
        Text("Backup Tools folder", () => AppPaths.BackupToolsDir, _ => { }, "Programs not found on this computer are looked for in this folder (open it from Tools & Updates).", readOnly: true);
        Text("FFmpeg (blank = auto-detect)", () => s.FfmpegPath, v => s.FfmpegPath = v);
        if (OsInfo.IsWindows)
        {
            Text("3DS Capture program", () => s.Capture3DSPath, v => s.Capture3DSPath = v);
            Text("Streamlabs Desktop", () => s.StreamlabsPath, v => s.StreamlabsPath = v);
            Text("PKHeX.exe (blank = auto-detect)", () => s.PKHeXPath, v => s.PKHeXPath = v);
        }
        Text("Console frame pictures (blank = Desktop/Console Frames)", () => s.ConsoleFramesFolder, v => s.ConsoleFramesFolder = v, "PNG console pictures with see-through screens, drawn around recordings.", folder: true);
        Text("Checkpoint folder (blank = search the SD card)", () => s.CheckpointFolder, v => s.CheckpointFolder = v, "A copy of the SD card (or its Checkpoint / saves folder) on this computer.", folder: true);
        Group("Bots");
        Text("Python hunting bot folder", () => s.PythonBotFolder, v => s.PythonBotFolder = v, "Blank or missing = the hunter that ships with ShinyForge.", folder: true);
        if (OsInfo.IsWindows) Text("SysBot.exe", () => s.SysBotPath, v => s.SysBotPath = v);
        Text("pokeldn program (blank = installed by ShinyForge)", () => s.PokeldnPath, v => s.PokeldnPath = v);
        Group("Updates");
        Bool("Check for updates every start", () => s.CheckUpdatesOnStart, v => s.CheckUpdatesOnStart = v);
        Group("Notifications (phone / Discord)");
        Text("Discord webhook URL", () => s.DiscordWebhookUrl, v => s.DiscordWebhookUrl = v, "Discord: channel settings → Integrations → Webhooks → New Webhook → Copy URL. Paste it here.");
        Bool("Discord: @everyone on a shiny", () => s.DiscordMentionEveryone, v => s.DiscordMentionEveryone = v);
        Text("ntfy topic (phone push)", () => s.NtfyTopic, v => s.NtfyTopic = v, "Free phone push: install the 'ntfy' app (Android / iPhone), subscribe to a topic name only you know (e.g. shinyforge-7f3k2q) and type the same name here.");
        Text("ntfy server", () => s.NtfyServer, v => s.NtfyServer = v);
        Bool("Notify: shiny / stop-rule found", () => s.NotifyShiny, v => s.NotifyShiny = v);
        Bool("Notify: problems (system check)", () => s.NotifyProblems, v => s.NotifyProblems = v);
        Bool("Notify: warnings too", () => s.NotifyWarnings, v => s.NotifyWarnings = v);
        Group("Safety");
        Bool("Keep the computer awake while hunting / recording", () => s.KeepAwake, v => s.KeepAwake = v);
        Bool("Automatic backups", () => s.AutoBackups, v => s.AutoBackups = v, "Copies saves and tracker spreadsheets (Data/Backups) before ShinyForge reads or changes them.");
        Number("Backups to keep per save file", () => s.BackupsToKeepSaves, v => s.BackupsToKeepSaves = v, 1, 500);
        Number("Backups to keep per spreadsheet", () => s.BackupsToKeepSheets, v => s.BackupsToKeepSheets = v, 1, 50, "Spreadsheets are big (up to ~235 MB each) - keep this small.");
        Group("Shiny clips");
        Bool("Save a clip of every shiny", () => s.ShinyClips, v => s.ShinyClips = v, "While a hunt runs, the capture device chosen in 'Record Gameplay' is recorded in a short rolling buffer; when a shiny appears it's saved as a video.");
        Number("Clip length (seconds)", () => s.ShinyClipSeconds, v => s.ShinyClipSeconds = v, 20, 300);
        Group("Stream overlay & phone remote");
        Bool("Stream overlay", () => s.OverlayEnabled, v => s.OverlayEnabled = v, "OBS/Streamlabs: add a 'Browser Source' with the overlay address (button below), or 'Text (read from file)' sources from Data/Overlay.");
        Bool("Phone remote (home network)", () => s.RemoteEnabled, v => s.RemoteEnabled = v, "Control/watch the hunt from your phone's browser on the same WiFi. Protected by the PIN.");
        Number("Port", () => s.WebPort, v => s.WebPort = v, 1024, 65535);
        Text("Remote PIN", () => WebServer.Pin, v => s.RemotePin = v);

        var test = new FancyButton("Test notification", Skin.GlyphSparkle, Skin.PanelHi);
        test.Click += async (_, _) => { Apply(); var r = await NotifyService.SendAsync("ShinyForge test", "Notifications are working! ✨", important: false); await Dialogs.Info(this, r, "Test notification"); };
        var backups = new FancyButton("Backups...", Skin.GlyphSave, Skin.PanelHi);
        backups.Click += async (_, _) => await new BackupsWindow().ShowDialog(this);
        var links = new FancyButton("Overlay / remote links...", Skin.GlyphWifi, Skin.PanelHi);
        links.Click += async (_, _) =>
        {
            Apply();
            WebServer.Apply();
            await Dialogs.Info(this, $"Stream overlay (OBS/Streamlabs → Browser Source, 520 x 140):\n{WebServer.OverlayUrl}\nText files for Text sources: {WebServer.OverlayDir}\n\n" +
                                     $"Phone remote (open on your phone, same WiFi):\n{WebServer.RemoteUrl}\n\n" +
                                     (s.OverlayEnabled || s.RemoteEnabled ? "" : "Both are switched OFF - turn them on above first.\n\n") + "Select the text to copy the addresses.", "Overlay / remote");
        };
        var portable = new FancyButton("Create portable copy...", "", Skin.PanelHi);
        portable.Click += async (_, _) => await CreatePortableCopy();
        var save = new FancyButton("Save", Skin.GlyphCheck, Skin.Accent);
        save.Click += (_, _) =>
        {
            Apply();
            s.Save();
            if (AppPaths.FindPythonBotFolder(s.PythonBotFolder) is { } bot)
                try { HuntService.SyncBotConfig(bot, s.SwitchIP, s.SwitchPort); HuntService.WriteBotOverrides(bot); } catch { }
            WebServer.Apply();
            Close();
        };
        var bottom = new Border
        {
            Background = Skin.B(Skin.Panel), Padding = new Thickness(12, 8),
            Child = new DockPanel { Children = { new StackPanel { [DockPanel.DockProperty] = Dock.Right, Children = { save } }, Skin.Wrap(test, backups, links, portable) } },
        };
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new DockPanel { Children = { bottom, new ScrollViewer { Content = body } } };
    }

    private static TextBlock Label(string t) { var l = Skin.T(t, 13, Skin.Text, wrap: false); l.Width = 290; l.VerticalAlignment = VerticalAlignment.Center; return l; }

    private void Apply() { foreach (var a in _apply) a(); }

    /// <summary>A copy that runs from a flash drive / external SSD and keeps its settings there (portable.flag next to the program).</summary>
    private async Task CreatePortableCopy()
    {
        if (await Dialogs.PickFolder(this, "Choose the drive/folder for the portable copy (e.g. your flash drive)") is not { } folder) return;
        var dest = Path.Combine(folder, "ShinyForge Portable");
        try
        {
            // macOS: copy the whole ShinyForge.app bundle; elsewhere the program folder.
            var appBundle = OsInfo.IsMac ? Path.GetFullPath(Path.Combine(AppPaths.AppDir, "..", "..")) : null;
            var src = appBundle ?? AppPaths.AppDir;
            var target = appBundle != null ? Path.Combine(dest, Path.GetFileName(appBundle.TrimEnd(Path.DirectorySeparatorChar))) : dest;
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(src, file);
                    if (rel.StartsWith("Data" + Path.DirectorySeparatorChar) || rel == "portable.flag") continue;
                    var t = Path.Combine(target, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                    File.Copy(file, t, true);
                    if (!OsInfo.IsWindows) File.SetUnixFileMode(t, File.GetUnixFileMode(file));
                }
            });
            File.WriteAllText(Path.Combine(dest, "portable.flag"), "ShinyForge portable mode: settings and data are stored in the Data folder next to the program.");
            Directory.CreateDirectory(Path.Combine(dest, "Data"));
            if (File.Exists(AppPaths.SettingsFile)) File.Copy(AppPaths.SettingsFile, Path.Combine(dest, "Data", "settings.json"), true);
            await Dialogs.Info(this, $"Portable copy created:\n{dest}\n\nStart ShinyForge from there.");
            OsInfo.OpenFolder(dest);
        }
        catch (Exception e) { await Dialogs.Info(this, e.Message, "Portable copy"); }
    }
}
