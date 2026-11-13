using PKHeX.Core;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// Living Dex planner across ALL your games: every species #1-1025, coloured if you own it in any of your saves
/// (which game / box / shiny), silhouette if not - with the games it can be found in (★ = a game you have a save for)
/// and where exactly in those games.
/// </summary>
public sealed class LivingDexForm : Form
{
    private readonly DexGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly TextBox _search = new() { Width = 200, PlaceholderText = "Search name or #" };
    private readonly ComboBox _filter = new() { Width = 130 };
    private readonly Label _counts = new() { AutoSize = true, ForeColor = Theme.Gold, Font = Theme.H2 };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Muted };
    private readonly RichTextBox _info = new() { ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, DetectUrls = true };
    private readonly Label _title = new() { AutoSize = false, Height = 36, Dock = DockStyle.Top, Font = new Font("Segoe UI Semibold", 15f) };
    private readonly PictureBox _sprite = new() { Height = 150, Dock = DockStyle.Top, SizeMode = PictureBoxSizeMode.Zoom };

    private readonly List<DexEntry> _all = new();
    private readonly Dictionary<ushort, List<(Game Game, Collection.Owned Mon)>> _owned = new();
    private readonly Dictionary<ushort, List<Game>> _availableIn = new();
    private readonly HashSet<string> _gamesWithSave = new();
    private CancellationTokenSource? _infoCts;

    public LivingDexForm()
    {
        Text = "ShinyForge - Living Dex planner (all your games)";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Size = new Size(1200, 820);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(10, 8, 10, 4), BackColor = Theme.Panel };
        Theme.StyleInput(_search);
        Theme.StyleInput(_filter);
        _filter.Items.AddRange(new object[] { "All", "Owned", "Missing", "Shiny" });
        _filter.SelectedIndex = 0;
        var rescan = new FancyButton("Rescan saves", Theme.GlyphSync, Theme.Accent) { Height = 32, Margin = new Padding(12, 0, 0, 0) };
        bar.Controls.AddRange(new Control[] { _search, _filter, rescan });
        var countsBar = new Panel { Dock = DockStyle.Top, Height = 34 };
        _counts.Location = new Point(12, 6);
        countsBar.Controls.Add(_counts);
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = Theme.Panel };
        _status.Location = new Point(10, 5);
        statusBar.Controls.Add(_status);

        var detail = new Panel { Dock = DockStyle.Right, Width = 420, BackColor = Theme.Panel, Padding = new Padding(14) };
        _info.BackColor = Theme.Panel;
        _info.ForeColor = Theme.Text;
        _info.Font = new Font("Segoe UI", 9.5f);
        _sprite.BackColor = Theme.PanelHi;
        _title.ForeColor = Theme.Text;
        detail.Controls.Add(_info);
        detail.Controls.Add(_title);
        detail.Controls.Add(_sprite);
        _info.LinkClicked += (_, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.LinkText!) { UseShellExecute = true }); } catch { } };

        Controls.Add(_grid);
        Controls.Add(detail);
        Controls.Add(statusBar);
        Controls.Add(countsBar);
        Controls.Add(bar);

        _grid.Selected += ShowInfo;
        _search.TextChanged += (_, _) => ApplyFilter();
        _filter.SelectedIndexChanged += (_, _) => ApplyFilter();
        rescan.Click += async (_, _) => await Scan();
        FormClosed += (_, _) => _grid.DisposeImages();
        Shown += async (_, _) =>
        {
            for (ushort sp = 1; sp <= 1025; sp++)
            {
                var name = DexEntry.Plain(SpeciesName.GetSpeciesName(sp, 2));
                _all.Add(new DexEntry { Species = sp, Name = name, Sprite = Path.Combine("Dex", $"{sp}.png") });
            }
            FixSprites();
            ApplyFilter();
            _info.Text = "Scanning your saves...";
            await Scan();
        };
    }

    /// <summary>Species whose default sprite has a form name (Unown A, Burmy...) - use the first file the generic set has.</summary>
    private void FixSprites()
    {
        for (int i = 0; i < _all.Count; i++)
        {
            var e = _all[i];
            if (File.Exists(Path.Combine(AppPaths.AssetsDir, e.Sprite))) continue;
            var alt = Directory.EnumerateFiles(DexData.DexDir, $"{e.Species}-*.png").FirstOrDefault();
            if (alt != null) _all[i] = new DexEntry { Species = e.Species, Name = e.Name, Sprite = Path.Combine("Dex", Path.GetFileName(alt)) };
        }
    }

    private async Task Scan()
    {
        _status.Text = "Reading the saves of every game...";
        var owned = new Dictionary<ushort, List<(Game, Collection.Owned)>>();
        var withSave = new HashSet<string>();
        var available = new Dictionary<ushort, List<Game>>();
        await Task.Run(() =>
        {
            foreach (var g in GameCatalog.All)
            {
                try
                {
                    // which species exist in this game
                    foreach (var sp in DexData.Build(g, includeForms: false).Select(e => e.Species).Distinct())
                    {
                        if (!available.TryGetValue(sp, out var l)) available[sp] = l = new();
                        l.Add(g);
                    }
                    var gs = AppSettings.Current.For(g.Id);
                    FoundSave? save = null;
                    if (!string.IsNullOrWhiteSpace(gs.SaveFilePath) && File.Exists(gs.SaveFilePath) && SaveUtil.TryGetSaveFile(gs.SaveFilePath, out var sv) && sv != null)
                        save = new FoundSave(gs.SaveFilePath, sv);
                    else if (!string.IsNullOrWhiteSpace(gs.GameFolder) && Directory.Exists(gs.GameFolder))
                        save = SaveService.FindSaves(gs.GameFolder, g).FirstOrDefault();
                    if (save == null) continue;
                    BeginInvoke(() => _status.Text = $"Reading {g.Title}: {Path.GetFileName(save.Path)}");
                    withSave.Add(g.Id);
                    foreach (var m in Collection.FromSave(save.Save, save.Path).Mons)
                    {
                        if (!owned.TryGetValue(m.Pk.Species, out var l)) owned[m.Pk.Species] = l = new();
                        l.Add((g, m));
                    }
                }
                catch (Exception e) { AppPaths.Log($"Living dex: {g.Id}: {e.Message}"); }
            }
        });
        _owned.Clear(); foreach (var kv in owned) _owned[kv.Key] = kv.Value;
        _availableIn.Clear(); foreach (var kv in available) _availableIn[kv.Key] = kv.Value;
        _gamesWithSave.Clear(); _gamesWithSave.UnionWith(withSave);
        _status.Text = withSave.Count == 0
            ? "No saves found - set each game's save file (Collection Tracker card) or game folder (Pokémon HOME card)."
            : $"Read {withSave.Count} game save(s): {string.Join(", ", GameCatalog.All.Where(g => withSave.Contains(g.Id)).Select(g => g.Title))}";
        ApplyFilter();
        if (_grid.Current is { } cur) ShowInfo(cur);
        else _info.Text = "Click a Pokémon to see which of your games have it, and where to get the ones you're missing.";
    }

    private DexState StateOf(DexEntry e) =>
        !_owned.TryGetValue(e.Species, out var l) || l.Count == 0 ? DexState.Missing : l.Any(x => x.Mon.Pk.IsShiny) ? DexState.Shiny : DexState.Owned;

    private void ApplyFilter()
    {
        var q = _search.Text.Trim();
        var f = _filter.SelectedItem as string ?? "All";
        var list = _all.Select(e => (e, StateOf(e)))
            .Where(x => q.Length == 0 || x.e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.e.Species.ToString() == q.TrimStart('#', '0'))
            .Where(x => f switch { "Owned" => x.Item2 != DexState.Missing, "Missing" => x.Item2 == DexState.Missing, "Shiny" => x.Item2 == DexState.Shiny, _ => true })
            .ToList();
        _grid.SetItems(list);
        var have = _all.Count(e => StateOf(e) != DexState.Missing);
        _counts.Text = $"Living Dex: {have}/1025 ({have * 100 / 1025}%)  •  ★ {_all.Count(e => StateOf(e) == DexState.Shiny)} shiny species";
    }

    private void ShowInfo(DexEntry e)
    {
        _sprite.Image = _grid.GetImage(e, StateOf(e) == DexState.Missing);
        _title.Text = $"#{e.Species:0000} {e.Name}";
        var sb = new System.Text.StringBuilder();
        if (_owned.TryGetValue(e.Species, out var mons) && mons.Count > 0)
        {
            sb.AppendLine($"YOU HAVE {mons.Count}:");
            foreach (var grp in mons.GroupBy(m => m.Game.Title))
                sb.AppendLine($"• {grp.Key}: {grp.Count()}" + (grp.Any(m => m.Mon.Pk.IsShiny) ? $"  (★ {grp.Count(m => m.Mon.Pk.IsShiny)} shiny)" : "") +
                              $"  - {string.Join(", ", grp.Take(3).Select(m => m.Mon.Where))}{(grp.Count() > 3 ? "..." : "")}");
            sb.AppendLine();
        }
        else sb.AppendLine("You don't have this one in any of your saves yet.\n");
        var games = _availableIn.TryGetValue(e.Species, out var g) ? g : new();
        sb.AppendLine("IN THESE GAMES (★ = you have a save):");
        sb.AppendLine(games.Count == 0 ? "None of the supported games." : string.Join(", ", games.Select(x => (_gamesWithSave.Contains(x.Id) ? "★ " : "") + x.Title)));
        sb.AppendLine();
        _info.Text = sb + "Looking up where to find it in your games...";
        _infoCts?.Cancel();
        var cts = _infoCts = new CancellationTokenSource();
        var mine = games.Where(x => _gamesWithSave.Contains(x.Id)).Reverse().Take(3).ToList();
        if (mine.Count == 0) mine = games.AsEnumerable().Reverse().Take(2).ToList();
        Task.Run(() =>
        {
            var more = new System.Text.StringBuilder();
            foreach (var game in mine)
            {
                if (cts.IsCancellationRequested) return "";
                var entry = DexData.Build(game, false).FirstOrDefault(x => x.Species == e.Species);
                if (entry == null) continue;
                more.AppendLine($"WHERE IN {game.Title.ToUpperInvariant()}:");
                foreach (var l in DexData.WhereToFind(game, entry, 8)) more.AppendLine("  • " + l);
                more.AppendLine();
            }
            more.AppendLine(DexData.BulbapediaUrl(e));
            return more.ToString();
        }).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || IsDisposed) return;
            try { BeginInvoke(() => { if (!cts.IsCancellationRequested) _info.Text = sb + (t.IsFaulted ? "" : t.Result); }); } catch { }
        });
    }
}
