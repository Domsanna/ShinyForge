using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PKHeX.Core;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>Living Dex planner across all your games: every Pokémon HOME box slot (species, forms, regional forms, gender
/// looks, Alcremie sweets - see <see cref="LivingDex"/>) with Pokémon HOME's pictures, owned in any save or not, and where to get it.</summary>
public sealed class LivingDexWindow : Window
{
    private readonly DexGrid _grid = new();
    private readonly TextBox _search = Skin.Input("", 200);
    private readonly ComboBox _filter = Skin.Combo(new[] { "All", "Owned", "Missing", "Shiny" }, "All", 130);
    private readonly CheckBox _forms = new() { Content = "Forms & genders", IsChecked = true, Foreground = Skin.B(Skin.Text), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _counts = Skin.T("", 16, Skin.Gold, FontWeight.SemiBold, false);
    private readonly TextBlock _status = Skin.T("", 11.5, Skin.Muted, wrap: false);
    private readonly SelectableTextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Foreground = Skin.B(Skin.Text), FontSize = 12.5 };
    private readonly TextBlock _title = Skin.T("", 20, Skin.Text, FontWeight.SemiBold);
    private readonly Image _sprite = new() { Height = 150 };
    private List<DexEntry> _all = new();
    private List<DexEntry> _species = new();
    private HashSet<(ushort, byte)> _genderSplit = new();
    private readonly Dictionary<ushort, List<(Game Game, Collection.Owned Mon)>> _owned = new();
    private readonly Dictionary<string, HashSet<(ushort, byte)>> _formsIn = new();
    private readonly HashSet<string> _gamesWithSave = new();
    private Bitmap? _big; // the detail picture (disposed when the next one is shown)

    public LivingDexWindow()
    {
        Title = "ShinyForge - Living Dex planner (all your games)";
        Icon = App.Icon;
        Width = 1240; Height = 840;
        Background = Skin.B(Skin.Bg);
        _search.PlaceholderText = "Search name, form or #";
        var rescan = new FancyButton("Rescan saves", Skin.GlyphSync, Skin.Accent) { Height = 32 };
        rescan.Click += async (_, _) => await Scan();
        var detail = new Border
        {
            Width = 420, Background = Skin.B(Skin.Panel), Padding = new Thickness(14),
            Child = new DockPanel { Children = { new StackPanel { [DockPanel.DockProperty] = Dock.Top, Spacing = 8, Children = { new Border { Background = Skin.B(Skin.PanelHi), CornerRadius = new CornerRadius(8), Child = _sprite }, _title } }, new ScrollViewer { Content = _info, Margin = new Thickness(0, 8, 0, 0) } } },
        };
        var root = new DockPanel();
        void Add(Control c, Dock d) { DockPanel.SetDock(c, d); root.Children.Add(c); }
        Add(new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(10, 8), Child = Skin.Row(_search, _filter, _forms, rescan) }, Dock.Top);
        Add(new Border { Padding = new Thickness(12, 6), Child = _counts }, Dock.Top);
        Add(new Border { Background = Skin.B(Skin.Panel), Padding = new Thickness(10, 4), Child = _status }, Dock.Bottom);
        Add(detail, Dock.Right);
        root.Children.Add(_grid.InScroll());
        Content = root;
        _grid.Selected += ShowInfo;
        _search.TextChanged += (_, _) => ApplyFilter();
        _filter.SelectionChanged += (_, _) => ApplyFilter();
        _forms.IsCheckedChanged += (_, _) => ApplyFilter();
        Opened += async (_, _) =>
        {
            _all = LivingDex.Load();
            _species = LivingDex.SpeciesOnly(_all);
            _genderSplit = LivingDex.GenderSplit(_all);
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
        var formsIn = new Dictionary<string, HashSet<(ushort, byte)>>();
        await Task.Run(() =>
        {
            foreach (var g in GameCatalog.All)
            {
                try
                {
                    formsIn[g.Id] = LivingDex.FormsIn(g);
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
        _formsIn.Clear(); foreach (var kv in formsIn) _formsIn[kv.Key] = kv.Value;
        _gamesWithSave.Clear(); _gamesWithSave.UnionWith(withSave);
        _status.Text = withSave.Count == 0
            ? "No saves found - set each game's save file (Collection Tracker card) or game folder (Pokémon HOME card)."
            : $"Read {withSave.Count} game save(s): {string.Join(", ", GameCatalog.All.Where(g => withSave.Contains(g.Id)).Select(g => g.Title))}";
        ApplyFilter();
        if (_grid.Current is { } cur) ShowInfo(cur);
        else _info.Text = "Click a Pokémon to see which of your games have it, and where to get the ones you're missing.";
    }

    private bool FormsView => _forms.IsChecked == true;

    private List<(Game Game, Collection.Owned Mon)> MonsFor(DexEntry e) =>
        !_owned.TryGetValue(e.Species, out var l) ? new()
        : FormsView ? l.Where(m => LivingDex.Fills(e, m.Mon.Pk, _genderSplit)).ToList() : l;

    private DexState StateOf(DexEntry e)
    {
        var mons = MonsFor(e);
        return mons.Count == 0 ? DexState.Missing : mons.Any(x => x.Mon.Pk.IsShiny) ? DexState.Shiny : DexState.Owned;
    }

    private void ApplyFilter()
    {
        var q = (_search.Text ?? "").Trim();
        var f = _filter.SelectedItem as string ?? "All";
        var source = FormsView ? _all : _species;
        var states = source.ToDictionary(e => e, StateOf);
        _grid.SetItems(source.Select(e => (e, states[e]))
            .Where(x => q.Length == 0 || x.e.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || x.e.Species.ToString() == q.TrimStart('#', '0'))
            .Where(x => f switch { "Owned" => x.Item2 != DexState.Missing, "Missing" => x.Item2 == DexState.Missing, "Shiny" => x.Item2 == DexState.Shiny, _ => true })
            .ToList());
        var have = states.Count(s => s.Value != DexState.Missing);
        var speciesHave = _species.Count(e => _owned.ContainsKey(e.Species));
        _counts.Text = FormsView
            ? $"Living Dex: {have}/{source.Count} forms ({have * 100 / Math.Max(1, source.Count)}%)  •  {speciesHave}/{_species.Count} species  •  ★ {states.Count(s => s.Value == DexState.Shiny)} shiny"
            : $"Living Dex: {have}/{source.Count} species ({have * 100 / Math.Max(1, source.Count)}%)  •  ★ {states.Count(s => s.Value == DexState.Shiny)} shiny species";
    }

    private void ShowInfo(DexEntry e)
    {
        var state = StateOf(e);
        // the detail picture: Pokémon HOME's render at full size (shiny when you have a shiny one), silhouette when missing
        var file = Path.Combine(AppPaths.AssetsDir, (state == DexState.Shiny && e.ShinySprite.Length > 0 ? e.ShinySprite : e.Sprite).Replace('\\', Path.DirectorySeparatorChar));
        Bitmap? big = null;
        if (state != DexState.Missing && File.Exists(file)) try { big = new Bitmap(file); } catch { }
        var old = _big;
        _big = big;
        _sprite.Source = big ?? Sprites.Get(e, state == DexState.Missing);
        old?.Dispose();
        _title.Text = $"#{e.Species:0000} {e.Name}" + (FormsView && (e.FormName.Length > 0 || e.Female)
            ? "\n" + (e.FormName.Length > 0 ? e.FormName : "") + (e.Female ? (e.FormName.Length > 0 ? " - " : "") + "female" : "") : "");
        var sb = new StringBuilder();
        var mons = MonsFor(e);
        if (mons.Count > 0)
        {
            sb.AppendLine($"YOU HAVE {mons.Count}:");
            foreach (var grp in mons.GroupBy(m => m.Game.Title))
                sb.AppendLine($"• {grp.Key}: {grp.Count()}" + (grp.Any(m => m.Mon.Pk.IsShiny) ? $"  (★ {grp.Count(m => m.Mon.Pk.IsShiny)} shiny)" : "") +
                              $"  - {string.Join(", ", grp.Take(3).Select(m => m.Mon.Where))}{(grp.Count() > 3 ? "..." : "")}");
            sb.AppendLine();
        }
        else
            sb.AppendLine(FormsView && _owned.TryGetValue(e.Species, out var other) && other.Count > 0
                ? $"You don't have this one yet ({other.Count} {e.Name} of other forms / genders).\n"
                : "You don't have this one in any of your saves yet.\n");
        // forms view: games with this exact form (Alolan Vulpix: Gen 7 on); species view: games with any form of it
        var games = GameCatalog.All.Where(g => _formsIn.TryGetValue(g.Id, out var s) &&
                                               (FormsView ? s.Contains((e.Species, e.Form)) : s.Any(x => x.Item1 == e.Species))).ToList();
        sb.AppendLine("IN THESE GAMES (★ = you have a save):");
        sb.AppendLine(games.Count == 0 ? "None of the supported games - get it in Pokémon HOME / an event." : string.Join(", ", games.Select(x => (_gamesWithSave.Contains(x.Id) ? "★ " : "") + x.Title)));
        sb.AppendLine();
        sb.AppendLine("Where to find it: open that game's Pokédex.");
        sb.AppendLine();
        sb.AppendLine(DexData.BulbapediaUrl(e));
        _info.Text = sb.ToString();
    }
}
