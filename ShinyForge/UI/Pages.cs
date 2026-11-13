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

    public GamesPage(bool isSwitch)
    {
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Bg,
            Padding = new Padding(28, 18, 20, 30),
        };
        root.Controls.Add(Theme.Label(isSwitch ? "Choose your Switch game" : "Choose your game", Theme.Title));
        root.Controls.Add(Theme.Label("Tiles marked BOT have an automatic shiny-hunting bot. Every game supports recording, collection tracking and save export.",
            Theme.Body, Theme.Muted));

        var groups = (isSwitch ? GameCatalog.ForSwitch : GameCatalog.ForHandheld).GroupBy(g => g.PlatformLabel);
        foreach (var grp in groups)
        {
            var header = Theme.Label(grp.Key.ToUpperInvariant(), new Font("Segoe UI Semibold", 11f), Theme.Gold);
            header.Margin = new Padding(6, 22, 0, 4);
            root.Controls.Add(header);
            var flow = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(1000, 0), BackColor = Theme.Bg };
            _flows.Add(flow);
            foreach (var game in grp)
            {
                var tile = new GameTile(game);
                tile.Click += (_, _) => Chosen?.Invoke(game);
                flow.Controls.Add(tile);
            }
            root.Controls.Add(flow);
        }
        Controls.Add(root);
        // Wrap tiles to the window width.
        Resize += (_, _) =>
        {
            var w = Math.Max(300, ClientSize.Width - 80);
            foreach (var f in _flows) f.MaximumSize = new Size(w, 0);
        };
    }

    private readonly List<FlowLayoutPanel> _flows = new();
}
