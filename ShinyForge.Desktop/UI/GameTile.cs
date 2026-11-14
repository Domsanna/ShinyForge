using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ShinyForge.Core;

namespace ShinyForge.UI;

/// <summary>Image cache for tiles and cards.</summary>
internal static class Images
{
    private static readonly Dictionary<string, Bitmap?> Cache = new();
    public static Bitmap? Get(string path)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(path, out var b)) Cache[path] = b = App.LoadBitmap(path);
            return b;
        }
    }
}

/// <summary>
/// A game "box art" tile: version-coloured gradient, the game's cover Pokémon, the title in a logo-style lettering and a
/// console badge (an official logo image in Assets/Logos/&lt;game id&gt;.png is drawn instead of the text).
/// </summary>
public sealed class GameTile : Control
{
    private double _glow;
    private bool _hover;
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(15) };
    public Game Game { get; }
    public bool Large { get; }
    public event Action<Game>? Clicked;

    public GameTile(Game game, bool large = false)
    {
        Game = game;
        Large = large;
        Width = large ? 520 : 250;
        Height = large ? 190 : 150;
        Margin = new Thickness(10);
        if (!large) Cursor = new Cursor(StandardCursorType.Hand);
        _anim.Tick += (_, _) =>
        {
            _glow = Math.Clamp(_glow + (_hover ? .12 : -.12), 0, 1);
            if (_glow is 0 or 1) _anim.Stop();
            InvalidateVisual();
        };
        PointerEntered += (_, _) => { if (!Large) { _hover = true; _anim.Start(); } };
        PointerExited += (_, _) => { _hover = false; _anim.Start(); };
        PointerReleased += (_, e) => { if (!Large && e.InitialPressMouseButton == MouseButton.Left) Clicked?.Invoke(Game); };
    }

    private static readonly Typeface Heavy = new(FontFamily.Default, FontStyle.Normal, FontWeight.Black);
    private static readonly Typeface Semi = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    public override void Render(DrawingContext g)
    {
        var lift = _glow * 4;
        var r = new Rect(4, 6 - lift, Bounds.Width - 9, Bounds.Height - 12);
        var radius = Large ? 20 : 16;
        var c1 = Skin.FromDrawing(Game.Color1);
        var c2 = Skin.FromDrawing(Game.Color2);

        if (_glow > 0)
            g.DrawRectangle(null, new Pen(Skin.B((byte)(170 * _glow), Skin.Gold), 3), r.Inflate(3), radius + 3, radius + 3);

        var bg = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(c1, 0), new GradientStop(c2, 1) },
        };
        g.DrawRectangle(bg, null, r, radius, radius);

        using (g.PushClip(new RoundedRect(r, radius)))
        {
            // diagonal shine band
            var shine = new StreamGeometry();
            using (var ctx = shine.Open())
            {
                ctx.BeginFigure(r.TopLeft, true);
                ctx.LineTo(new Point(r.Left + r.Width * .55, r.Top));
                ctx.LineTo(new Point(r.Left + r.Width * .25, r.Bottom));
                ctx.LineTo(r.BottomLeft);
                ctx.EndFigure(true);
            }
            g.DrawGeometry(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(70, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
            }, null, shine);
            // ring watermark
            var d = r.Height * 1.25;
            g.DrawEllipse(null, new Pen(Skin.B(38, Colors.White), Large ? 16 : 10), new Rect(r.Right - d * .78, r.Top - d * .15, d, d));
            // cover Pokémon
            if (Images.Get(Path.Combine(AppPaths.ArtDir, $"{Game.Mascot}.png")) is { } art)
            {
                var h = r.Height * (Large ? 1.0 : .95);
                var w = h * art.Size.Width / art.Size.Height;
                g.DrawImage(art, new Rect(r.Right - w * .92, r.Bottom - h + lift * .5, w, h));
            }
        }

        var textArea = new Rect(r.Left + 16, r.Top + (Large ? 44 : 34), r.Width * .62, r.Height - (Large ? 60 : 44));
        if (Images.Get(Path.Combine(AppPaths.LogoDir, $"{Game.Id}.png")) is { } logo)
        {
            var scale = Math.Min(textArea.Width / logo.Size.Width, textArea.Height / logo.Size.Height);
            g.DrawImage(logo, new Rect(textArea.Left, textArea.Top, logo.Size.Width * scale, logo.Size.Height * scale));
        }
        else DrawLogoText(g, textArea, c1);

        // badge: console + generation
        var badge = Text($"{Game.PlatformLabel.ToUpperInvariant()}  •  GEN {Game.Gen}", Semi, Large ? 12 : 10, Brushes.White);
        var br = new Rect(r.Left + 14, r.Top + 12, badge.Width + 14, badge.Height + 4);
        g.DrawRectangle(Skin.B(120, Colors.Black), null, br, br.Height / 2, br.Height / 2);
        g.DrawText(badge, new Point(br.Left + 7, br.Top + 2));
        if (Services.HuntService.OptionsFor(Game).Count > 0 || Services.ThreeDS.Games3DS.All.Any(p => p.ShinyForgeId == Game.Id))
        {
            var tag = Text("BOT", new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), Large ? 12 : 10, Brushes.Black);
            var tr = new Rect(r.Right - tag.Width - 26, r.Top + 12, tag.Width + 14, tag.Height + 4);
            g.DrawRectangle(Skin.B(Skin.Gold), null, tr, tr.Height / 2, tr.Height / 2);
            g.DrawText(tag, new Point(tr.Left + 7, tr.Top + 2));
        }
    }

    private static FormattedText Text(string s, Typeface tf, double size, IBrush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, brush);

    /// <summary>"POKéMON" over the version name, outlined and with a drop shadow, like a box logo.</summary>
    private void DrawLogoText(DrawingContext g, Rect area, Color c1)
    {
        var small = Large ? 17 : 12;
        g.DrawText(Text("POKéMON", Heavy, small, Skin.B(230, Skin.Gold)), new Point(area.Left + 1, area.Top));
        var title = Game.Title.ToUpperInvariant();
        var size = Large ? 45.0 : 28.0;
        var probe = Text(title, Heavy, size, Brushes.White);
        if (probe.Width > area.Width) size *= area.Width / probe.Width;
        var ft = Text(title, Heavy, size, Brushes.White);
        var origin = new Point(area.Left, area.Top + small * 1.25);
        if (ft.BuildGeometry(origin) is not { } geo)
        {
            g.DrawText(ft, origin);
            return;
        }
        using (g.PushTransform(Matrix.CreateTranslation(2, 3)))
            g.DrawGeometry(Skin.B(120, Colors.Black), null, geo);
        g.DrawGeometry(null, new Pen(Skin.B(200, Color.FromRgb(15, 12, 30)), Large ? 6 : 4, lineJoin: PenLineJoin.Round), geo);
        g.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Skin.Lerp(Colors.White, c1, .35), 1) },
        }, null, geo);
    }
}

