using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ShinyForge.UI;

/// <summary>ShinyForge's dark look (the same colours as the Windows version) and small control helpers.</summary>
public static class Skin
{
    public static readonly Color Bg = Color.FromRgb(14, 13, 22);
    public static readonly Color Panel = Color.FromRgb(26, 24, 40);
    public static readonly Color PanelHi = Color.FromRgb(36, 33, 56);
    public static readonly Color Border = Color.FromRgb(58, 54, 88);
    public static readonly Color Accent = Color.FromRgb(124, 58, 237);
    public static readonly Color Accent2 = Color.FromRgb(24, 190, 200);
    public static readonly Color Gold = Color.FromRgb(255, 214, 102);
    public static readonly Color Text = Color.FromRgb(240, 238, 250);
    public static readonly Color Muted = Color.FromRgb(160, 156, 190);
    public static readonly Color Good = Color.FromRgb(52, 211, 153);
    public static readonly Color Warn = Color.FromRgb(251, 191, 36);
    public static readonly Color Bad = Color.FromRgb(248, 113, 113);
    public static readonly Color StopRed = Color.FromRgb(150, 40, 60);

    // Unicode symbols present in the common system fonts of Windows, macOS and Linux (no icon font needed).
    public const string GlyphBack = "←", GlyphSettings = "⚙", GlyphSparkle = "✦", GlyphRecord = "●", GlyphSheet = "▦",
        GlyphSave = "▣", GlyphTools = "⚒", GlyphFolder = "▤", GlyphPlay = "▶", GlyphStop = "■", GlyphSync = "↻", GlyphInfo = "ℹ",
        GlyphGame = "◉", GlyphCheck = "✔", GlyphWarn = "⚠", GlyphDownload = "↓", GlyphUsb = "⌁", GlyphWifi = "≋", GlyphHelp = "?",
        GlyphStar = "★";

    public static IBrush B(Color c) => new SolidColorBrush(c);
    public static IBrush B(byte a, Color c) => new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
    public static Color FromDrawing(System.Drawing.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Color Lighter(Color c, double t) => Lerp(c, Colors.White, t);
    public static Color Darker(Color c, double t) => Lerp(c, Colors.Black, t);

    public static TextBlock T(string text, double size = 13, Color? color = null, FontWeight weight = FontWeight.Normal, bool wrap = true) => new()
    {
        Text = text, FontSize = size, Foreground = B(color ?? Text), FontWeight = weight,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    public static TextBlock Title(string text) => T(text, 26, Text, FontWeight.Bold, false);
    public static TextBlock H2(string text, Color? color = null) => T(text, 16, color ?? Text, FontWeight.SemiBold);
    public static TextBlock Small(string text, Color? color = null) => T(text, 11.5, color ?? Muted);
    public static TextBlock MutedText(string text) => T(text, 13, Skin.Muted);

    public static StackPanel Row(params Control[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4) };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static WrapPanel Wrap(params Control[] items)
    {
        var p = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4) };
        foreach (var i in items) { i.Margin = new Thickness(0, 0, 8, 6); p.Children.Add(i); }
        return p;
    }

    public static StackPanel Column(double spacing = 6, params Control[] items)
    {
        var p = new StackPanel { Spacing = spacing };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static TextBox Input(string text = "", double width = 300) => new()
    {
        Text = text, Width = width, Background = B(PanelHi), Foreground = B(Text), BorderBrush = B(Border), FontSize = 13,
    };

    public static ComboBox Combo(IEnumerable<string> items, string? selected, double width = 300)
    {
        var list = items.ToList();
        var cb = new ComboBox { ItemsSource = list, Width = width, Background = B(PanelHi), Foreground = B(Text), FontSize = 13 };
        cb.SelectedIndex = selected != null && list.Contains(selected) ? list.IndexOf(selected) : list.Count > 0 ? 0 : -1;
        return cb;
    }

    public static CheckBox Check(string text, bool on) => new() { Content = T(text, 13, Text, wrap: false), IsChecked = on };

    public static NumericUpDown Number(int min, int max, int value, double width = 120) => new()
    {
        Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Increment = 1, Width = width, FormatString = "0",
        Background = B(PanelHi), Foreground = B(Text),
    };

    public static LogView LogBox(double height = 140) => new(height);

    public static void Append(LogView box, string line) => box.Append(line);
}

/// <summary>A read-only log that scrolls inside itself (appending never scrolls the page around it).</summary>
public sealed class LogView : Border
{
    private readonly SelectableTextBlock _text = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("monospace"), FontSize = 12, Foreground = Skin.B(Skin.Text) };
    private readonly ScrollViewer _scroll;

