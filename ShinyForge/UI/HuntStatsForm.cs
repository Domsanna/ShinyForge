using System.Drawing.Drawing2D;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>Hunt statistics: counter, odds, chance by now, rate, ETA, best IVs, natures, species, shinies and a 24-hour chart.</summary>
public sealed class HuntStatsForm : Form
{
    private readonly ComboBox _source = new() { Width = 420 };
    private readonly CheckBox _charm = new() { Text = "Shiny Charm", AutoSize = true };
    private readonly Label _big = new() { AutoSize = true, Font = new Font("Segoe UI", 30f, FontStyle.Bold), ForeColor = Theme.Text };
    private readonly Label _sub = new() { AutoSize = true, ForeColor = Theme.Gold, Font = Theme.H2 };
    private readonly Label _details = new() { AutoSize = true, ForeColor = Theme.Text, Font = Theme.Body, MaximumSize = new Size(560, 0) };
    private readonly Label _side = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Body, MaximumSize = new Size(330, 0) };
    private readonly Panel _chart = new() { Height = 170, Dock = DockStyle.Bottom };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10_000 };
    private List<HuntStats.Source> _sources = new();
    private HuntStatsResult? _r;

    public HuntStatsForm(string? preferSource = null)
    {
        Text = "ShinyForge - hunt statistics";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Size = new Size(980, 640);
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterParent;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(12, 10, 12, 4), BackColor = Theme.Panel };
        Theme.StyleInput(_source);
        _charm.ForeColor = Theme.Text;
        _charm.Margin = new Padding(14, 6, 0, 0);
        _charm.Checked = WebServer.ShinyCharm;
        var refresh = new FancyButton("Refresh", Theme.GlyphSync, Theme.PanelHi) { Height = 32 };
        var log = new FancyButton("Open log", Theme.GlyphFolder, Theme.PanelHi) { Height = 32 };
        top.Controls.AddRange(new Control[] { _source, _charm, refresh, log });

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18, 10, 18, 4) };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        left.Controls.AddRange(new Control[] { _big, _sub, _details });
        main.Controls.Add(left, 0, 0);
        main.Controls.Add(_side, 1, 0);

        _chart.Paint += DrawChart;
        _chart.BackColor = Theme.Panel;
        Controls.Add(main);
        Controls.Add(_chart);
        Controls.Add(top);

        _sources = HuntStats.Sources();
        foreach (var s in _sources) _source.Items.Add(s.Name);
        if (_sources.Count == 0) _source.Items.Add("No hunt logs yet - start a hunt first");
        var idx = preferSource == null ? -1 : _sources.FindIndex(s => s.Name.Contains(preferSource, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 && HuntStats.Active() is { } a) idx = _sources.FindIndex(s => s.Name == a.Name);
        _source.SelectedIndex = Math.Max(0, idx);

        _source.SelectedIndexChanged += (_, _) => Fill();
        _charm.CheckedChanged += (_, _) => { WebServer.ShinyCharm = _charm.Checked; Fill(); };
        refresh.Click += (_, _) => Fill();
        log.Click += (_, _) =>
        {
            if (Current() is { } s) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(s.CsvPath) { UseShellExecute = true }); } catch { }
        };
        _timer.Tick += (_, _) => Fill();
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
        Shown += (_, _) => Fill();
    }

    private HuntStats.Source? Current() => _source.SelectedIndex >= 0 && _source.SelectedIndex < _sources.Count ? _sources[_source.SelectedIndex] : null;

    private void Fill()
    {
        if (Current() is not { } src) { _big.Text = "No hunt yet"; return; }
        HuntStatsResult r;
        try { r = HuntStats.Compute(src, _charm.Checked); }
        catch (Exception ex) { _big.Text = "Can't read the log"; _details.Text = ex.Message; return; }
        _r = r;
        _big.Text = $"{r.Phase:N0} encounters";
        _sub.Text = r.Milestone;
        var eta = r.Eta90 is { } e ? $"{(int)e.TotalHours}h {e.Minutes:00}m" : r.Phase >= r.To90 ? "already past 90 %" : "-";
        _details.Text =
            $"Current hunt (since the last shiny{(r.PhaseStart is { } ps ? $", started {ps:yyyy-MM-dd HH:mm}" : "")})\n\n" +
            $"Shiny odds per encounter:  1 in {Math.Round(1 / r.Odds):N0}{(r.ShinyCharm ? " (Shiny Charm)" : "")}  (Generation {r.Gen})\n" +
            $"Chance you'd have found one by now:  {r.ChanceByNow:P1}\n" +
            $"50 % of hunters find it by {r.To50:N0} encounters, 90 % by {r.To90:N0}\n" +
            $"Speed:  {(r.PerHour > 0 ? $"{r.PerHour:0} encounters / hour" : "- (not hunting right now)")}      Time to 90 %:  {eta}\n\n" +
            (r.BestIv is { } b ? $"Best IVs this hunt:  {b.IvTotal}/186 ({string.Join("/", b.Ivs)}) - {b.Species}, {b.Nature} at {b.Time:MM-dd HH:mm}\n" : "") +
            (r.Last is { } l ? $"Last encounter:  {l.Species}, {l.Nature}, IVs {l.IvTotal}/186 at {l.Time:HH:mm:ss}\n" : "") +
            "\n" + HuntStats.AllTimeText(r);
        _side.Text =
            "Species this hunt\n" + string.Join("\n", r.Species.Select(s => $"  {s.Name}  ×{s.Count:N0}")) + "\n\n" +
            "Most common natures\n" + string.Join("\n", r.Natures.Select(n => $"  {n.Name}  ×{n.Count:N0}")) + "\n\n" +
            "Shinies found\n" + (r.Shinies.Count == 0 ? "  none yet" : string.Join("\n", r.Shinies.TakeLast(8).Select(s => $"  ★ {s.Species} {s.Time:yyyy-MM-dd}")));
        _chart.Invalidate();
    }

    private void DrawChart(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, "Encounters per hour - last 24 hours", Theme.Small, new Point(14, 6), Theme.Muted);
        if (_r == null) return;
        var data = _r.PerHourLast24;
        var max = Math.Max(1, data.Max());
        var area = new Rectangle(14, 28, _chart.Width - 28, _chart.Height - 52);
        var w = area.Width / 24f;
        using var bar = new SolidBrush(Theme.Accent);
        for (int i = 0; i < 24; i++)
        {
            var h = data[i] * (float)area.Height / max;
            g.FillRectangle(bar, area.X + i * w + 2, area.Bottom - h, w - 4, h);
            if (i % 3 == 0)
                TextRenderer.DrawText(g, DateTime.Now.AddHours(-(24 - i)).ToString("HH:00"), Theme.Small, new Point((int)(area.X + i * w), area.Bottom + 4), Theme.Muted);
        }
        TextRenderer.DrawText(g, $"max {max}/h", Theme.Small, new Point(area.Right - 70, 6), Theme.Muted);
    }
}
