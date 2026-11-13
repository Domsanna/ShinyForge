using System.ComponentModel;
using System.Diagnostics;
using ShinyForge.Core;

namespace ShinyForge.UI;

public sealed class SettingsForm : Form
{
    /// <summary>The editable subset of AppSettings, with descriptions for the property grid.</summary>
    private sealed class View
    {
        [Category("Switch"), DisplayName("Switch IP address"), Description("IP of the modded Switch running sys-botbase.")]
        public string SwitchIP { get => AppSettings.Current.SwitchIP; set => AppSettings.Current.SwitchIP = value.Trim(); }
        [Category("Switch"), DisplayName("sys-botbase port")]
        public int SwitchPort { get => AppSettings.Current.SwitchPort; set => AppSettings.Current.SwitchPort = value; }
        [Category("3DS"), DisplayName("3DS IP address"), Description("Shown in Rosalina's menu. Asked once each time ShinyForge starts and remembered until it closes (not saved). Change it here if the 3DS gets a new IP.")]
        public string ThreeDSIP { get => AppSettings.Current.ThreeDSIP; set => AppSettings.Current.ThreeDSIP = value.Trim(); }
        [Category("Paths"), DisplayName("Pokédex spreadsheets folder"), Description("Folder containing the Gen 1 ... Gen 9 tracker workbooks.")]
        public string SpreadsheetRoot { get => AppSettings.Current.SpreadsheetRoot; set => AppSettings.Current.SpreadsheetRoot = value; }
        [Category("Paths"), DisplayName("3DS Capture program")]
        public string Capture3DSPath { get => AppSettings.Current.Capture3DSPath; set => AppSettings.Current.Capture3DSPath = value; }
        [Category("Paths"), DisplayName("Backup Tools folder"), ReadOnly(true),
         Description("Programs not found on this PC are looked for in this folder (open it from Tools & Updates).")]
        public string BackupToolsFolder => AppPaths.BackupToolsDir;
        [Category("Paths"), DisplayName("Streamlabs Desktop")]
        public string StreamlabsPath { get => AppSettings.Current.StreamlabsPath; set => AppSettings.Current.StreamlabsPath = value; }
        [Category("Paths"), DisplayName("FFmpeg (blank = auto-detect)")]
        public string FfmpegPath { get => AppSettings.Current.FfmpegPath; set => AppSettings.Current.FfmpegPath = value; }
        [Category("Paths"), DisplayName("PKHeX.exe (blank = auto-detect)")]
        public string PKHeXPath { get => AppSettings.Current.PKHeXPath; set => AppSettings.Current.PKHeXPath = value; }
        [Category("Bots"), DisplayName("Python hunting bot folder")]
        public string PythonBotFolder { get => AppSettings.Current.PythonBotFolder; set => AppSettings.Current.PythonBotFolder = value; }
        [Category("Bots"), DisplayName("SysBot.exe")]
        public string SysBotPath { get => AppSettings.Current.SysBotPath; set => AppSettings.Current.SysBotPath = value; }
        [Category("Updates"), DisplayName("Check for updates every start")]
        public bool CheckUpdatesOnStart { get => AppSettings.Current.CheckUpdatesOnStart; set => AppSettings.Current.CheckUpdatesOnStart = value; }

        private static AppSettings S => AppSettings.Current;
        [Category("Notifications (phone / Discord)"), DisplayName("Discord webhook URL"),
         Description("Discord: channel settings → Integrations → Webhooks → New Webhook → Copy URL. Paste it here.")]
        public string DiscordWebhookUrl { get => S.DiscordWebhookUrl; set => S.DiscordWebhookUrl = value.Trim(); }
        [Category("Notifications (phone / Discord)"), DisplayName("Discord: @everyone on a shiny")]
        public bool DiscordMentionEveryone { get => S.DiscordMentionEveryone; set => S.DiscordMentionEveryone = value; }
        [Category("Notifications (phone / Discord)"), DisplayName("ntfy topic (phone push)"),
         Description("Free phone push: install the 'ntfy' app (Android / iPhone), subscribe to a topic name only you know " +
                     "(e.g. shinyforge-7f3k2q) and type the same name here.")]
        public string NtfyTopic { get => S.NtfyTopic; set => S.NtfyTopic = value.Trim(); }
        [Category("Notifications (phone / Discord)"), DisplayName("ntfy server")]
        public string NtfyServer { get => S.NtfyServer; set => S.NtfyServer = value.Trim(); }
        [Category("Notifications (phone / Discord)"), DisplayName("Notify: shiny / stop-rule found")]
        public bool NotifyShiny { get => S.NotifyShiny; set => S.NotifyShiny = value; }
        [Category("Notifications (phone / Discord)"), DisplayName("Notify: problems (system check)")]
        public bool NotifyProblems { get => S.NotifyProblems; set => S.NotifyProblems = value; }
        [Category("Notifications (phone / Discord)"), DisplayName("Notify: warnings too")]
        public bool NotifyWarnings { get => S.NotifyWarnings; set => S.NotifyWarnings = value; }