/// <summary>Big clickable card for choosing the console.</summary>
public sealed class PlatformCard : Control
{
    private bool _hover;
    private readonly string _glyph, _heading, _sub;
    private readonly Color _c1, _c2;
    private readonly ushort[] _art;
    public event Action? Clicked;

    public PlatformCard(string glyph, string heading, string sub, Color c1, Color c2, params ushort[] art)
    {
        (_glyph, _heading, _sub, _c1, _c2, _art) = (glyph, heading, sub, c1, c2, art);
        Width = 470; Height = 320;
        Margin = new Thickness(20);
        Cursor = new Cursor(StandardCursorType.Hand);
        PointerEntered += (_, _) => { _hover = true; InvalidateVisual(); };
        PointerExited += (_, _) => { _hover = false; InvalidateVisual(); };
        PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Clicked?.Invoke(); };
    }

    public override void Render(DrawingContext g)
    {
        var r = new Rect(5, _hover ? 2 : 6, Bounds.Width - 11, Bounds.Height - 12);
        g.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(_c1, 0), new GradientStop(_c2, 1) },
        }, _hover ? new Pen(Skin.B(Skin.Gold), 3) : null, r, 24, 24);
        using (g.PushClip(new RoundedRect(r, 24)))
        {
            var x = r.Right - 20;
            foreach (var sp in _art.Reverse())
            {
                if (Images.Get(Path.Combine(AppPaths.ArtDir, $"{sp}.png")) is not { } img) continue;
                var h = r.Height * .42;
                var w = h * img.Size.Width / img.Size.Height;
                x -= w * .72;
                g.DrawImage(img, new Rect(x, r.Bottom - h - 4, w, h));
            }
            g.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(110, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
            }, null, new Rect(r.Left, r.Top, r.Width, r.Height * .62));
        }
        var tf = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
        g.DrawText(new FormattedText(_glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 40, Brushes.White), new Point(r.Left + 26, r.Top + 16));
        g.DrawText(new FormattedText(_heading, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 32, Brushes.White), new Point(r.Left + 24, r.Top + 70));
        var sub = new FormattedText(_sub, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 14, Skin.B(240, Colors.White)) { MaxTextWidth = r.Width - 50 };
        g.DrawText(sub, new Point(r.Left + 28, r.Top + 118));
    }
}
