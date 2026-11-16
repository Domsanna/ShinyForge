using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using PKHeX.Core;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// Pop-out Pokédex for one game: every species / form / regional form / gender difference in that game, coloured when you've
/// caught it, a silhouette when you haven't. Click an entry for where to find it, how it evolves, events, or - when you
/// have it - the data of each one you own (from the save file or the live game).
/// </summary>
public sealed class DexForm : Form
{
    private readonly Game _game;
    private readonly Func<Action<string>, Collection?> _load;
    private readonly DexGrid _grid = new();
    private readonly TextBox _search = new() { Width = 200, PlaceholderText = "Search name or #" };
    private readonly ComboBox _filter = new() { Width = 130 };
    private readonly CheckBox _forms = new() { Text = "Forms, regional forms && female differences", Checked = true, AutoSize = true };
    private readonly CheckBox _top = new() { Text = "Keep on top", AutoSize = true };
    private readonly Label _counts = new() { AutoSize = true };
    private readonly Label _status = new() { AutoSize = true };
    private readonly PictureBox _bigSprite = new() { Size = new Size(160, 160), SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label _title = new() { AutoSize = false, Height = 34 };
    private readonly Label _sub = new() { AutoSize = false, Height = 44 };
    private readonly RichTextBox _info = new() { ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = true };
    private readonly System.Windows.Forms.Timer _autoRefresh = new() { Interval = 60_000 };
    private List<DexEntry> _all = new();
    private Collection? _owned;
    private CancellationTokenSource? _infoCts;
    private bool _loading;

    public DexForm(Game game, Func<Action<string>, Collection?> load)
    {
        _game = game;
        _load = load;
        Text = $"Pokédex - Pokémon {game.Title}";
        Size = new Size(1180, 800);
        MinimumSize = new Size(820, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // ---- top bar
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(10, 8, 10, 4), BackColor = Theme.Panel, WrapContents = false };
        Theme.StyleInput(_search);
        Theme.StyleInput(_filter);
        _filter.Items.AddRange(new object[] { "All", "Caught", "Missing", "Shiny" });
        _filter.SelectedIndex = 0;
        foreach (var cb in new[] { _forms, _top }) { cb.ForeColor = Theme.Text; cb.Margin = new Padding(12, 6, 0, 0); }
        var refresh = new FancyButton("Refresh", Theme.GlyphSync, Theme.Accent) { Height = 32, Margin = new Padding(12, 0, 0, 0) };
        refresh.Click += async (_, _) => await LoadOwnedAsync();
        _counts.ForeColor = Theme.Gold;
        _counts.Font = Theme.H2;
        _search.Margin = new Padding(0, 3, 6, 0);
        _filter.Margin = new Padding(0, 3, 0, 0);
        bar.Controls.AddRange(new Control[] { _search, _filter, _forms, _top, refresh });
        var countsBar = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Theme.Bg };
        _counts.Location = new Point(12, 6);
        countsBar.Controls.Add(_counts);

        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = Theme.Panel };
        _status.ForeColor = Theme.Muted;
        _status.Location = new Point(10, 5);
        statusBar.Controls.Add(_status);

        // ---- detail panel
        var detail = new Panel { Dock = DockStyle.Right, Width = 400, BackColor = Theme.Panel, Padding = new Padding(14) };
        _bigSprite.BackColor = Theme.PanelHi;
        _bigSprite.Location = new Point(14, 14);
        _title.Location = new Point(184, 18);
        _title.Width = 200;
        _title.Font = new Font("Segoe UI Semibold", 15f);
        _title.ForeColor = Theme.Text;
        _sub.Location = new Point(184, 56);
        _sub.Width = 200;
        _sub.Height = 110;
        _sub.ForeColor = Theme.Muted;
        _info.BackColor = Theme.Panel;
        _info.ForeColor = Theme.Text;
        _info.Font = new Font("Segoe UI", 9.5f);
        _info.Location = new Point(14, 186);
        _info.Size = new Size(372, 520);
        _info.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _info.LinkClicked += (_, e) => { try { Process.Start(new ProcessStartInfo(e.LinkText!) { UseShellExecute = true }); } catch { } };
        detail.Controls.AddRange(new Control[] { _bigSprite, _title, _sub, _info });
        detail.Resize += (_, _) => _info.Size = new Size(detail.ClientSize.Width - 28, detail.ClientSize.Height - 200);

