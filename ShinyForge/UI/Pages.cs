using System.Drawing.Drawing2D;
using ShinyForge.Core;

namespace ShinyForge.UI;

/// <summary>Big clickable card for choosing the system.</summary>
public sealed class PlatformCard : Control
{
    private bool _hover;
    public string Glyph { get; }
    public string Heading { get; }
    public string Sub { get; }
    public Color C1 { get; }
    public Color C2 { get; }
    public ushort[] Art { get; }

    public PlatformCard(string glyph, string heading, string sub, Color c1, Color c2, params ushort[] art)
    {
        (Glyph, Heading, Sub, C1, C2, Art) = (glyph, heading, sub, c1, c2, art);
        Size = new Size(470, 320);
        Cursor = Cursors.Hand;
        Margin = new Padding(20);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var r = new RectangleF(5, _hover ? 2 : 6, Width - 11, Height - 12);
        using var path = Theme.Round(r, 24);
        using (var b = new LinearGradientBrush(r, C1, C2, 45f))
            g.FillPath(b, path);
        if (_hover)
        {
            using var pen = new Pen(Theme.Gold, 3f);
            g.DrawPath(pen, path);
        }
        g.SetClip(path);
        float x = r.Right - 20;
        foreach (var sp in Art.Reverse())
        {
            var file = Path.Combine(AppPaths.ArtDir, $"{sp}.png");
            if (!File.Exists(file)) continue;
            using var img = Image.FromFile(file);
            var h = r.Height * .42f;
            var w = h * img.Width / img.Height;
            x -= w * .72f;
            g.DrawImage(img, x, r.Bottom - h - 4, w, h);
        }
        // Darken the upper area slightly so the text always reads well over the artwork.
        var fadeRect = new RectangleF(r.Left, r.Top, r.Width, r.Height * .62f);
        using (var fade = new LinearGradientBrush(fadeRect, Color.FromArgb(110, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90f))
            g.FillRectangle(fade, fadeRect.Left, fadeRect.Top, fadeRect.Width, fadeRect.Height - 1);
        g.ResetClip();
        TextRenderer.DrawText(g, Glyph, Theme.IconsBig, new Point((int)r.Left + 26, (int)r.Top + 22), Color.White);
        using var hf = new Font("Segoe UI", 24f, FontStyle.Bold);
        TextRenderer.DrawText(g, Heading, hf, new Point((int)r.Left + 24, (int)r.Top + 70), Color.White);
        TextRenderer.DrawText(g, Sub, Theme.Body, new Rectangle((int)r.Left + 28, (int)r.Top + 114, (int)r.Width - 50, 50),
            Color.FromArgb(240, 255, 255, 255), TextFormatFlags.WordBreak);
    }
}

public sealed class PlatformPage : UserControl
{
    public event Action<bool>? Chosen; // true = Switch

    public PlatformPage()
    {
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        var title = Theme.Label("Which system are you playing on?", Theme.Title);
        title.Location = new Point(40, 28);
        var sub = Theme.Label("ShinyForge hunts shinies, records your gameplay, tracks your collection in your Pokédex spreadsheets and backs up your Pokémon.", Theme.Body, Theme.Muted);
        sub.Location = new Point(44, 76);

        var flow = new FlowLayoutPanel { Location = new Point(20, 120), AutoSize = true, BackColor = Theme.Bg };
        var ds = new PlatformCard(Theme.GlyphUsb, "Nintendo 3DS",
            "USB capture  •  Game Boy, Game Boy Color, GBA, DS and 3DS games",
            Color.FromArgb(206, 35, 54), Color.FromArgb(70, 10, 30), 6, 384, 487, 716);
        var sw = new PlatformCard(Theme.GlyphGame, "Nintendo Switch",
            "Capture card + Streamlabs  •  Let's Go to Legends: Z-A, plus FireRed/LeafGreen",
            Color.FromArgb(230, 0, 18), Color.FromArgb(30, 30, 40), 25, 888, 1007, 718);
        ds.Click += (_, _) => Chosen?.Invoke(false);
        sw.Click += (_, _) => Chosen?.Invoke(true);
        flow.Controls.Add(ds);
        flow.Controls.Add(sw);
        Controls.AddRange(new Control[] { title, sub, flow });
    }
}

public sealed class GamesPage : UserControl
{
    public event Action<Game>? Chosen;

