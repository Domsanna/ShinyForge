using PKHeX.Core;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// Living Dex planner across ALL your games: every Pokémon HOME box slot (each species, form, regional form, gender look and
/// Alcremie sweet - see <see cref="LivingDex"/>) with Pokémon HOME's pictures, coloured if you own it in any of your saves
/// (which game / box / shiny), silhouette if not - with the games it can be found in (★ = a game you have a save for)
/// and where exactly in those games.
/// </summary>
public sealed class LivingDexForm : Form
{
    private readonly DexGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly TextBox _search = new() { Width = 200, PlaceholderText = "Search name, form or #" };
    private readonly ComboBox _filter = new() { Width = 130 };
    private readonly CheckBox _forms = new() { Text = "Forms && genders", Checked = true, AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
    private readonly Label _counts = new() { AutoSize = true, ForeColor = Theme.Gold, Font = Theme.H2 };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Muted };
    private readonly RichTextBox _info = new() { ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, DetectUrls = true };
    private readonly Label _title = new() { AutoSize = false, Height = 56, Dock = DockStyle.Top, Font = new Font("Segoe UI Semibold", 14f) };
    private readonly PictureBox _sprite = new() { Height = 150, Dock = DockStyle.Top, SizeMode = PictureBoxSizeMode.Zoom };

    private List<DexEntry> _all = new();
    private List<DexEntry> _species = new();
    private HashSet<(ushort, byte)> _genderSplit = new();
    private readonly Dictionary<ushort, List<(Game Game, Collection.Owned Mon)>> _owned = new();
    private readonly Dictionary<string, HashSet<(ushort, byte)>> _formsIn = new();
    private readonly HashSet<string> _gamesWithSave = new();
    private CancellationTokenSource? _infoCts;
    private Image? _bigImage;

    public LivingDexForm()
    {
        Text = "ShinyForge - Living Dex planner (all your games)";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Size = new Size(1240, 840);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(10, 8, 10, 4), BackColor = Theme.Panel };
        Theme.StyleInput(_search);
        Theme.StyleInput(_filter);
        _filter.Items.AddRange(new object[] { "All", "Owned", "Missing", "Shiny" });
        _filter.SelectedIndex = 0;
        _forms.ForeColor = Theme.Text;
        var rescan = new FancyButton("Rescan saves", Theme.GlyphSync, Theme.Accent) { Height = 32, Margin = new Padding(12, 0, 0, 0) };
        bar.Controls.AddRange(new Control[] { _search, _filter, _forms, rescan });
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
        _forms.CheckedChanged += (_, _) => ApplyFilter();
        rescan.Click += async (_, _) => await Scan();
        FormClosed += (_, _) => { _grid.DisposeImages(); _bigImage?.Dispose(); };
        Shown += async (_, _) =>
        {
            _all = LivingDex.Load();
            _species = LivingDex.SpeciesOnly(_all);
            _genderSplit = LivingDex.GenderSplit(_all);
            ApplyFilter();
            _info.Text = "Scanning your saves...";
            await Scan();
        };
    }

    private async Task Scan()
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
                    formsIn[g.Id] = LivingDex.FormsIn(g); // which species and forms exist in this game
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
        _formsIn.Clear(); foreach (var kv in formsIn) _formsIn[kv.Key] = kv.Value;
        _gamesWithSave.Clear(); _gamesWithSave.UnionWith(withSave);
        _status.Text = withSave.Count == 0
            ? "No saves found - set each game's save file (Collection Tracker card) or game folder (Pokémon HOME card)."
            : $"Read {withSave.Count} game save(s): {string.Join(", ", GameCatalog.All.Where(g => withSave.Contains(g.Id)).Select(g => g.Title))}";
        ApplyFilter();
        if (_grid.Current is { } cur) ShowInfo(cur);
        else _info.Text = "Click a Pokémon to see which of your games have it, and where to get the ones you're missing.";
    }

    private bool FormsView => _forms.Checked;

    /// <summary>The Pokémon that fill this slot: the exact form / gender look / sweet, or (species view) any of the species.</summary>
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
        var q = _search.Text.Trim();
        var f = _filter.SelectedItem as string ?? "All";
        var source = FormsView ? _all : _species;
        var states = source.ToDictionary(e => e, StateOf);
        var list = source.Select(e => (e, states[e]))
            .Where(x => q.Length == 0 || x.e.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || x.e.Species.ToString() == q.TrimStart('#', '0'))
            .Where(x => f switch { "Owned" => x.Item2 != DexState.Missing, "Missing" => x.Item2 == DexState.Missing, "Shiny" => x.Item2 == DexState.Shiny, _ => true })
            .ToList();
        _grid.SetItems(list);
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
        var old = _bigImage;
        _bigImage = null;
        var file = Path.Combine(AppPaths.AssetsDir, state == DexState.Shiny && e.ShinySprite.Length > 0 ? e.ShinySprite : e.Sprite);
        if (state != DexState.Missing && File.Exists(file))
            try { using var src = Image.FromFile(file); _bigImage = new Bitmap(src); } catch { }
        _sprite.Image = _bigImage ?? _grid.GetImage(e, state == DexState.Missing);
        old?.Dispose();
        _title.Text = $"#{e.Species:0000} {e.Name}" + (FormsView && (e.FormName.Length > 0 || e.Female)
            ? "\n" + (e.FormName.Length > 0 ? e.FormName : "") + (e.Female ? (e.FormName.Length > 0 ? " - " : "") + "female" : "") : "");
        var sb = new System.Text.StringBuilder();
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
        {
            sb.AppendLine(FormsView && _owned.TryGetValue(e.Species, out var other) && other.Count > 0
                ? $"You don't have this one yet ({other.Count} {e.Name} of other forms / genders).\n"
                : "You don't have this one in any of your saves yet.\n");
        }
        // forms view: games with this exact form (Alolan Vulpix: Gen 7 on); species view: games with any form of it
        var games = GameCatalog.All.Where(g => _formsIn.TryGetValue(g.Id, out var s) &&
                                               (FormsView ? s.Contains((e.Species, e.Form)) : s.Any(x => x.Item1 == e.Species))).ToList();
        sb.AppendLine("IN THESE GAMES (★ = you have a save):");
        sb.AppendLine(games.Count == 0 ? "None of the supported games - get it in Pokémon HOME / an event." : string.Join(", ", games.Select(x => (_gamesWithSave.Contains(x.Id) ? "★ " : "") + x.Title)));
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
                more.AppendLine($"WHERE IN {game.Title.ToUpperInvariant()}:");
                foreach (var l in DexData.WhereToFind(game, LivingDex.ForGame(game, e), 8)) more.AppendLine("  • " + l);
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