        _grid.Dock = DockStyle.Fill;
        _grid.Selected += e => ShowInfo(e);
        Controls.Add(_grid);
        Controls.Add(detail);
        Controls.Add(statusBar);
        Controls.Add(countsBar);
        Controls.Add(bar);

        _search.TextChanged += (_, _) => ApplyFilter();
        _filter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _forms.CheckedChanged += (_, _) => Rebuild();
        _top.CheckedChanged += (_, _) => TopMost = _top.Checked;
        // Save files are re-read every minute. The live Switch is only read on Refresh, so it never competes with a running hunt bot.
        _autoRefresh.Tick += async (_, _) => { if (!_loading && _owned?.Source.StartsWith("Live") != true) await LoadOwnedAsync(quiet: true); };
        FormClosed += (_, _) => { _autoRefresh.Dispose(); _grid.DisposeImages(); };

        Shown += async (_, _) =>
        {
            Rebuild();
            ShowWelcome();
            await LoadOwnedAsync();
            _autoRefresh.Start();
        };
    }

    // ------------------------------------------------------------------ data
    private void Rebuild()
    {
        try
        {
            _all = DexData.Build(_game, _forms.Checked);
        }
        catch (Exception e)
        {
            _all = new();
            _status.Text = "Couldn't build the Pokédex: " + e.Message;
            AppPaths.Log("Dex build: " + e);
        }
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
            var c = await Task.Run(() => _load(s => BeginInvokeSafe(() => _status.Text = s)));
            if (IsDisposed) return;
            if (c == null)
            {
                _status.Text = "No save file found - in the game screen choose a save file (Collection Tracker) or set the game folder. Showing an empty Pokédex.";
            }
            else
            {
                _owned = c;
                _status.Text = $"{c.Total} Pokémon read from {c.Source}  •  updated {DateTime.Now:HH:mm}  •  " +
                               (c.Source.StartsWith("Live") ? "press Refresh to read the Switch again" : "refreshes every minute");
            }
            ApplyFilter();
            // Redraw the details only when something changed for the selected Pokémon (keeps your scroll position).
            if (_grid.Current != null && (!quiet || OwnedFor(_grid.Current).Count != before)) ShowInfo(_grid.Current);
        }
        catch (Exception e)
        {
            if (!IsDisposed) _status.Text = "Couldn't read your Pokémon: " + e.Message;
        }
        finally { _loading = false; }
    }

    private void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    /// <summary>The owned Pokémon that count for this entry (matching species, form and - for split gender looks - gender).</summary>
    private List<Collection.Owned> OwnedFor(DexEntry e)
    {
        if (_owned == null) return new();
        var hasFemaleEntry = _forms.Checked && _all.Any(x => x.Species == e.Species && x.Female);
        return _owned.Mons.Where(m =>
        {
            if (m.Pk.Species != e.Species) return false;
            if (!_forms.Checked) return true;
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
        // The save's own Pokédex says it was caught at some point (no longer in your boxes) - base entry only.
        if (_owned != null && !e.Female && e.Form == 0 && _owned.DexCaught.Contains(e.Species)) return DexState.DexOnly;
        return DexState.Missing;
    }

    private void ApplyFilter()
    {
        var q = _search.Text.Trim();
        var f = _filter.SelectedItem as string ?? "All";
        var states = _all.ToDictionary(e => e, StateOf);
        IEnumerable<DexEntry> list = _all;
        if (q.Length > 0)
            list = list.Where(e => e.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Species.ToString() == q.TrimStart('#', '0'));
        list = f switch
        {
            "Caught" => list.Where(e => states[e] != DexState.Missing),
            "Missing" => list.Where(e => states[e] == DexState.Missing),
            "Shiny" => list.Where(e => states[e] == DexState.Shiny),
            _ => list,
        };
        _grid.SetItems(list.Select(e => (e, states[e])).ToList());

        var species = _all.Select(e => e.Species).Distinct().Count();
        var caughtSpecies = _all.Where(e => states[e] != DexState.Missing).Select(e => e.Species).Distinct().Count();
        var entries = _all.Count(e => states[e] != DexState.Missing);
        var shiny = _all.Count(e => states[e] == DexState.Shiny);
        _counts.Text = $"Caught {caughtSpecies}/{species} species ({(species == 0 ? 0 : caughtSpecies * 100 / species)}%)" +
                       (_forms.Checked ? $"  •  {entries}/{_all.Count} entries" : "") + $"  •  ★ {shiny}";
    }

    // ------------------------------------------------------------------ detail panel
    private void ShowWelcome()
    {
        _title.Text = "Pokédex";
        _sub.Text = "Click a Pokémon.";
        _info.Text = "Coloured = caught (in your party/boxes, or marked caught in the save's Pokédex).\n" +
                     "Silhouette = not caught yet.\n★ = you have a shiny one.\n\n" +
                     "Select a missing Pokémon to see where to find it, how it evolves and its event distributions; select one you have " +
                     "to see the details of each one you own.\n\nData comes from the save file chosen in the Collection Tracker card, or " +
                     "live from the Switch when that source is selected. The window refreshes every minute.";
    }

    private void ShowInfo(DexEntry e)
    {
        var state = StateOf(e);
        var mons = OwnedFor(e);
        _bigSprite.Image = _grid.GetImage(e, state == DexState.Missing);
        _title.Text = $"#{e.Species:0000} {e.Name}";
        _sub.Text = (e.FormName.Length > 0 ? e.FormName + (e.Female ? " (female)" : "") + "\n" : e.Female ? "Female\n" : "") +
                    e.Types + "\n" + state switch
                    {
                        DexState.Shiny => $"★ Caught - shiny! ({mons.Count} owned)",
                        DexState.Owned => $"Caught ({mons.Count} owned)",
                        DexState.DexOnly => "Caught before (Pokédex) - none in your boxes now",
                        _ => "Not caught yet",
                    };
        _sub.ForeColor = state == DexState.Missing ? Theme.Warn : state == DexState.Shiny ? Theme.Gold : Theme.Good;
        _info.Text = "Loading...";

        _infoCts?.Cancel();
        var cts = _infoCts = new CancellationTokenSource();
        Task.Run(() =>
        {
            var sb = new StringBuilder();
            if (mons.Count > 0)
            {
                sb.AppendLine($"YOUR {e.Name.ToUpperInvariant()} ({mons.Count})");
                sb.AppendLine($"Source: {_owned?.Source}");
                sb.AppendLine();
                foreach (var m in mons.Take(30))
                {
                    sb.AppendLine(Describe(m));
                    sb.AppendLine();
                }
                if (mons.Count > 30) sb.AppendLine($"... and {mons.Count - 30} more.\n");
            }
            if (cts.IsCancellationRequested) return "";
            sb.AppendLine(mons.Count > 0 ? "HOW TO GET MORE" : "WHERE TO FIND IT");
            foreach (var l in DexData.WhereToFind(_game, e)) sb.AppendLine("• " + l);
            sb.AppendLine();
            sb.AppendLine("EVOLUTION");
            sb.AppendLine(DexData.Evolution(_game, e));
            sb.AppendLine();
            var events = DexData.Events(_game, e);
            sb.AppendLine("EVENT DISTRIBUTIONS (Generation " + _game.Gen + ")");
            if (events.Count == 0) sb.AppendLine("None known for this generation.");
            foreach (var l in events) sb.AppendLine("• " + l);
            sb.AppendLine();
            sb.AppendLine("MORE INFO");
            sb.AppendLine(DexData.BulbapediaUrl(e));
            sb.AppendLine(DexData.SerebiiUrl(_game, e));
            return sb.ToString();
        }).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested) return;
            BeginInvokeSafe(() => { if (!cts.IsCancellationRequested) _info.Text = t.IsFaulted ? "Error: " + t.Exception?.GetBaseException().Message : t.Result; });
        });
    }

    private static string Describe(Collection.Owned m)
    {
        var pk = m.Pk;
        var s = GameInfo.Strings;
        string Get(IReadOnlyList<string> list, int i) => i >= 0 && i < list.Count ? list[i] : "?";
        var sb = new StringBuilder();
        var nick = pk.IsNicknamed ? $"\"{pk.Nickname}\" " : "";
        var gender = pk.Gender switch { 0 => "(male)", 1 => "(female)", _ => "" };
        sb.AppendLine($"{(pk.IsShiny ? "★ SHINY " : "")}{nick}{DexEntry.Plain(SpeciesName.GetSpeciesName(pk.Species, 2))} {gender}  Lv.{pk.CurrentLevel}  -  {m.Where}");
        if (pk.Format >= 3)
            sb.AppendLine($"Nature {Get(s.natures, (int)pk.Nature)}  •  Ability {Get(s.abilitylist, pk.Ability)}");
        sb.AppendLine($"IVs {pk.IV_HP}/{pk.IV_ATK}/{pk.IV_DEF}/{pk.IV_SPA}/{pk.IV_SPD}/{pk.IV_SPE}  (HP/Atk/Def/SpA/SpD/Spe)");
        if (TransferShiny.Line(pk) is { } transfer) sb.AppendLine(transfer); // shiny after moving to Bank / later games?
        sb.AppendLine($"EVs {pk.EV_HP}/{pk.EV_ATK}/{pk.EV_DEF}/{pk.EV_SPA}/{pk.EV_SPD}/{pk.EV_SPE}");
        var moves = new[] { pk.Move1, pk.Move2, pk.Move3, pk.Move4 }.Where(x => x != 0).Select(x => Get(s.movelist, x));
        sb.AppendLine("Moves: " + string.Join(", ", moves));
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

public enum DexState { Missing, DexOnly, Owned, Shiny }

/// <summary>Fast owner-drawn, virtual-scrolling grid of Pokédex cells.</summary>
public sealed class DexGrid : Panel
{
    private const int CellW = 96, CellH = 112, Pad = 6, Img = 76;
    private List<(DexEntry E, DexState S)> _items = new();
    private readonly Dictionary<string, (Image Color, Image Shadow)> _cache = new();
    private int _sel = -1, _hover = -1;
    public event Action<DexEntry>? Selected;
    public DexEntry? Current => _sel >= 0 && _sel < _items.Count ? _items[_sel].E : null;
    private static readonly Font NumFont = new("Segoe UI", 7.5f);
    private static readonly Font NameFont = new("Segoe UI", 8f);

    public DexGrid()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = Theme.Bg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
    }

    private int Cols => Math.Max(1, (ClientSize.Width - Pad) / (CellW + Pad));

    public void SetItems(List<(DexEntry, DexState)> items)
    {
        var current = Current;
        _items = items;
        _sel = current == null ? -1 : _items.FindIndex(i => i.E == current);
        _hover = -1;
        UpdateScroll();
        Invalidate();
    }

    private void UpdateScroll()
    {
        var rows = (_items.Count + Cols - 1) / Cols;
        AutoScrollMinSize = new Size(0, rows * (CellH + Pad) + Pad);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateScroll(); Invalidate(); }
    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }

    private int HitTest(Point p)
    {
        var y = p.Y - AutoScrollPosition.Y - Pad;
        var x = p.X - Pad;
        if (x < 0 || y < 0) return -1;
        int col = x / (CellW + Pad), row = y / (CellH + Pad);
        if (col >= Cols || x % (CellW + Pad) >= CellW || y % (CellH + Pad) >= CellH) return -1;
        var i = row * Cols + col;
        return i < _items.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var h = HitTest(e.Location);
        if (h < 0) return;
        _sel = h;
        Invalidate();
        Selected?.Invoke(_items[h].E);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        var n = _sel < 0 ? 0 : _sel + e.KeyCode switch { Keys.Left => -1, Keys.Right => 1, Keys.Up => -Cols, Keys.Down => Cols, _ => 0 };
        n = Math.Clamp(n, 0, _items.Count - 1);
        if (n == _sel) return;
        _sel = n;
        var top = Pad + n / Cols * (CellH + Pad);
        if (top + AutoScrollPosition.Y < 0) AutoScrollPosition = new Point(0, top - Pad);
        else if (top + CellH + AutoScrollPosition.Y > ClientSize.Height) AutoScrollPosition = new Point(0, top + CellH + Pad - ClientSize.Height);
        Invalidate();
        Selected?.Invoke(_items[n].E);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, "Nothing matches.", Theme.Body, new Point(20, 20), Theme.Muted);
            return;
        }
        var cols = Cols;
        var scroll = -AutoScrollPosition.Y;
        int firstRow = Math.Max(0, (scroll - Pad) / (CellH + Pad)), lastRow = (scroll + ClientSize.Height) / (CellH + Pad) + 1;
        for (int row = firstRow; row <= lastRow; row++)
        for (int col = 0; col < cols; col++)
        {
            var i = row * cols + col;
            if (i >= _items.Count) return;
            var (entry, state) = _items[i];
            var r = new Rectangle(Pad + col * (CellW + Pad), Pad + row * (CellH + Pad) - scroll, CellW, CellH);
            var missing = state == DexState.Missing;
            var fill = i == _sel ? Theme.Accent : i == _hover ? Theme.PanelHi : Theme.Panel;
            using (var path = Theme.Round(r, 10))
            {
                using var b = new SolidBrush(fill);
                g.FillPath(b, path);
                var border = state == DexState.Shiny ? Theme.Gold : missing ? Theme.Border : Theme.Good;
                using var p = new Pen(border, state == DexState.Missing ? 1f : 1.6f);
                g.DrawPath(p, path);
            }
            var img = GetImage(entry, missing, state == DexState.Shiny);
            if (img != null)
                g.DrawImage(img, new Rectangle(r.X + (CellW - Img) / 2, r.Y + 6, Img, Img));
            TextRenderer.DrawText(g, $"#{entry.Species:0000}", NumFont, new Rectangle(r.X + 4, r.Y + 2, CellW - 8, 14),
                Theme.Muted, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            if (state == DexState.Shiny)
                TextRenderer.DrawText(g, "★", NameFont, new Rectangle(r.Right - 18, r.Y + 1, 16, 16), Theme.Gold, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, entry.Name, NameFont, new Rectangle(r.X + 2, r.Bottom - 30, CellW - 4, 15),
                missing ? Theme.Muted : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (entry.FormName.Length > 0 || entry.Female)
                TextRenderer.DrawText(g, entry.FormName.Length > 0 ? entry.FormName + (entry.Female ? " (female)" : "") : "female", NumFont,
                    new Rectangle(r.X + 2, r.Bottom - 16, CellW - 4, 14), Theme.Accent2, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Sprite (cropped to the Pokémon and scaled) in colour, or as a dark silhouette; the shiny picture when there is one.</summary>
    public Image? GetImage(DexEntry e, bool silhouette, bool shiny = false)
    {
        var sprite = shiny && e.ShinySprite.Length > 0 ? e.ShinySprite : e.Sprite;
        if (sprite.Length == 0) return null;
        if (!_cache.TryGetValue(sprite, out var pair))
        {
            var path = Path.Combine(AppPaths.AssetsDir, sprite);
            if (!File.Exists(path)) return null;
            try
            {
                using var src = new Bitmap(path);
                var color = CropScale(src);
                pair = (color, Shadow(color));
            }
            catch { return null; }
            _cache[sprite] = pair;
        }
        return silhouette ? pair.Shadow : pair.Color;
    }

    private static Bitmap CropScale(Bitmap src)
    {
        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < src.Height; y++)
        for (int x = 0; x < src.Width; x++)
            if (src.GetPixel(x, y).A > 16)
            {
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        if (maxX < 0) return new Bitmap(src);
        var w = maxX - minX + 1; var h = maxY - minY + 1;
        var side = Math.Max(w, h) + 4;
        if (side > Img)
        {
            // big renders (Gen 8/9 sprites): shrink smoothly to the cell size
            var small = new Bitmap(Img, Img, PixelFormat.Format32bppArgb);
            using var gs = Graphics.FromImage(small);
            gs.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gs.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var k = (float)Img / side;
            gs.DrawImage(src, new RectangleF((Img - w * k) / 2, (Img - h * k) / 2, w * k, h * k), new RectangleF(minX, minY, w, h), GraphicsUnit.Pixel);
            return small;
        }
        // integer upscale keeps pixel art crisp
        var scale = Math.Max(1, Img / side);
        var outSide = side * scale;
        var bmp = new Bitmap(outSide, outSide, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(src, new Rectangle((side - w) / 2 * scale, (side - h) / 2 * scale, w * scale, h * scale), new Rectangle(minX, minY, w, h), GraphicsUnit.Pixel);
        return bmp;
    }

    private static Bitmap Shadow(Bitmap color)
    {
        var bmp = new Bitmap(color.Width, color.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        // every visible pixel becomes a flat dark slate colour (alpha kept) = silhouette
        var m = new ColorMatrix(new[]
        {
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 0, 0 },
            new float[] { 0, 0, 0, 1, 0 },
            new float[] { .23f, .22f, .32f, 0, 1 },
        });
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(m);
        g.DrawImage(color, new Rectangle(0, 0, color.Width, color.Height), 0, 0, color.Width, color.Height, GraphicsUnit.Pixel, ia);
        return bmp;
    }

    public void DisposeImages()
    {
        foreach (var (c, s) in _cache.Values) { c.Dispose(); s.Dispose(); }
        _cache.Clear();
    }
}
