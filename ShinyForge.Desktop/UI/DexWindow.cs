using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PKHeX.Core;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

public enum DexState { Missing, DexOnly, Owned, Shiny }

/// <summary>Pokédex sprites, cropped to the Pokémon and scaled for the grid, in colour and as a silhouette.</summary>
public static class Sprites
{
    public const int Size = 76;
    private static readonly Dictionary<string, (Bitmap Color, Bitmap Shadow)?> Cache = new();

    /// <param name="shiny">use the shiny picture when the entry has one (Living Dex: Pokémon HOME's shiny renders)</param>
    public static Bitmap? Get(DexEntry e, bool silhouette, bool shiny = false)
    {
        var sprite = shiny && e.ShinySprite.Length > 0 ? e.ShinySprite : e.Sprite;
        if (sprite.Length == 0) return null;
        (Bitmap Color, Bitmap Shadow)? pair;
        lock (Cache)
        {
            if (!Cache.TryGetValue(sprite, out pair))
                Cache[sprite] = pair = Load(Path.Combine(AppPaths.AssetsDir, sprite.Replace('\\', Path.DirectorySeparatorChar)));
        }
        return pair is { } p ? silhouette ? p.Shadow : p.Color : null;
    }

    /// <summary>Crops transparent borders, scales (crisp integer upscale for pixel art, smooth downscale for big renders) and makes a silhouette.</summary>
    private static (Bitmap, Bitmap)? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var src = new Bitmap(path);
            int w = src.PixelSize.Width, h = src.PixelSize.Height;
            var px = new byte[w * h * 4];
            var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { src.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, w * 4); }
            finally { handle.Free(); }
            var premul = src.AlphaFormat != AlphaFormat.Unpremul;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > 16) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            if (maxX < 0) { minX = minY = 0; maxX = w - 1; maxY = h - 1; }
            int cw = maxX - minX + 1, ch = maxY - minY + 1, side = Math.Max(cw, ch) + 4;
            int outSide, scale = 1;
            double k = 1;
            if (side > Size) { outSide = Size; k = (double)Size / side; }
            else { scale = Math.Max(1, Size / side); outSide = side * scale; }
            var dst = new byte[outSide * outSide * 4];
            var shadow = new byte[dst.Length];
            for (int oy = 0; oy < outSide; oy++)
            for (int ox = 0; ox < outSide; ox++)
            {
                // source area of this output pixel (box filter when shrinking, nearest when enlarging)
                double sx0, sy0, sx1, sy1;
                if (side > Size)
                {
                    var offX = (outSide - cw * k) / 2; var offY = (outSide - ch * k) / 2;
                    sx0 = (ox - offX) / k + minX; sy0 = (oy - offY) / k + minY; sx1 = sx0 + 1 / k; sy1 = sy0 + 1 / k;
                }
                else
                {
                    var offX = (side - cw) / 2 * scale; var offY = (side - ch) / 2 * scale;
                    sx0 = Math.Floor((double)(ox - offX) / scale) + minX; sy0 = Math.Floor((double)(oy - offY) / scale) + minY; sx1 = sx0 + 1; sy1 = sy0 + 1;
                }
                double b = 0, g = 0, r = 0, a = 0; int n = 0;
                for (int sy = (int)Math.Floor(sy0); sy < (int)Math.Ceiling(sy1); sy++)
                for (int sx = (int)Math.Floor(sx0); sx < (int)Math.Ceiling(sx1); sx++)
                {
                    n++;
                    if (sx < minX || sx > maxX || sy < minY || sy > maxY) continue;
                    var i = (sy * w + sx) * 4;
                    double al = px[i + 3], f = premul ? 1 : al / 255.0;
                    b += px[i] * f; g += px[i + 1] * f; r += px[i + 2] * f; a += al;
                }
                if (n == 0) continue;
                var o = (oy * outSide + ox) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = (byte)(a / n);
                var sa = dst[o + 3] / 255.0;   // silhouette: a flat dark slate colour, alpha kept (premultiplied)
                shadow[o] = (byte)(82 * sa); shadow[o + 1] = (byte)(56 * sa); shadow[o + 2] = (byte)(59 * sa); shadow[o + 3] = dst[o + 3];
            }
            return (Make(dst, outSide), Make(shadow, outSide));
        }
        catch (Exception ex) { AppPaths.Log($"Sprite {path}: {ex.Message}"); return null; }
    }

    private static WriteableBitmap Make(byte[] pixels, int side)
    {
        var wb = new WriteableBitmap(new PixelSize(side, side), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = wb.Lock();
        for (int y = 0; y < side; y++)
            Marshal.Copy(pixels, y * side * 4, fb.Address + y * fb.RowBytes, side * 4);
        return wb;
    }
}

