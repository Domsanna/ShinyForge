using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>"A new version of ShinyForge is available": what's new, then download + install, view on GitHub, skip or later.</summary>
public sealed class UpdateForm : Form
{
    private readonly Label _progress = Theme.Label("", Theme.Body, Theme.Muted);
    private readonly ProgressBar _bar = new() { Style = ProgressBarStyle.Marquee, Visible = false, Height = 10, Width = 300 };
    /// <summary>True when the update has started and ShinyForge must close.</summary>
    public bool CloseApp { get; private set; }

    public UpdateForm(AppRelease r)
    {
        Text = "ShinyForge - update available";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Size = new Size(760, 600);
        MinimumSize = new Size(620, 460);
        StartPosition = FormStartPosition.CenterParent;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var head = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = Theme.Panel, Padding = new Padding(20, 14, 20, 0) };
        var title = Theme.Label($"ShinyForge {r.VersionText} is available", Theme.Title);
        title.Location = new Point(20, 12);
        var sub = Theme.Label($"You have {AppInfo.Display}.  Released {(r.Published == default ? "" : r.Published.ToLocalTime().ToString("yyyy-MM-dd"))}  •  " +
                              $"download {Mb(r.ZipSize)} - your spreadsheets, saves and settings are kept", Theme.Body, Theme.Muted);
        sub.Location = new Point(22, 60);
        head.Controls.AddRange(new Control[] { title, sub });

        var notes = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            Text = "What's new\r\n\r\n" + (string.IsNullOrWhiteSpace(r.Notes) ? "(no notes for this release)" : Plain(r.Notes)),
        };
        Theme.StyleInput(notes);
        var notesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 8) };
        notesHost.Controls.Add(notes);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 110, Padding = new Padding(16, 8, 16, 8), BackColor = Theme.Panel };
        var install = new FancyButton("Download and install", Theme.GlyphDownload, Theme.Accent);
        var page = new FancyButton("View on GitHub", Theme.GlyphInfo, Theme.PanelHi);
        var skip = new FancyButton("Skip this version", Theme.GlyphStop, Theme.PanelHi);
        var later = new FancyButton("Later", Theme.GlyphBack, Theme.PanelHi);
        _progress.Margin = new Padding(4, 12, 0, 0);
        _bar.Margin = new Padding(4, 18, 0, 0);
        bottom.Controls.AddRange(new Control[] { install, page, skip, later, _progress, _bar });
        bottom.SetFlowBreak(later, true);

        page.Click += (_, _) => { try { Browser.Open(r.PageUrl); } catch { } };
        later.Click += (_, _) => Close();
        skip.Click += (_, _) => { AppSettings.Current.SkippedAppVersion = r.Tag; AppSettings.Current.Save(); Close(); };
        install.Click += async (_, _) =>
        {
            if (HuntService.IsPythonHuntRunning() &&
                MessageBox.Show(this, "A hunt is running. Updating closes ShinyForge (the Python hunter keeps running, and its output is back on the game page afterwards). Continue?",
                    "ShinyForge update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var b in new[] { install, skip, later }) b.Enabled = false;
            _bar.Visible = true;
            try
            {
                CloseApp = await AppUpdateService.InstallAsync(r, s => BeginInvoke(() => _progress.Text = s));
                if (CloseApp) { DialogResult = DialogResult.OK; Close(); }
            }
            catch (Exception ex)
            {
                _bar.Visible = false;
                _progress.Text = "Update failed: " + ex.Message;
                foreach (var b in new[] { install, skip, later }) b.Enabled = true;
                AppPaths.Log("App update failed: " + ex);
            }
        };

        Controls.Add(notesHost);
        Controls.Add(bottom);
        Controls.Add(head);
    }

    private static string Mb(long bytes) => bytes <= 0 ? "" : $"({bytes / 1048576.0:N0} MB)";

    /// <summary>Release notes are Markdown: show them as plain text.</summary>
    public static string Plain(string md) =>
        string.Join("\r\n", md.Replace("\r", "").Split('\n').Select(line =>
        {
            var l = line.Replace("**", "").Replace("`", "");
            var t = l.TrimStart();
            var indent = l.Length - t.Length;
            if (t.StartsWith('#')) return t.TrimStart('#', ' ').ToUpperInvariant();
            if (t.StartsWith("- ") || t.StartsWith("* ")) return new string(' ', indent) + "•  " + t[2..];
            return l;
        }));
}
