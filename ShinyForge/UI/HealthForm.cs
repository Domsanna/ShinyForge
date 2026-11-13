using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>Shows the result of the program-wide safety checks (HealthMonitor) with "Check now" and "Clear" buttons.</summary>
public sealed class HealthForm : Form
{
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly Label _summary = Theme.Label("", Theme.Body, Theme.Text);

    public HealthForm()
    {
        Text = "ShinyForge - system check";
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 520);
        MinimumSize = new Size(700, 360);
        var top = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Panel };
        var title = Theme.Label("System check", Theme.H2);
        title.Location = new Point(16, 12);
        var help = Theme.Label("Every minute ShinyForge checks everything that's running: hunting bots (same Pokémon repeating, stalls, crashes), " +
                               "recordings, collection monitoring,\nthe Switch / 3DS connections, disk space and its own files. Problems it can fix " +
                               "(stuck hunt, crashed recording) are fixed automatically and listed here.", Theme.Small, Theme.Muted);
        help.Location = new Point(18, 42);
        top.Controls.AddRange(new Control[] { title, help });

        _list.BackColor = Theme.Bg;
        _list.ForeColor = Theme.Text;
        _list.Font = Theme.Body;
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("", 30);
        _list.Columns.Add("Time", 70);
        _list.Columns.Add("Feature", 190);
        _list.Columns.Add("Details", 660);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 6), BackColor = Theme.Panel };
        var check = new FancyButton("Check now", Theme.GlyphSync, Theme.Accent);
        check.Click += async (_, _) => { check.Enabled = false; await Task.Run(HealthMonitor.RunNow); Fill(); check.Enabled = true; };
        var clear = new FancyButton("Clear reported problems", Theme.GlyphCheck, Theme.PanelHi);
        clear.Click += (_, _) => { HealthMonitor.Clear(); Fill(); };
        var log = new FancyButton("Open log", Theme.GlyphFolder, Theme.PanelHi);
        log.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true }); } catch { } };
        _summary.Margin = new Padding(16, 10, 0, 0);
        bottom.Controls.AddRange(new Control[] { check, clear, log, _summary });

        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(top);
        Fill();
    }

    private void Fill()
    {
        _list.Items.Clear();
        var items = HealthMonitor.Items;
        foreach (var i in items)
        {
            var row = new ListViewItem(i.State switch { HealthState.Error => "✖", HealthState.Warning => "!", _ => "✓" });
            row.SubItems.Add(i.When.ToString("HH:mm"));
            row.SubItems.Add(i.Feature);
            row.SubItems.Add(i.Message);
            row.ForeColor = i.State switch { HealthState.Error => Theme.Bad, HealthState.Warning => Theme.Warn, _ => Theme.Good };
            row.ToolTipText = i.Message;
            _list.Items.Add(row);
        }
        _list.ShowItemToolTips = true;
        _summary.Text = items.Count == 0
            ? $"Everything is working normally{(HealthMonitor.LastRun == default ? "." : $" (last check {HealthMonitor.LastRun:HH:mm:ss}).")}"
            : $"{items.Count(i => i.State == HealthState.Error)} error(s), {items.Count(i => i.State == HealthState.Warning)} warning(s).";
        _summary.ForeColor = items.Count == 0 ? Theme.Good : Theme.Warn;
    }
}
