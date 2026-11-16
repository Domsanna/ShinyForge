using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// The program-wide safety checks (HealthMonitor). Selecting a problem shows ShinyForge's diagnosis with step-by-step
/// solutions and, when it has one, a fix it can try itself. After that the problem can be reported to GitHub.
/// Minor problems (they don't affect hunting/playing) are hidden unless "Show minor issues" is ticked.
/// </summary>
public sealed class HealthForm : Form
{
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = false };
    private readonly Label _summary = Theme.Label("", Theme.Body, Theme.Text);
    private readonly CheckBox _minor = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(16, 12, 0, 0) };
    private readonly Label _cause = Theme.Label("", Theme.Body, Theme.Text, autoSize: false);
    private readonly Label _steps = Theme.Label("", Theme.Small, Theme.Muted, autoSize: false);
    private readonly Label _result = Theme.Label("", Theme.Small, Theme.Good);
    private readonly FancyButton _fix = new("Try the fix", Theme.GlyphTools, Theme.Accent);
    private readonly FancyButton _report = new("Report to GitHub", Theme.GlyphInfo, Theme.PanelHi);
    /// <summary>Fix results per problem (feature|message), so the report says what was already tried.</summary>
    private readonly Dictionary<string, string> _fixResults = new();
    private HealthItem? _item;
    private Diagnosis? _diag;

    public HealthForm()
    {
        Text = "ShinyForge - system check";
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 680);
        MinimumSize = new Size(760, 520);
        var top = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Panel };
        var title = Theme.Label("System check", Theme.H2);
        title.Location = new Point(16, 12);
        var help = Theme.Label("Every minute ShinyForge checks everything that's running: hunting bots (same Pokémon repeating, stalls, crashes), " +
                               "recordings, collection monitoring,\nthe Switch / 3DS connections, disk space and its own files. Select a problem to see " +
                               "what's wrong, how to solve it, and a fix ShinyForge can try itself.", Theme.Small, Theme.Muted);
        help.Location = new Point(18, 42);
        top.Controls.AddRange(new Control[] { title, help });

        _list.BackColor = Theme.Bg;
        _list.ForeColor = Theme.Text;
        _list.Font = Theme.Body;
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("", 30);
        _list.Columns.Add("Time", 70);
        _list.Columns.Add("Feature", 190);
        _list.Columns.Add("Details", 680);
        _list.ShowItemToolTips = true;
        _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedItems.Count > 0) ShowDiagnosis(_list.SelectedItems[0].Tag as HealthItem); };

        // diagnosis of the selected problem
        var detail = new Panel { Dock = DockStyle.Bottom, Height = 200, BackColor = Theme.Panel, Padding = new Padding(16, 10, 16, 6) };
        _cause.Dock = DockStyle.Top; _cause.Height = 26;
        _steps.Dock = DockStyle.Fill;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, BackColor = Theme.Panel, Padding = new Padding(0, 4, 0, 0) };
        _result.Margin = new Padding(12, 14, 0, 0);
        _result.MaximumSize = new Size(560, 0);
        actions.Controls.AddRange(new Control[] { _fix, _report, _result });
        detail.Controls.Add(_steps);
        detail.Controls.Add(actions);
        detail.Controls.Add(_cause);
        _fix.Click += async (_, _) => await RunFix();
        _report.Click += (_, _) => Report();

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 6), BackColor = Theme.Panel };
        var check = new FancyButton("Check now", Theme.GlyphSync, Theme.Accent);
        check.Click += async (_, _) => { check.Enabled = false; await Task.Run(HealthMonitor.RunNow); Fill(); check.Enabled = true; };
        var clear = new FancyButton("Clear reported problems", Theme.GlyphCheck, Theme.PanelHi);
        clear.Click += (_, _) => { HealthMonitor.Clear(); Fill(); };
        var log = new FancyButton("Open log", Theme.GlyphFolder, Theme.PanelHi);
        log.Click += (_, _) => Open(_minor.Checked && File.Exists(ErrorDoctor.IssuesLog) ? ErrorDoctor.IssuesLog : AppPaths.LogFile);
        _minor.CheckedChanged += (_, _) => Fill();
        _summary.Margin = new Padding(16, 10, 0, 0);
        bottom.Controls.AddRange(new Control[] { check, clear, log, _minor, _summary });

        Controls.Add(_list);
        Controls.Add(detail);
        Controls.Add(bottom);
        Controls.Add(top);
        Fill();
    }

    private static void Open(string file)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true }); } catch { }
    }

    private static string Key(HealthItem i) => i.Feature + "|" + i.Message;

    private void Fill()
    {
        var keep = _item;
        _list.Items.Clear();
        var gameplay = HealthMonitor.Items;
        var minor = HealthMonitor.MinorItems;
        _minor.Text = $"Show minor issues ({minor.Count})";
        foreach (var i in _minor.Checked ? gameplay.Concat(minor) : gameplay)
        {
            var row = new ListViewItem(i.State switch { HealthState.Error => "✖", HealthState.Warning => "!", _ => "✓" }) { Tag = i };
            row.SubItems.Add(i.When.ToString("HH:mm"));
            row.SubItems.Add(i.AffectsGameplay ? i.Feature : i.Feature + " (minor)");
            row.SubItems.Add(i.Message);
            row.ForeColor = !i.AffectsGameplay ? Theme.Muted : i.State switch { HealthState.Error => Theme.Bad, HealthState.Warning => Theme.Warn, _ => Theme.Good };
            row.ToolTipText = i.Message;
            _list.Items.Add(row);
        }
        _summary.Text = gameplay.Count == 0
            ? $"Everything that affects hunting is working{(HealthMonitor.LastRun == default ? "." : $" (last check {HealthMonitor.LastRun:HH:mm:ss}).")}"
            : $"{gameplay.Count(i => i.State == HealthState.Error)} error(s), {gameplay.Count(i => i.State == HealthState.Warning)} warning(s).";
        _summary.ForeColor = gameplay.Count == 0 ? Theme.Good : Theme.Warn;
        var again = _list.Items.Cast<ListViewItem>().FirstOrDefault(r => keep != null && r.Tag is HealthItem h && Key(h) == Key(keep))
                    ?? (_list.Items.Count > 0 ? _list.Items[0] : null);
        if (again != null) { again.Selected = true; ShowDiagnosis((HealthItem)again.Tag!); }
        else ShowDiagnosis(null);
    }

    private void ShowDiagnosis(HealthItem? item)
    {
        _item = item;
        _diag = item == null ? null : ErrorDoctor.Diagnose(item);
        if (item == null || _diag == null)
        {
            _cause.Text = "No problems.";
            _steps.Text = "When something goes wrong it appears in the list above with ShinyForge's diagnosis and a way to fix it.";
            _fix.Visible = _report.Visible = false;
            _result.Text = "";
            return;
        }
        _cause.Text = (item.AffectsGameplay ? "" : "Minor (doesn't affect hunting) - ") + _diag.Cause;
        _steps.Text = "How to solve it:\n" + string.Join("\n", _diag.Steps.Select((s, n) => $"  {n + 1}. {s}"));
        _fix.Visible = _diag.Fix != null;
        if (_diag.FixLabel != null)
        {
            _fix.Text = _diag.FixLabel;
            _fix.Width = TextRenderer.MeasureText(_fix.Text, _fix.Font, Size.Empty, TextFormatFlags.NoPrefix).Width + 58;
        }
        _report.Visible = true;
        _fixResults.TryGetValue(Key(item), out var done);
        _result.Text = done ?? (_diag.Fix != null ? "Try the fix first - if the problem stays, report it." : "");
        _result.ForeColor = done != null ? Theme.Good : Theme.Muted;
    }

    private async Task RunFix()
    {
        if (_item is not { } item || _diag is not { Fix: { } fix } d) return;
        _fix.Enabled = false;
        _result.ForeColor = Theme.Muted;
        _result.Text = "Working on it...";
        string msg;
        try { msg = await fix(); }
        catch (Exception e) { msg = "The fix failed: " + e.Message; }
        AppPaths.Log($"[health] fix '{d.FixLabel}' for {item.Feature}: {msg}");
        _fixResults[Key(item)] = msg;
        _fix.Enabled = true;
        await Task.Run(HealthMonitor.RunNow);
        if (IsDisposed) return;
        Fill();
        if (_item == null || Key(_item) != Key(item)) { _result.Text = msg; _result.ForeColor = Theme.Good; }
    }

    private void Report()
    {
        if (_item is not { } item || _diag is not { } d) return;
        _fixResults.TryGetValue(Key(item), out var fixResult);
        if (d.Fix != null && fixResult == null &&
            MessageBox.Show(this, $"ShinyForge can try to fix this itself (\"{d.FixLabel}\"). Try that before reporting it?", "Report to GitHub",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _ = RunFix();
            return;
        }
        if (MessageBox.Show(this, ErrorDoctor.ReportPrompt(item, d, fixResult), "Report to GitHub", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            return;
        DependencyService.OpenUrl(ErrorDoctor.ReportUrl(item, d, fixResult));
        AppPaths.Log($"[health] opened a GitHub report for {item.Feature}");
        _result.ForeColor = Theme.Good;
        _result.Text = "Opened the report in your browser - press \"Create\" there to send it. Thank you!";
    }
}