/// <summary>Owner-drawn, scrolling grid of Pokédex cells (only the visible rows are drawn).</summary>
public sealed class DexGrid : Control
{
    private const int CellW = 96, CellH = 112, Pad = 6;
    private List<(DexEntry E, DexState S)> _items = new();
    private int _sel = -1, _hover = -1;
    private ScrollViewer? _scroll;
    public event Action<DexEntry>? Selected;
    public DexEntry? Current => _sel >= 0 && _sel < _items.Count ? _items[_sel].E : null;

    public DexGrid()
    {
        Focusable = true;
        ClipToBounds = true;
        PointerMoved += (_, e) =>
        {
            var h = HitTest(e.GetPosition(this));
            if (h != _hover) { _hover = h; Cursor = new Cursor(h >= 0 ? StandardCursorType.Hand : StandardCursorType.Arrow); InvalidateVisual(); }
        };
        PointerExited += (_, _) => { _hover = -1; InvalidateVisual(); };
        PointerPressed += (_, e) =>
        {
            Focus();
            var h = HitTest(e.GetPosition(this));
            if (h < 0) return;
            _sel = h;
            InvalidateVisual();
            Selected?.Invoke(_items[h].E);
        };
        KeyDown += (_, e) =>
        {
            if (_items.Count == 0) return;
            var delta = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -Cols, Key.Down => Cols, _ => 0 };
            if (delta == 0) return;
            e.Handled = true;
            var n = Math.Clamp(_sel < 0 ? 0 : _sel + delta, 0, _items.Count - 1);
            if (n == _sel) return;
            _sel = n;
            if (_scroll != null)
            {
                var top = Pad + n / Cols * (CellH + Pad);
                if (top < _scroll.Offset.Y) _scroll.Offset = new Vector(0, top - Pad);
                else if (top + CellH > _scroll.Offset.Y + _scroll.Viewport.Height) _scroll.Offset = new Vector(0, top + CellH + Pad - _scroll.Viewport.Height);
            }
            InvalidateVisual();
            Selected?.Invoke(_items[n].E);
        };
    }

    /// <summary>Put the grid in this ScrollViewer (redraws on scroll, sizes to its width).</summary>
    public ScrollViewer InScroll()
    {
        _scroll = new ScrollViewer { Content = this, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _scroll.ScrollChanged += (_, _) => InvalidateVisual();
        _scroll.SizeChanged += (_, _) => InvalidateMeasure();
        return _scroll;
    }

    private int Cols => Math.Max(1, (int)((Bounds.Width - Pad) / (CellW + Pad)));

    protected override Size MeasureOverride(Size available)
    {
        var w = _scroll?.Viewport.Width is > 0 and var vw ? vw : double.IsInfinity(available.Width) ? 800 : available.Width;
        var cols = Math.Max(1, (int)((w - Pad) / (CellW + Pad)));
        var rows = (_items.Count + cols - 1) / cols;
        return new Size(w, Math.Max(40, rows * (CellH + Pad) + Pad));
    }

    public void SetItems(List<(DexEntry, DexState)> items)
    {
        var current = Current;
        _items = items;
        _sel = current == null ? -1 : _items.FindIndex(i => i.E == current);
        _hover = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private int HitTest(Point p)
    {
        var x = p.X - Pad; var y = p.Y - Pad;
        if (x < 0 || y < 0) return -1;
        int col = (int)(x / (CellW + Pad)), row = (int)(y / (CellH + Pad));
        if (col >= Cols || x % (CellW + Pad) >= CellW || y % (CellH + Pad) >= CellH) return -1;
        var i = row * Cols + col;
        return i < _items.Count ? i : -1;
    }

    private static readonly Typeface Body = new(FontFamily.Default);
    private static FormattedText Txt(string s, double size, Color c, double maxW = 0)
    {
        var f = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Body, size, Skin.B(c)) { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        if (maxW > 0) f.MaxTextWidth = maxW;
        return f;
    }

    public override void Render(DrawingContext g)
    {
        g.FillRectangle(Skin.B(Skin.Bg), new Rect(Bounds.Size));
        if (_items.Count == 0) { g.DrawText(Txt("Nothing matches.", 13, Skin.Muted), new Point(20, 20)); return; }
        var cols = Cols;
        var top = _scroll?.Offset.Y ?? 0;
        var height = _scroll?.Viewport.Height ?? Bounds.Height;
        int firstRow = Math.Max(0, (int)((top - Pad) / (CellH + Pad))), lastRow = (int)((top + height) / (CellH + Pad)) + 1;
        for (int row = firstRow; row <= lastRow; row++)
        for (int col = 0; col < cols; col++)
        {
            var i = row * cols + col;
            if (i >= _items.Count) return;
            var (entry, state) = _items[i];
            var r = new Rect(Pad + col * (CellW + Pad), Pad + row * (CellH + Pad), CellW, CellH);
            var missing = state == DexState.Missing;
            var fill = i == _sel ? Skin.Accent : i == _hover ? Skin.PanelHi : Skin.Panel;
            var border = state == DexState.Shiny ? Skin.Gold : missing ? Skin.Border : Skin.Good;
            g.DrawRectangle(Skin.B(fill), new Pen(Skin.B(border), missing ? 1 : 1.6), r, 10, 10);
            if (Sprites.Get(entry, missing, state == DexState.Shiny) is { } img)
            {
                using (g.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                    g.DrawImage(img, new Rect(r.X + (CellW - Sprites.Size) / 2.0, r.Y + 6, Sprites.Size, Sprites.Size));
            }
            g.DrawText(Txt($"#{entry.Species:0000}", 10, Skin.Muted), new Point(r.X + 4, r.Y + 2));
            if (state == DexState.Shiny) g.DrawText(Txt("★", 12, Skin.Gold), new Point(r.Right - 16, r.Y + 1));
            var name = Txt(entry.Name, 11, missing ? Skin.Muted : Skin.Text, CellW - 4);
            g.DrawText(name, new Point(r.X + (CellW - Math.Min(name.Width, CellW - 4)) / 2, r.Bottom - 30));
            if (entry.FormName.Length > 0 || entry.Female)
            {
                var form = Txt(entry.FormName.Length > 0 ? entry.FormName + (entry.Female ? " (female)" : "") : "female", 10, Skin.Accent2, CellW - 4);
                g.DrawText(form, new Point(r.X + (CellW - Math.Min(form.Width, CellW - 4)) / 2, r.Bottom - 16));
            }
        }
    }
}

/// <summary>Pop-out Pokédex for one game: caught = coloured, missing = silhouette; click for details, locations, evolutions and events.</summary>
public sealed class DexWindow : Window
{
    private readonly Game _game;
    private readonly Func<Action<string>, Collection?> _load;
    private readonly DexGrid _grid = new();
    private readonly TextBox _search = Skin.Input("", 200);
    private readonly ComboBox _filter = Skin.Combo(new[] { "All", "Caught", "Missing", "Shiny" }, "All", 130);
    private readonly CheckBox _forms = Skin.Check("Forms, regional forms & female differences", true);
    private readonly CheckBox _top = Skin.Check("Keep on top", false);
    private readonly TextBlock _counts = Skin.T("", 16, Skin.Gold, FontWeight.SemiBold, false);
    private readonly TextBlock _status = Skin.T("", 11.5, Skin.Muted, wrap: false);
    private readonly Image _big = new() { Width = 160, Height = 160 };
    private readonly TextBlock _title = Skin.T("", 20, Skin.Text, FontWeight.SemiBold);
    private readonly TextBlock _sub = Skin.T("", 13, Skin.Muted);
    private readonly SelectableTextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Foreground = Skin.B(Skin.Text), FontSize = 12.5 };
    private readonly StackPanel _links = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly DispatcherTimer _autoRefresh = new() { Interval = TimeSpan.FromMinutes(1) };
    private List<DexEntry> _all = new();
    private Collection? _owned;
    private CancellationTokenSource? _infoCts;
    private bool _loading;

    public DexWindow(Game game, Func<Action<string>, Collection?> load)
    {
        _game = game;
        _load = load;
        Title = $"Pokédex - Pokémon {game.Title}";
        Icon = App.Icon;
        Width = 1180; Height = 800; MinWidth = 820; MinHeight = 520;
        Background = Skin.B(Skin.Bg);
        _search.PlaceholderText = "Search name or #";
        var refresh = new FancyButton("Refresh", Skin.GlyphSync, Skin.Accent) { Height = 32 };
        refresh.Click += async (_, _) => await LoadOwnedAsync();
        var bar = new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(10, 8), Child = Skin.Row(_search, _filter, _forms, _top, refresh) };
        _big.Margin = new Thickness(0, 0, 14, 0);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Border { Background = Skin.B(Skin.PanelHi), CornerRadius = new CornerRadius(8), Child = _big }, new StackPanel { Width = 200, Spacing = 4, Children = { _title, _sub } } } };
        var detail = new Border
        {
            Width = 410, Background = Skin.B(Skin.Panel), Padding = new Thickness(14),
            Child = new DockPanel { Children = { new StackPanel { [DockPanel.DockProperty] = Dock.Top, Spacing = 10, Children = { head, _links } }, new ScrollViewer { Margin = new Thickness(0, 10, 0, 0), Content = _info } } },
        };
        var root = new DockPanel();
        void Add(Control c, Dock d) { DockPanel.SetDock(c, d); root.Children.Add(c); }
        Add(bar, Dock.Top);
        Add(new Border { Padding = new Thickness(12, 6), Child = _counts }, Dock.Top);
        Add(new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(10, 4), Child = _status }, Dock.Bottom);
        Add(detail, Dock.Right);
        root.Children.Add(_grid.InScroll());
        Content = root;

        _grid.Selected += ShowInfo;
        _search.TextChanged += (_, _) => ApplyFilter();
        _filter.SelectionChanged += (_, _) => ApplyFilter();
        _forms.IsCheckedChanged += (_, _) => Rebuild();
        _top.IsCheckedChanged += (_, _) => Topmost = _top.IsChecked == true;
        // Save files are re-read every minute; the live Switch only on Refresh (never competes with a running hunt bot).
        _autoRefresh.Tick += async (_, _) => { if (!_loading && _owned?.Source.StartsWith("Live") != true) await LoadOwnedAsync(quiet: true); };
        Closed += (_, _) => _autoRefresh.Stop();
        Opened += async (_, _) =>
        {
            Rebuild();
            ShowWelcome();
            await LoadOwnedAsync();
            _autoRefresh.Start();
        };
    }

    private void Rebuild()
    {
        try { _all = DexData.Build(_game, _forms.IsChecked == true); }
        catch (Exception e) { _all = new(); _status.Text = "Couldn't build the Pokédex: " + e.Message; AppPaths.Log("Dex build: " + e); }
        ApplyFilter();
    }

    private async Task LoadOwnedAsync(bool quiet = false)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            if (!quiet) _status.Text = "Reading your Pokémon...";
            var before = _grid.Current is { } cur ? OwnedFor(cur).Count : -1;
            var c = await Task.Run(() => _load(s => Dialogs.Ui(() => _status.Text = s)));
            if (c == null) _status.Text = "No save file found - in the game screen choose a save file (Collection Tracker) or set the game folder. Showing an empty Pokédex.";
            else
            {
                _owned = c;
                _status.Text = $"{c.Total} Pokémon read from {c.Source}  •  updated {DateTime.Now:HH:mm}  •  " + (c.Source.StartsWith("Live") ? "press Refresh to read the Switch again" : "refreshes every minute");
            }
            ApplyFilter();
            if (_grid.Current != null && (!quiet || OwnedFor(_grid.Current).Count != before)) ShowInfo(_grid.Current);
        }
        catch (Exception e) { _status.Text = "Couldn't read your Pokémon: " + e.Message; }
        finally { _loading = false; }
    }

    private List<Collection.Owned> OwnedFor(DexEntry e)
    {
        if (_owned == null) return new();
        var forms = _forms.IsChecked == true;
        var hasFemaleEntry = forms && _all.Any(x => x.Species == e.Species && x.Female);
        return _owned.Mons.Where(m =>
        {
            if (m.Pk.Species != e.Species) return false;
            if (!forms) return true;
            if (m.Pk.Form != e.Form && _all.Any(x => x.Species == e.Species && x.Form == m.Pk.Form)) return false;
            if (m.Pk.Form != e.Form && e.Form != 0) return false;
            if (hasFemaleEntry && e.Form == 0) return e.Female ? m.Pk.Gender == 1 : m.Pk.Gender != 1;
            return true;
        }).ToList();
    }

    private DexState StateOf(DexEntry e)
    {
        var mons = OwnedFor(e);
        if (mons.Count > 0) return mons.Any(m => m.Pk.IsShiny) ? DexState.Shiny : DexState.Owned;
        if (_owned != null && !e.Female && e.Form == 0 && _owned.DexCaught.Contains(e.Species)) return DexState.DexOnly;
        return DexState.Missing;
    }

    private void ApplyFilter()
    {
        var q = (_search.Text ?? "").Trim();
        var f = _filter.SelectedItem as string ?? "All";
        var states = _all.ToDictionary(e => e, StateOf);
        IEnumerable<DexEntry> list = _all;
        if (q.Length > 0) list = list.Where(e => e.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Species.ToString() == q.TrimStart('#', '0'));
        list = f switch
        {
            "Caught" => list.Where(e => states[e] != DexState.Missing),
            "Missing" => list.Where(e => states[e] == DexState.Missing),
            "Shiny" => list.Where(e => states[e] == DexState.Shiny),
            _ => list,
        };
        _grid.SetItems(list.Select(e => (e, states[e])).ToList());
        var species = _all.Select(e => e.Species).Distinct().Count();
        var caught = _all.Where(e => states[e] != DexState.Missing).Select(e => e.Species).Distinct().Count();
        _counts.Text = $"Caught {caught}/{species} species ({(species == 0 ? 0 : caught * 100 / species)}%)" +
                       (_forms.IsChecked == true ? $"  •  {_all.Count(e => states[e] != DexState.Missing)}/{_all.Count} entries" : "") + $"  •  ★ {_all.Count(e => states[e] == DexState.Shiny)}";
    }

    private void ShowWelcome()
    {
        _title.Text = "Pokédex";
        _sub.Text = "Click a Pokémon.";
        _info.Text = "Coloured = caught (in your party/boxes, or marked caught in the save's Pokédex).\nSilhouette = not caught yet.\n★ = you have a shiny one.\n\n" +
                     "Select a missing Pokémon to see where to find it, how it evolves and its event distributions; select one you have to see the details of each one you own.\n\n" +
                     "Data comes from the save file chosen in the Collection Tracker card, or live from the Switch when that source is selected. The window refreshes every minute.";
    }

    private void ShowInfo(DexEntry e)
    {
        var state = StateOf(e);
        var mons = OwnedFor(e);
        _big.Source = Sprites.Get(e, state == DexState.Missing);
        _title.Text = $"#{e.Species:0000} {e.Name}";
        _sub.Text = (e.FormName.Length > 0 ? e.FormName + (e.Female ? " (female)" : "") + "\n" : e.Female ? "Female\n" : "") + e.Types + "\n" + state switch
        {
            DexState.Shiny => $"★ Caught - shiny! ({mons.Count} owned)",
            DexState.Owned => $"Caught ({mons.Count} owned)",
            DexState.DexOnly => "Caught before (Pokédex) - none in your boxes now",
            _ => "Not caught yet",
        };
        _sub.Foreground = Skin.B(state == DexState.Missing ? Skin.Warn : state == DexState.Shiny ? Skin.Gold : Skin.Good);
        _links.Children.Clear();
        foreach (var (label, url) in new[] { ("Bulbapedia", DexData.BulbapediaUrl(e)), ("Serebii", DexData.SerebiiUrl(_game, e)) })
        {
            var b = new FancyButton(label, "↗", Skin.PanelHi) { Height = 30 };
            b.Click += (_, _) => { try { Browser.Open(url); } catch { } };
            _links.Children.Add(b);
        }
        _info.Text = "Loading...";
        _infoCts?.Cancel();
        var cts = _infoCts = new CancellationTokenSource();
        Task.Run(() =>
        {
            var sb = new StringBuilder();
            if (mons.Count > 0)
            {
                sb.AppendLine($"YOUR {e.Name.ToUpperInvariant()} ({mons.Count})").AppendLine($"Source: {_owned?.Source}").AppendLine();
                foreach (var m in mons.Take(30)) sb.AppendLine(Describe(m)).AppendLine();
                if (mons.Count > 30) sb.AppendLine($"... and {mons.Count - 30} more.\n");
            }
            if (cts.IsCancellationRequested) return "";
            sb.AppendLine(mons.Count > 0 ? "HOW TO GET MORE" : "WHERE TO FIND IT");
            foreach (var l in DexData.WhereToFind(_game, e)) sb.AppendLine("• " + l);
            sb.AppendLine().AppendLine("EVOLUTION").AppendLine(DexData.Evolution(_game, e)).AppendLine();
            var events = DexData.Events(_game, e);
            sb.AppendLine("EVENT DISTRIBUTIONS (Generation " + _game.Gen + ")");
            if (events.Count == 0) sb.AppendLine("None known for this generation.");
            foreach (var l in events) sb.AppendLine("• " + l);
            return sb.ToString();
        }).ContinueWith(t => Dialogs.Ui(() => { if (!cts.IsCancellationRequested) _info.Text = t.IsFaulted ? "Error: " + t.Exception?.GetBaseException().Message : t.Result; }));
    }

    public static string Describe(Collection.Owned m)
    {
        var pk = m.Pk;
        var s = GameInfo.Strings;
        string Get(IReadOnlyList<string> list, int i) => i >= 0 && i < list.Count ? list[i] : "?";
        var sb = new StringBuilder();
        var nick = pk.IsNicknamed ? $"\"{pk.Nickname}\" " : "";
        var gender = pk.Gender switch { 0 => "(male)", 1 => "(female)", _ => "" };
        sb.AppendLine($"{(pk.IsShiny ? "★ SHINY " : "")}{nick}{DexEntry.Plain(SpeciesName.GetSpeciesName(pk.Species, 2))} {gender}  Lv.{pk.CurrentLevel}  -  {m.Where}");
        if (pk.Format >= 3) sb.AppendLine($"Nature {Get(s.natures, (int)pk.Nature)}  •  Ability {Get(s.abilitylist, pk.Ability)}");
        sb.AppendLine($"IVs {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}  (HP/Atk/Def/SpA/SpD/Spe)");
        sb.AppendLine($"EVs {pk.EV_HP}/{pk.EV_ATK}/{pk.EV_DEF}/{pk.EV_SPA}/{pk.EV_SPD}/{pk.EV_SPE}");
        sb.AppendLine("Moves: " + string.Join(", ", new[] { pk.Move1, pk.Move2, pk.Move3, pk.Move4 }.Where(x => x != 0).Select(x => Get(s.movelist, x))));
        if (pk.HeldItem != 0)
        {
            string item;
            try { item = Get(s.GetItemStrings(pk.Context, pk.Version), pk.HeldItem); } catch { item = Get(s.itemlist, pk.HeldItem); }
            sb.AppendLine("Item: " + item);
        }
        sb.AppendLine($"OT {pk.OriginalTrainerName} (ID {pk.DisplayTID}){(m.OwnOT ? " - caught by you" : " - traded / not your OT")}");
        if (pk.Format >= 3)
        {
            string loc;
            try { loc = GameInfo.GetLocationName(false, pk.MetLocation, pk.Format, pk.Generation, pk.Version); } catch { loc = ""; }
            var ball = pk.Ball > 0 ? Get(s.balllist, pk.Ball) : "";
            var date = pk.Format >= 4 && pk.MetDate is { } d ? $" on {d:yyyy-MM-dd}" : "";
            sb.AppendLine($"Met {(string.IsNullOrWhiteSpace(loc) ? "?" : loc)} at Lv.{pk.MetLevel}{date}{(ball.Length > 0 ? $" in a {ball}" : "")}  ({pk.Version})");
        }
        return sb.ToString().TrimEnd();
    }
}
