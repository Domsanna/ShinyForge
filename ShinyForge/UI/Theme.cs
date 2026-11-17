using System.Drawing.Drawing2D;

namespace ShinyForge.UI;

public static class Theme
{
    public static readonly Color Bg = Color.FromArgb(14, 13, 22);
    public static readonly Color Panel = Color.FromArgb(26, 24, 40);
    public static readonly Color PanelHi = Color.FromArgb(36, 33, 56);
    public static readonly Color Border = Color.FromArgb(58, 54, 88);
    public static readonly Color Accent = Color.FromArgb(124, 58, 237);
    public static readonly Color Accent2 = Color.FromArgb(24, 190, 200);
    public static readonly Color Gold = Color.FromArgb(255, 214, 102);
    public static readonly Color Text = Color.FromArgb(240, 238, 250);
    public static readonly Color Muted = Color.FromArgb(160, 156, 190);
    public static readonly Color Good = Color.FromArgb(52, 211, 153);
    public static readonly Color Warn = Color.FromArgb(251, 191, 36);
    public static readonly Color Bad = Color.FromArgb(248, 113, 113);

    public static readonly Font Title = new("Segoe UI", 22f, FontStyle.Bold);
    public static readonly Font H2 = new("Segoe UI Semibold", 13f);
    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Mono = new("Consolas", 9f);
    public static readonly Font Icons = MakeIconFont(16f);
    public static readonly Font IconsBig = MakeIconFont(34f);

    private static Font MakeIconFont(float size)
    {
        using var probe = new Font("Segoe Fluent Icons", size);
        return probe.Name == "Segoe Fluent Icons" ? new Font("Segoe Fluent Icons", size) : new Font("Segoe MDL2 Assets", size);
    }

    // Segoe Fluent / MDL2 glyphs
    public const string GlyphBack = "", GlyphSettings = "", GlyphSparkle = "", GlyphRecord = "",
        GlyphSheet = "", GlyphSave = "", GlyphTools = "", GlyphFolder = "", GlyphPlay = "",
        GlyphStop = "", GlyphSync = "", GlyphInfo = "", GlyphGame = "", GlyphCheck = "",
        GlyphWarn = "", GlyphDownload = "", GlyphUsb = "", GlyphWifi = "";

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Lerp(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static Label Label(string text, Font? font = null, Color? color = null, bool autoSize = true) => new()
    {
        Text = text, Font = font ?? Body, ForeColor = color ?? Text, AutoSize = autoSize, BackColor = Color.Transparent,
        UseMnemonic = false, // show "&" as itself ("Tools & Updates"), not as a shortcut underline
    };

    public static void StyleInput(Control c)
    {
        c.BackColor = PanelHi;
        c.ForeColor = Text;
        c.Font = Body;
        if (c is TextBox t) t.BorderStyle = BorderStyle.FixedSingle;
        if (c is ComboBox cb) { cb.FlatStyle = FlatStyle.Flat; cb.DropDownStyle = ComboBoxStyle.DropDownList; }
    }
}

/// <summary>Rounded, flat, hover-highlighted button with an optional icon glyph.</summary>
public sealed class FancyButton : Control
{
    private bool _hover, _down;
    public string Glyph { get; set; } = "";
    public Color BaseColor { get; set; } = Theme.Accent;

    public FancyButton(string text, string glyph = "", Color? color = null)
    {
        Text = text;
        Glyph = glyph;
        BaseColor = color ?? Theme.Accent;
        Font = new Font("Segoe UI Semibold", 10f);
        ForeColor = Color.White;
        Cursor = Cursors.Hand;
        Height = 38;
        Width = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width + (glyph.Length > 0 ? 58 : 34);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var c = !Enabled ? Color.FromArgb(70, 68, 90) : _down ? ControlPaint.Dark(BaseColor, .1f) : _hover ? ControlPaint.Light(BaseColor, .25f) : BaseColor;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.Round(r, 8))
        using (var brush = new LinearGradientBrush(r, ControlPaint.Light(c, .15f), c, 90f))
            g.FillPath(brush, path);
        var x = 14;
        if (Glyph.Length > 0)
        {
            TextRenderer.DrawText(g, Glyph, Theme.Icons, new Rectangle(x, 0, 24, Height), Enabled ? Color.White : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            x += 30;
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x - 8, Height), Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>A rounded panel with a header glyph and title, used for each feature on the game screen.</summary>
public sealed class Card : Panel
{
    public string Glyph { get; }
    public string Heading { get; }
    public Color AccentColor { get; set; } = Theme.Accent;
    public FlowLayoutPanel Body { get; }

    public Card(string glyph, string heading)
    {
        Glyph = glyph;
        Heading = heading;
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        Padding = new Padding(18, 56, 18, 14);
        Body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            BackColor = Theme.Panel,
        };
        Controls.Add(Body);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var path = Theme.Round(r, 14))
        {
            using var fill = new SolidBrush(Theme.Panel);
            g.FillPath(fill, path);
            using var pen = new Pen(Theme.Border, 1.2f);
            g.DrawPath(pen, path);
        }
        using (var bar = new LinearGradientBrush(new RectangleF(1, 1, Width - 3, 4), AccentColor, Theme.Accent2, 0f))
            g.FillRectangle(bar, 14, 1, Width - 30, 3);
        TextRenderer.DrawText(g, Glyph, Theme.Icons, new Point(18, 18), AccentColor == Theme.Accent ? Theme.Gold : AccentColor);
        TextRenderer.DrawText(g, Heading, Theme.H2, new Point(48, 16), Theme.Text);
    }

    protected override void OnResize(EventArgs eventargs) { base.OnResize(eventargs); Invalidate(); }
}
