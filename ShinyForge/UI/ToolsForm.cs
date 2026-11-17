using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>Lists every outside program ShinyForge uses (installed / missing / update available) with install and update buttons.</summary>
public sealed class ToolsForm : Form
{
    public static List<DependencyService.UpdateInfo> LastUpdates { get; set; } = new();
    private readonly FlowLayoutPanel _list;
    private readonly Label _status = Theme.Label("", Theme.Body, Theme.Muted);

    public ToolsForm(bool? isSwitch)
    {
        Text = "ShinyForge - Tools & Updates";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Size = new Size(860, 680);
        StartPosition = FormStartPosition.CenterParent;
        Icon = Owner?.Icon;
        var title = Theme.Label("Tools & Updates", Theme.Title);
        title.Location = new Point(20, 14);
        _list = new FlowLayoutPanel
        {
            Location = new Point(20, 70), Size = new Size(805, 510), FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = Theme.Bg, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        var recheck = new FancyButton("Check for updates", Theme.GlyphSync, Theme.Accent) { Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        recheck.Location = new Point(20, 592);
        recheck.Click += async (_, _) =>
        {
            _status.Text = "Checking...";
            LastUpdates = await DependencyService.CheckUpdates();
            Populate(isSwitch);
            _status.Text = LastUpdates.Count == 0 ? "No updates found." : $"{LastUpdates.Count} update(s) available.";
        };
        var installAll = new FancyButton("Install all missing", Theme.GlyphDownload, Theme.Accent) { Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        installAll.Location = new Point(recheck.Right + 12, 592);
        installAll.Click += (_, _) => { InstallAll(this, isSwitch); Populate(isSwitch); };
        _status.Location = new Point(installAll.Right + 16, 600);
        _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.AddRange(new Control[] { title, _list, recheck, installAll, _status });
        Populate(isSwitch);
        // the installed versions (some programs are asked, which takes a moment): filled in when known
        Shown += async (_, _) => { await DependencyService.ReadVersions(); if (!IsDisposed) Populate(isSwitch); };
    }

    private void Populate(bool? isSwitch)
    {
        _list.Controls.Clear();

        // ShinyForge itself: version, build and the newest release on GitHub.
        var app = new Panel { Size = new Size(770, 70), BackColor = Color.FromArgb(36, 30, 70), Margin = new Padding(0, 0, 0, 12) };
        var aIcon = Theme.Label(Theme.GlyphSparkle, Theme.Icons, Theme.Gold);
        aIcon.Location = new Point(14, 22);
        var latest = AppUpdateService.Latest;
        var aName = Theme.Label($"ShinyForge {AppInfo.Display}   —   " +
                                (latest == null ? "installed" : latest.IsNewer ? $"{latest.VersionText} available" : "up to date"), Theme.H2, Theme.Text);
        aName.Location = new Point(48, 8);
        var aInfo = Theme.Label("New versions are published on GitHub: " + AppInfo.ReleasesUrl, Theme.Small, Theme.Muted);
        aInfo.Location = new Point(50, 38);
        aInfo.MaximumSize = new Size(570, 30);
        var aCheck = new FancyButton("Check now", Theme.GlyphSync, Theme.Accent) { Location = new Point(620, 16), Height = 36 };
        aCheck.Click += async (_, _) =>
        {
            if (Owner is MainForm main && await main.CheckAppUpdate(quiet: false)) { Close(); return; }
            Populate(isSwitch);
        };
        app.Controls.AddRange(new Control[] { aIcon, aName, aInfo, aCheck });
        _list.Controls.Add(app);

        // The Backup Tools folder: checked after the computer for anything missing.
        var backup = new Panel { Size = new Size(770, 70), BackColor = Color.FromArgb(24, 40, 60), Margin = new Padding(0, 0, 0, 12) };
        var bIcon = Theme.Label(Theme.GlyphTools, Theme.Icons, Theme.Accent);
        bIcon.Location = new Point(14, 22);
        var bName = Theme.Label("Backup Tools folder", Theme.H2, Theme.Text);
        bName.Location = new Point(48, 8);
        var bInfo = Theme.Label("Anything not installed on this PC is looked for here (sub-folders too).\n" + AppPaths.BackupToolsDir, Theme.Small, Theme.Muted);
        bInfo.Location = new Point(50, 34);
        bInfo.MaximumSize = new Size(570, 34);
        var open = new FancyButton("Open folder", Theme.GlyphFolder, Theme.PanelHi) { Location = new Point(620, 16), Height = 36 };
        open.Click += (_, _) => OpenBackupFolder();
        backup.Controls.AddRange(new Control[] { bIcon, bName, bInfo, open });
        _list.Controls.Add(backup);

        foreach (var d in DependencyService.All.Where(d => isSwitch is null || (isSwitch.Value ? d.ForSwitch : d.For3DS)))
        {
            if (d.State != DepState.UpdateAvailable) DependencyService.Refresh(d);
            var row = new Panel { Size = new Size(770, 70), BackColor = Theme.Panel, Margin = new Padding(0, 0, 0, 8) };
            var (glyph, color, state) = d.State switch
            {
                DepState.Ok => (Theme.GlyphCheck, Theme.Good, d.FromBackup ? "Using Backup Tools folder" : "Installed"),
                DepState.UpdateAvailable => (Theme.GlyphDownload, Theme.Warn, "Update available"),
                _ => (Theme.GlyphWarn, Theme.Bad, "Not installed"),
            };
            var icon = Theme.Label(glyph, Theme.Icons, color);
            icon.Location = new Point(14, 22);
            var name = Theme.Label($"{d.Name}{(d.Path != null && d.Version != null ? "  " + d.Version : "")}   —   {state}", Theme.H2, Theme.Text);
            name.Location = new Point(48, 8);
            var purpose = Theme.Label(d.Purpose + (d.State == DepState.Ok ? $"\n{d.Detail}" : ""), Theme.Small, Theme.Muted);
            purpose.Location = new Point(50, 34);
            purpose.MaximumSize = new Size(560, 34);
            row.Controls.AddRange(new Control[] { icon, name, purpose });
            if (d.State == DepState.Missing)
            {
                var install = new FancyButton("Install", Theme.GlyphDownload, Theme.Accent) { Location = new Point(640, 16), Height = 36 };
                var dep = d;
                install.Click += (_, _) => { AskInstall(this, dep); Populate(isSwitch); };
                row.Controls.Add(install);
            }
            _list.Controls.Add(row);
        }

        foreach (var u in LastUpdates)
        {
            var row = new Panel { Size = new Size(770, 56), BackColor = Color.FromArgb(50, 40, 12), Margin = new Padding(0, 0, 0, 8) };
            var label = Theme.Label($"{u.Item}: {u.Message}", Theme.Body, Theme.Text);
            label.Location = new Point(14, 18);
            label.MaximumSize = new Size(600, 40);
            row.Controls.Add(label);
            if (u.Apply != null)
            {
                var apply = new FancyButton("Update", Theme.GlyphDownload, Theme.Accent) { Location = new Point(640, 10), Height = 36 };
                var upd = u;
                apply.Click += async (_, _) =>
                {
                    apply.Enabled = false;
                    _status.Text = $"Updating {upd.Item}...";
                    try { _status.Text = await upd.Apply(s => BeginInvoke(() => _status.Text = s)); }
                    catch (Exception ex) { _status.Text = ex.Message; }
                };
                row.Controls.Add(apply);
            }
            _list.Controls.Add(row);
        }
    }

    public static void OpenBackupFolder()
    {
        AppPaths.EnsureBackupToolsDir();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.BackupToolsDir}\"") { UseShellExecute = true });
    }