        [Category("Safety"), DisplayName("Keep the PC awake while hunting / recording")]
        public bool KeepAwake { get => S.KeepAwake; set => S.KeepAwake = value; }
        [Category("Safety"), DisplayName("Automatic backups"), Description("Copies saves and tracker spreadsheets (Data\\Backups) before ShinyForge reads or changes them.")]
        public bool AutoBackups { get => S.AutoBackups; set => S.AutoBackups = value; }
        [Category("Safety"), DisplayName("Backups to keep per save file")]
        public int BackupsToKeepSaves { get => S.BackupsToKeepSaves; set => S.BackupsToKeepSaves = Math.Clamp(value, 1, 500); }
        [Category("Safety"), DisplayName("Backups to keep per spreadsheet"), Description("Spreadsheets are big (up to ~235 MB each) - keep this small.")]
        public int BackupsToKeepSheets { get => S.BackupsToKeepSheets; set => S.BackupsToKeepSheets = Math.Clamp(value, 1, 50); }

        [Category("Shiny clips"), DisplayName("Save a clip of every shiny"),
         Description("While a hunt runs, the capture device you chose in 'Record Gameplay' is recorded in a short rolling buffer; " +
                     "when a shiny appears that buffer is saved as a video (only when you're not already recording).")]
        public bool ShinyClips { get => S.ShinyClips; set => S.ShinyClips = value; }
        [Category("Shiny clips"), DisplayName("Clip length (seconds)")]
        public int ShinyClipSeconds { get => S.ShinyClipSeconds; set => S.ShinyClipSeconds = Math.Clamp(value, 20, 300); }

        [Category("Stream overlay & phone remote"), DisplayName("Stream overlay"),
         Description("Streamlabs/OBS: add a 'Browser Source' with the overlay address (button below), or 'Text (read from file)' sources from Data\\Overlay.")]
        public bool OverlayEnabled { get => S.OverlayEnabled; set => S.OverlayEnabled = value; }
        [Category("Stream overlay & phone remote"), DisplayName("Phone remote (home network)"),
         Description("Control/watch the hunt from your phone's browser on the same WiFi. Protected by the PIN. Windows may ask to allow ShinyForge through the firewall - allow it for private networks.")]
        public bool RemoteEnabled { get => S.RemoteEnabled; set => S.RemoteEnabled = value; }
        [Category("Stream overlay & phone remote"), DisplayName("Port")]
        public int WebPort { get => S.WebPort; set => S.WebPort = Math.Clamp(value, 1024, 65535); }
        [Category("Stream overlay & phone remote"), DisplayName("Remote PIN")]
        public string RemotePin { get => Services.WebServer.Pin; set => S.RemotePin = value.Trim(); }
    }