    public LogView(double height)
    {
        Height = height;
        Background = Skin.B(Skin.PanelHi);
        BorderBrush = Skin.B(Skin.Border);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        _scroll = new ScrollViewer { Content = _text, Padding = new Thickness(6, 4) };
        Child = _scroll;
    }

    public string Text => _text.Text ?? "";

    public void Append(string line)
    {
        _text.Text = Text + line + Environment.NewLine;
        _scroll.ScrollToEnd();
    }
}

/// <summary>Rounded, hover-highlighted button with an optional symbol (the Windows version's FancyButton).</summary>
public sealed class FancyButton : Border
{
    private readonly TextBlock _glyph = new() { FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
    private readonly TextBlock _text = new() { FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
    private Color _color;
    private bool _hover, _down;
    public event EventHandler? Click;

    public FancyButton(string text, string glyph = "", Color? color = null)
    {
        _color = color ?? Skin.Accent;
        _glyph.Text = glyph;
        _glyph.IsVisible = glyph.Length > 0;
        _text.Text = text;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(14, 0, 16, 0);
        Height = 38;
        Cursor = new Cursor(StandardCursorType.Hand);
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Children = { _glyph, _text } };
        Focusable = true;
        Paint();
        PointerEntered += (_, _) => { _hover = true; Paint(); };
        PointerExited += (_, _) => { _hover = _down = false; Paint(); };
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _down = true; Paint(); } };
        PointerReleased += (_, e) =>
        {
            var fire = _down && IsEffectivelyEnabled && new Rect(Bounds.Size).Contains(e.GetPosition(this));
            _down = false;
            Paint();
            if (fire) Click?.Invoke(this, EventArgs.Empty);
        };
        KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space && IsEffectivelyEnabled) Click?.Invoke(this, EventArgs.Empty); };
        PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty) Paint(); };
    }

    public string Text { get => _text.Text ?? ""; set => _text.Text = value; }
    public Color BaseColor { get => _color; set { _color = value; Paint(); } }

    private void Paint()
    {
        var c = !IsEnabled ? Color.FromRgb(70, 68, 90) : _down ? Skin.Darker(_color, .12) : _hover ? Skin.Lighter(_color, .22) : _color;
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Skin.Lighter(c, .14), 0), new GradientStop(c, 1) },
        };
        var fg = IsEnabled ? Brushes.White : Skin.B(Skin.Muted);
        _text.Foreground = fg;
        _glyph.Foreground = fg;
    }
}

/// <summary>A rounded panel with a coloured top line, symbol and title: one feature on the game screen.</summary>
public sealed class Card : Border
{
    public StackPanel Body { get; } = new() { Spacing = 4 };

    public Card(string glyph, string heading, Color? accent = null)
    {
        var a = accent ?? Skin.Accent;
        Background = Skin.B(Skin.Panel);
        BorderBrush = Skin.B(Skin.Border);
        BorderThickness = new Thickness(1.2);
        CornerRadius = new CornerRadius(14);
        Margin = new Thickness(8);
        var bar = new Border
        {
            Height = 3, Margin = new Thickness(14, 0, 14, 0), CornerRadius = new CornerRadius(2),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(a, 0), new GradientStop(Skin.Accent2, 1) },
            },
        };
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(18, 14, 18, 8),
            Children = { Skin.T(glyph, 18, a == Skin.Accent ? Skin.Gold : a, wrap: false), Skin.H2(heading) },
        };
        Child = new DockPanel
        {
            Children =
            {
                new StackPanel { [DockPanel.DockProperty] = Dock.Top, Children = { bar, head } },
                new ScrollViewer { Content = Body, Padding = new Thickness(18, 0, 18, 14), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            },
        };
    }
}