    /// <summary>Runs an install job behind a progress window and shows its result.</summary>
    public static void RunInstall(IWin32Window? owner, string title, Func<Action<string>, Task<string>> job)
    {
        using var progress = new ProgressForm(title);
        progress.Show(owner);
        var task = job(s => progress.SetText(s));
        while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(50); }
        progress.Close();
        MessageBox.Show(owner, task.IsFaulted ? task.Exception!.GetBaseException().Message : task.Result, "ShinyForge");
    }

    /// <summary>Confirms, then installs every missing program for the current platform (or all, if none is chosen).</summary>
    public static void InstallAll(IWin32Window? owner, bool? isSwitch)
    {
        var relevant = DependencyService.All.Where(d => isSwitch is null || (isSwitch.Value ? d.ForSwitch : d.For3DS)).ToList();
        foreach (var d in relevant) DependencyService.Refresh(d);
        var missing = relevant.Where(d => d.State == DepState.Missing && d.Wanted).ToList();
        if (missing.Count == 0)
        {
            MessageBox.Show(owner, "Everything is already installed.", "ShinyForge");
            return;
        }
        var auto = missing.Where(d => d.CanAutoInstall).Select(d => "  • " + d.Name).ToList();
        var manual = missing.Where(d => d.ManualOnly).Select(d => "  • " + d.Name).ToList();
        var text = "Install all missing programs?\n\n" +
                   (auto.Count > 0 ? "Downloaded and installed automatically:\n" + string.Join("\n", auto) + "\n\n" : "") +
                   (manual.Count > 0 ? "Can't be installed automatically (their download pages will open):\n" + string.Join("\n", manual) + "\n\n" : "") +
                   "Windows may ask for permission for some installers - click Yes.";
        if (MessageBox.Show(owner, text, "ShinyForge - install everything", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        RunInstall(owner, "Installing missing programs...", p => DependencyService.InstallAllMissing(isSwitch, p));
    }

    /// <summary>Asks whether to install a missing program automatically or manually, then does it.</summary>
    public static void AskInstall(IWin32Window? owner, Dependency d)
    {
        if (d.Name == "SysBot.exe")
        {
            var r = MessageBox.Show(owner,
                $"SysBot.exe (Sys-EncounterBot.NET) wasn't found.\n\n{d.Purpose}.\n\n" +
                "Yes = download and install the latest official release automatically\n" +
                "No = locate a copy that's already on this PC (use this for a self-built copy with custom bots such as FRLG_EncounterBot)",
                "ShinyForge - missing program", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
                RunInstall(owner, $"Installing {d.Name}...", p => DependencyService.Install(d, p));
            else if (r == DialogResult.No)
            {
                using var dlg = new OpenFileDialog { Filter = "SysBot.exe|SysBot.exe|Programs|*.exe", Title = "Locate SysBot.exe" };
                if (dlg.ShowDialog(owner) == DialogResult.OK)
                {
                    AppSettings.Current.SysBotPath = dlg.FileName;
                    AppSettings.Current.Save();
                }
            }
            return;
        }

        if (d.Name == "3DS Capture")
        {
            var r = MessageBox.Show(owner,
                $"{d.Name} wasn't found.\n\n{d.Purpose}.\n\nYes = locate it on this PC\nNo = open the download page",
                "ShinyForge - missing program", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
            {
                using var dlg = new OpenFileDialog { Filter = "Programs|*.exe", Title = $"Locate {d.Name}" };
                if (dlg.ShowDialog(owner) == DialogResult.OK)
                {
                    AppSettings.Current.Capture3DSPath = dlg.FileName;
                    AppSettings.Current.Save();
                }
            }
            else if (r == DialogResult.No) DependencyService.OpenUrl(d.ManualUrl);
            return;
        }

        var auto = d.CanAutoInstall;
        var choice = MessageBox.Show(owner,
            $"{d.Name} is needed: {d.Purpose}.\n\n" +
            (d.ExeNames.Length > 0 ? $"(You can also put it in the Backup Tools folder:\n{AppPaths.BackupToolsDir})\n\n" : "") +
            (auto ? "Yes = let ShinyForge download and install the latest version automatically\nNo = I'll install it myself (opens the download page)"
                  : "It can't be installed automatically. Open its download page now?"),
            "ShinyForge - install " + d.Name, auto ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (auto && choice == DialogResult.Yes)
            RunInstall(owner, $"Installing {d.Name}...", p => DependencyService.Install(d, p));
        else if ((auto && choice == DialogResult.No) || (!auto && choice == DialogResult.Yes))
            DependencyService.OpenUrl(d.ManualUrl);
    }
}

public sealed class ProgressForm : Form
{
    private readonly Label _label;
    public ProgressForm(string text)
    {
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(520, 140);
        BackColor = Theme.Panel;
        Text = "ShinyForge";
        _label = Theme.Label(text, Theme.Body, Theme.Text, autoSize: false);
        _label.Dock = DockStyle.Fill;
        _label.Padding = new Padding(16);
        var bar = new ProgressBar { Dock = DockStyle.Bottom, Style = ProgressBarStyle.Marquee, Height = 14 };
        Controls.Add(_label);
        Controls.Add(bar);
    }
    public void SetText(string s) { if (IsHandleCreated) BeginInvoke(() => _label.Text = s); }
}