    public SettingsForm()
    {
        Text = "ShinyForge - Settings";
        BackColor = Theme.Bg;
        Size = new Size(860, 760);
        StartPosition = FormStartPosition.CenterParent;
        var grid = new PropertyGrid
        {
            SelectedObject = new View(), Location = new Point(16, 16), Size = new Size(812, 520),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            ViewBackColor = Theme.Panel, ViewForeColor = Theme.Text, LineColor = Theme.PanelHi, CategoryForeColor = Theme.Gold,
            HelpBackColor = Theme.Panel, HelpForeColor = Theme.Muted, BackColor = Theme.Bg, CategorySplitterColor = Theme.Border,
            ToolbarVisible = false, PropertySort = PropertySort.Categorized,
        };
        var save = new FancyButton("Save", Theme.GlyphCheck, Theme.Accent) { Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        save.Location = new Point(720, 668);
        save.Click += (_, _) =>
        {
            var s = AppSettings.Current;
            s.Save();
            // Keep the Python hunter's own config.yaml pointing at the same Switch.
            var bot = AppPaths.FindPythonBotFolder(s.PythonBotFolder);
            if (bot != null) try { Services.HuntService.SyncBotConfig(bot, s.SwitchIP, s.SwitchPort); Services.HuntService.WriteBotOverrides(bot); } catch { }
            Services.WebServer.Apply();
            Close();
        };

        // second row of tool buttons
        var test = new FancyButton("Test notification", Theme.GlyphSparkle, Theme.PanelHi) { Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        test.Location = new Point(16, 610);
        test.Click += async (_, _) =>
        {
            AppSettings.Current.Save();
            var r = await Services.NotifyService.SendAsync("ShinyForge test", "Notifications are working! ✨", important: false);
            MessageBox.Show(this, r, "Test notification");
        };
        var backups = new FancyButton("Backups...", Theme.GlyphSave, Theme.PanelHi) { Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        backups.Location = new Point(test.Right + 12, 610);
        backups.Click += (_, _) => { using var f = new BackupsForm(); f.ShowDialog(this); };
        var links = new FancyButton("Overlay / remote links...", Theme.GlyphWifi, Theme.PanelHi) { Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        links.Location = new Point(backups.Right + 12, 610);
        links.Click += (_, _) =>
        {
            AppSettings.Current.Save();
            Services.WebServer.Apply();
            var text = $"Stream overlay (Streamlabs/OBS → Browser Source, 520 x 140):\n{Services.WebServer.OverlayUrl}\n" +
                       $"Text files for Text sources: {Services.WebServer.OverlayDir}\n\n" +
                       $"Phone remote (open on your phone, same WiFi):\n{Services.WebServer.RemoteUrl}\n\n" +
                       (AppSettings.Current.OverlayEnabled || AppSettings.Current.RemoteEnabled ? "" : "Both are switched OFF - turn them on above first.\n\n") +
                       "Copy the addresses with Ctrl+C.";
            MessageBox.Show(this, text, "Overlay / remote");
        };
        Controls.AddRange(new Control[] { test, backups, links });

        var install = new FancyButton(AppPaths.IsPortable ? "Install on this PC" : "Reinstall / repair", "", Theme.PanelHi) { Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        install.Location = new Point(16, 668);
        install.Click += (_, _) => InstallToPc();
        var portable = new FancyButton("Create portable copy...", "", Theme.PanelHi) { Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        portable.Location = new Point(install.Right + 12, 668);
        portable.Click += (_, _) => CreatePortableCopy();
        var info = Theme.Label("Portable copies run from a flash drive / external SSD and keep their settings on that drive.", Theme.Small, Theme.Muted);
        info.Location = new Point(18, 550);
        info.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.AddRange(new Control[] { grid, info, install, portable, save });
    }

    private static void CopyApp(string dest)
    {
        foreach (var file in Directory.EnumerateFiles(AppPaths.AppDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(AppPaths.AppDir, file);
            if (rel.StartsWith("Data" + Path.DirectorySeparatorChar) || rel == "portable.flag") continue;
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private void CreatePortableCopy()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose the drive/folder for the portable copy (e.g. your flash drive)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var dest = Path.Combine(dlg.SelectedPath, "ShinyForge Portable");
        try
        {
            CopyApp(dest);
            File.WriteAllText(Path.Combine(dest, "portable.flag"), "ShinyForge portable mode: settings and data are stored in the Data folder next to the program.");
            Directory.CreateDirectory(Path.Combine(dest, "Data"));
            File.Copy(AppPaths.SettingsFile, Path.Combine(dest, "Data", "settings.json"), true);
            MessageBox.Show(this, $"Portable copy created:\n{dest}\n\nRun ShinyForge.exe from there.", "ShinyForge");
            Process.Start("explorer.exe", dest);
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Portable copy", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    /// <summary>Installs for the current user (no admin needed) with Start menu and desktop shortcuts.</summary>
    private void InstallToPc()
    {
        var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ShinyForge");
        if (MessageBox.Show(this, $"Install ShinyForge to\n{dest}\nand create Start menu and desktop shortcuts?", "Install", MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        try
        {
            if (!string.Equals(Path.GetFullPath(dest).TrimEnd('\\'), Path.GetFullPath(AppPaths.AppDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                CopyApp(dest);
            var exe = Path.Combine(dest, "ShinyForge.exe");
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ShinyForge.lnk"), exe);
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ShinyForge.lnk"), exe);
            MessageBox.Show(this, "ShinyForge is installed. Use the new Start menu / desktop shortcut from now on.", "Install");
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Install", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static void CreateShortcut(string lnk, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic sc = shell.CreateShortcut(lnk);
        sc.TargetPath = target;
        sc.WorkingDirectory = Path.GetDirectoryName(target);
        sc.IconLocation = target + ",0";
        sc.Description = "ShinyForge";
        sc.Save();
    }
}