    private readonly FlowLayoutPanel _flow;
    private readonly Panel _view = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    private readonly VScrollBar _bar = new() { Dock = DockStyle.Right, Visible = false };
    private int _offset;

    /// <summary>
    /// The game tiles wrap to the window width; the list scrolls exactly as far as its content - the last game is the end.
    /// The scrolling is done here rather than by WinForms' AutoScroll, which kept the height of the first, narrow layout
    /// (one tile per row) and let the list scroll screens past the last game (and recomputing it fought the scrollbar).
    /// </summary>
    public GamesPage(bool isSwitch)
    {
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        _flow = new FlowLayoutPanel
        {
            AutoSize = false, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Location = Point.Empty,
            BackColor = Theme.Bg, Padding = new Padding(28, 18, 20, 30),
        };
        void Break(Control c) { _flow.Controls.Add(c); _flow.SetFlowBreak(c, true); }
        Break(Theme.Label(isSwitch ? "Choose your Switch game" : "Choose your game", Theme.Title));
        Break(Theme.Label("Tiles marked BOT have an automatic shiny-hunting bot. Every game supports recording, collection tracking and save export.",
            Theme.Body, Theme.Muted));

        foreach (var grp in (isSwitch ? GameCatalog.ForSwitch : GameCatalog.ForHandheld).GroupBy(g => g.PlatformLabel))
        {
            var header = Theme.Label(grp.Key.ToUpperInvariant(), new Font("Segoe UI Semibold", 11f), Theme.Gold);
            header.Margin = new Padding(6, 22, 0, 4);
            Break(header);
            GameTile? last = null;
            foreach (var game in grp)
            {
                var tile = new GameTile(game);
                tile.Click += (_, _) => Chosen?.Invoke(game);
                _flow.Controls.Add(tile);
                last = tile;
            }
            if (last != null) _flow.SetFlowBreak(last, true); // the next console starts on a new line
        }
        _view.Controls.Add(_flow);
        Controls.Add(_view);
        Controls.Add(_bar);
        _view.Resize += (_, _) => Fit();
        _bar.Scroll += (_, e) => ScrollTo(e.NewValue);
        // the wheel over a tile reaches the view (Windows passes unhandled wheel messages up to the parent)
        _view.MouseWheel += (_, e) => ScrollTo(_offset - e.Delta);
        MouseWheel += (_, e) => ScrollTo(_offset - e.Delta);
    }

    /// <summary>How far down the list can go: its height at the current width minus what's visible.</summary>
    internal int MaxOffset => Math.Max(0, _flow.Height - _view.ClientSize.Height);
    internal int Offset => _offset;

    /// <summary>Lays the tiles out at the view's width and sizes the list (and the scrollbar) to exactly their height.</summary>
    private void Fit()
    {
        if (_view.ClientSize.Width <= 0) return;
        var width = _view.ClientSize.Width;
        var height = _flow.GetPreferredSize(new Size(width, 0)).Height;
        _flow.SetBounds(0, -_offset, width, height);
        _bar.Visible = height > _view.ClientSize.Height;
        _bar.Minimum = 0;
        _bar.SmallChange = 60;
        _bar.LargeChange = Math.Max(1, _view.ClientSize.Height);
        _bar.Maximum = Math.Max(0, height - 1);
        ScrollTo(_offset);
    }

    internal void ScrollTo(int offset)
    {
        _offset = Math.Clamp(offset, 0, MaxOffset);
        _flow.Top = -_offset;
        if (_bar.Visible) _bar.Value = Math.Min(_offset, Math.Max(0, _bar.Maximum - _bar.LargeChange + 1));
    }
}
