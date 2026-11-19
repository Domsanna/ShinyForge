using System.Drawing.Drawing2D;
using ShinyForge.Core;

namespace ShinyForge.UI;

/// <summary>
/// A game "box art" tile: version-coloured gradient, the game's cover Pokémon, the title in a logo-style lettering and a
/// console badge. If an official logo image is placed in Assets\Logos\&lt;game id&gt;.png it is drawn instead of the text.
/// </summary>
public sealed class GameTile : Control
{
    private static readonly Dictionary<string, Image?> ImageCache = new();
    private bool _hover;
    private float _glow;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    public Game Game { get; }
    public bool Large { get; init; }

    public GameTile(Game game, bool large = false)
    {
        Game = game;
        Large = large;
        Size = large ? new Size(520, 190) : new Size(250, 150);
        Cursor = large ? Cursors.Default : Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Margin = new Padding(10);
        _anim.Tick += (_, _) =>
        {
            _glow = Math.Clamp(_glow + (_hover ? .12f : -.12f), 0, 1);
            if (_glow is 0 or 1) _anim.Stop();
            Invalidate();
        };
    }

    protected override void OnMouseEnter(EventArgs e) { if (!Large) { _hover = true; _anim.Start(); } base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _anim.Start(); base.OnMouseLeave(e); }

    private static readonly string HeavyFamily = PickFamily("Segoe UI Black", "Arial Black", "Segoe UI");

    private static string PickFamily(params string[] names)
    {
        foreach (var n in names)
        {
            try { using var f = new FontFamily(n); return n; }
            catch { /* not installed */ }
        }
        return FontFamily.GenericSansSerif.Name;
    }

    private static Image? Load(string path)
    {
        if (ImageCache.TryGetValue(path, out var img))
            return img;
        try { img = File.Exists(path) ? Image.FromFile(path) : null; }
        catch { img = null; }
        return ImageCache[path] = img;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var lift = _glow * 4;
        var r = new RectangleF(4, 6 - lift, Width - 9, Height - 12);
        var radius = Large ? 20 : 16;

        // Glow / shadow
        if (_glow > 0)
        {
            using var glowPath = Theme.Round(RectangleF.Inflate(r, 3, 3), radius + 3);
            using var glowPen = new Pen(Color.FromArgb((int)(170 * _glow), Theme.Gold), 3f);
            g.DrawPath(glowPen, glowPath);
        }

        using var path = Theme.Round(r, radius);
        using (var brush = new LinearGradientBrush(r, Game.Color1, Game.Color2, 35f))
            g.FillPath(brush, path);

        g.SetClip(path);
        // Diagonal shine band
        using (var shine = new LinearGradientBrush(r, Color.FromArgb(70, Color.White), Color.FromArgb(0, Color.White), 60f))
            g.FillPolygon(shine, new[] { new PointF(r.Left, r.Top), new PointF(r.Left + r.Width * .55f, r.Top), new PointF(r.Left + r.Width * .25f, r.Bottom), new PointF(r.Left, r.Bottom) });
        // Pokéball-like ring watermark
        using (var ringPen = new Pen(Color.FromArgb(38, Color.White), Large ? 16 : 10))
        {
            var d = r.Height * 1.25f;
            g.DrawEllipse(ringPen, r.Right - d * .78f, r.Top - d * .15f, d, d);
        }
        // Cover Pokémon
        var art = Load(Path.Combine(AppPaths.ArtDir, $"{Game.Mascot}.png"));
        if (art != null)
        {
            var h = r.Height * (Large ? 1.0f : .95f);
            var w = h * art.Width / art.Height;
            g.DrawImage(art, r.Right - w * .92f, r.Bottom - h + lift * .5f, w, h);
        }
        g.ResetClip();

        // Title (official logo image if provided)
        var logo = Load(Path.Combine(AppPaths.LogoDir, $"{Game.Id}.png"));
        var textArea = new RectangleF(r.Left + 16, r.Top + (Large ? 44 : 34), r.Width * .62f, r.Height - (Large ? 60 : 44));
        if (logo != null)
        {
            var scale = Math.Min(textArea.Width / logo.Width, textArea.Height / logo.Height);
            g.DrawImage(logo, textArea.Left, textArea.Top, logo.Width * scale, logo.Height * scale);
        }
        else
        {
            DrawLogoText(g, textArea);
        }

        // Badge: console + generation
        var badge = Game.Badge;
        using var bf = new Font("Segoe UI Semibold", Large ? 9f : 7.5f);
        var bs = g.MeasureString(badge, bf);
        var br = new RectangleF(r.Left + 14, r.Top + 12, bs.Width + 14, bs.Height + 4);
        using (var bp = Theme.Round(br, br.Height / 2))
        using (var bb = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.FillPath(bb, bp);
        g.DrawString(badge, bf, Brushes.White, br.Left + 7, br.Top + 2);

        if (Game.Bot != BotKind.None)
        {
            const string tag = "BOT";
            using var tf = new Font("Segoe UI", Large ? 9f : 7.5f, FontStyle.Bold);
            var ts = g.MeasureString(tag, tf);
            var tr = new RectangleF(r.Right - ts.Width - 26, r.Top + 12, ts.Width + 14, ts.Height + 4);
            using var tp = Theme.Round(tr, tr.Height / 2);
            using var tb = new SolidBrush(Theme.Gold);
            g.FillPath(tb, tp);
            g.DrawString(tag, tf, Brushes.Black, tr.Left + 7, tr.Top + 2);
        }
    }

    /// <summary>"POKéMON" over the version name, with an outline and drop shadow, like a box logo.</summary>
    private void DrawLogoText(Graphics g, RectangleF area)
    {
        var small = Large ? 13f : 9f;
        var titleSize = Large ? 34f : 21f;
        var familyName = HeavyFamily;
        using var pokeFont = new Font(familyName, small, FontStyle.Bold);
        using var titleFamily = new FontFamily(familyName);

        g.DrawString("POKéMON", pokeFont, new SolidBrush(Color.FromArgb(230, Theme.Gold)), area.Left + 1, area.Top);

        var text = Game.Title.ToUpperInvariant();
        float size = titleSize;
        using (var test = new Font(titleFamily, size, FontStyle.Bold))
        {
            var w = g.MeasureString(text, test).Width;
            if (w > area.Width) size *= area.Width / w;
        }
        using var gp = new GraphicsPath();
        gp.AddString(text, titleFamily, (int)FontStyle.Bold, size * g.DpiY / 72f, new PointF(area.Left, area.Top + small * 1.7f), StringFormat.GenericTypographic);
        using (var shadow = (GraphicsPath)gp.Clone())
        {
            using var m = new Matrix();
            m.Translate(2, 3);
            shadow.Transform(m);
            using var sb = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            g.FillPath(sb, shadow);
        }
        using (var outline = new Pen(Color.FromArgb(200, 15, 12, 30), Large ? 6 : 4) { LineJoin = LineJoin.Round })
            g.DrawPath(outline, gp);
        var bounds = gp.GetBounds();
        using (var fill = new LinearGradientBrush(bounds.IsEmpty ? area : bounds, Color.White, Theme.Lerp(Color.White, Game.Color1, .35f), 90f))
            g.FillPath(fill, gp);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }
}
