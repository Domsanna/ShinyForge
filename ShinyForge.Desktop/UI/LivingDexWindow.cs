using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PKHeX.Core;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>Living Dex planner across all your games: every species #1-1025, owned in any save or not, and where to get it.</summary>
public sealed class LivingDexWindow : Window
{
    private readonly DexGrid _grid = new();
    private readonly TextBox _search = Skin.Input("", 200);
    private readonly ComboBox _filter = Skin.Combo(new[] { "All", "Owned", "Missing", "Shiny" }, "All", 130);
    private readonly TextBlock _counts = Skin.T("", 16, Skin.Gold, FontWeight.SemiBold, false);
    private readonly TextBlock _status = Skin.T("", 11.5, Skin.Muted, wrap: false);
    private readonly SelectableTextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Foreground = Skin.B(Skin.Text), FontSize = 12.5 };
    private readonly TextBlock _title = Skin.T("", 20, Skin.Text, FontWeight.SemiBold);
    private readonly Image _sprite = new() { Height = 150 };
    private readonly List<DexEntry> _all = new();
    private readonly Dictionary<ushort, List<(Game Game, Collection.Owned Mon)>> _owned = new();
    private readonly Dictionary<ushort, List<Game>> _availableIn = new();
    private readonly HashSet<string> _gamesWithSave = new();
    private CancellationTokenSource? _infoCts;

    public LivingDexWindow()
    {
        Title = "ShinyForge - Living Dex planner (all your games)";
        Icon = App.Icon;
        Width = 1200; Height = 820;
        Background = Skin.B(Skin.Bg);
        _search.PlaceholderText = "Search name or #";
        var rescan = new FancyButton("Rescan saves", Skin.GlyphSync, Skin.Accent) { Height = 32 };
        rescan.Click += async (_, _) => await Scan();
        var detail = new Border
        {
            Width = 420, Background = Skin.B(Skin.Panel), Padding = new Thickness(14),
            Child = new DockPanel { Children = { new StackPanel { [DockPanel.DockProperty] = Dock.Top, Spacing = 8, Children = { new Border { Background = Skin.B(Skin.PanelHi), CornerRadius = new CornerRadius(8), Child = _sprite }, _title } }, new ScrollViewer { Content = _info, Margin = new Thickness(0, 8, 0, 0) } } },
        };
        var root = new DockPanel();
        void Add(Control c, Dock d) { DockPanel.SetDock(c, d); root.Children.Add(c); }
        Add(new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(10, 8), Child = Skin.Row(_search, _filter, rescan) }, Dock.Top);
        Add(new Border { Padding = new Thickness(12, 6), Child = _counts }, Dock.Top);
        Add(new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(10, 4), Child = _status }, Dock.Bottom);
        Add(detail, Dock.Right);
        root.Children.Add(_grid.InScroll());
        Content = root;
        _grid.Selected += ShowInfo;
        _search.TextChanged += (_, _) => ApplyFilter();
        _filter.SelectionChanged += (_, _) => ApplyFilter();
        Opened += async (_, _) =>
        {
            for (ushort sp = 1; sp <= 1025; sp++)
            {
                var file = Path.Combine("Dex", $"{sp}.png");
                if (!File.Exists(Path.Combine(AppPaths.AssetsDir, file)) && Directory.Exists(DexData.DexDir) &&
                    Directory.EnumerateFiles(DexData.DexDir, $"{sp}-*.png").FirstOrDefault() is { } alt)
                    file = Path.Combine("Dex", Path.GetFileName(alt));
                _all.Add(new DexEntry { Species = sp, Name = DexEntry.Plain(SpeciesName.GetSpeciesName(sp, 2)), Sprite = file });
            }
            ApplyFilter();
            _info.Text = "Scanning your saves...";
            await Scan();
        };
    }

    public async Task Scan()
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
                    Dialogs.Ui(() => _status.Text = $"Reading {g.Title}: {Path.GetFileName(save.Path)}");
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
        var q = (_search.Text ?? "").Trim();
        var f = _filter.SelectedItem as string ?? "All";
        _grid.SetItems(_all.Select(e => (e, StateOf(e)))
            .Where(x => q.Length == 0 || x.e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || x.e.Species.ToString() == q.TrimStart('#', '0'))
            .Where(x => f switch { "Owned" => x.Item2 != DexState.Missing, "Missing" => x.Item2 == DexState.Missing, "Shiny" => x.Item2 == DexState.Shiny, _ => true })
            .ToList());
        var have = _all.Count(e => StateOf(e) != DexState.Missing);
        _counts.Text = $"Living Dex: {have}/1025 ({have * 100 / 1025}%)  •  ★ {_all.Count(e => StateOf(e) == DexState.Shiny)} shiny species";
    }

    private void ShowInfo(DexEntry e)
    {
        _sprite.Source = Sprites.Get(e, StateOf(e) == DexState.Missing);
        _title.Text = $"#{e.Species:0000} {e.Name}";
        var sb = new StringBuilder();
        if (_owned.TryGetValue(e.Species, out var mons) && mons.Count > 0)
        {
            sb.AppendLine($"YOU HAVE {mons.Count}:");
            foreach (var grp in mons.GroupBy(m => m.Game.Title))
                sb.AppendLine($"• {grp.Key}: {grp.Count()}" + (grp.Any(m => m.Mon.Pk.IsShiny) ? $"  (★ {grp.Count(m => m.Mon.Pk.IsShiny)} shiny)" : "") +
                              $"  - {string.Join(", ", grp.Take(3).Select(m => m.Mon.Where))}{(grp.Count() > 3 ? "..." : "")}");
            sb.AppendLine();
        }
        else sb.AppendLine("You don't have this one in any of your saves yet.\n");
        var games = _availableIn.TryGetValue(e.Species, out var gl) ? gl : new();
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
            var more = new StringBuilder();
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
        }).ContinueWith(t => Dialogs.Ui(() => { if (!cts.IsCancellationRequested) _info.Text = sb + (t.IsFaulted ? "" : t.Result); }));
    }
}
