using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ShinyForge.Core;

namespace ShinyForge.UI;

/// <summary>Choose the console.</summary>
public sealed class PlatformPage : UserControl
{
    public event Action<bool>? Chosen; // true = Switch

    public PlatformPage()
    {
        var ds = new PlatformCard(Skin.GlyphUsb, "Nintendo 3DS", "USB capture  •  Game Boy, Game Boy Color, GBA, DS and 3DS games",
            Color.FromRgb(206, 35, 54), Color.FromRgb(70, 10, 30), 6, 384, 487, 716);
        var sw = new PlatformCard(Skin.GlyphGame, "Nintendo Switch", "Capture card  •  Let's Go to Legends: Z-A, plus FireRed/LeafGreen",
            Color.FromRgb(230, 0, 18), Color.FromRgb(30, 30, 40), 25, 888, 1007, 718);
        ds.Clicked += () => Chosen?.Invoke(false);
        sw.Clicked += () => Chosen?.Invoke(true);
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(40, 28, 20, 20), Spacing = 8,
                Children =
                {
                    Skin.Title("Which system are you playing on?"),
                    Skin.MutedText("ShinyForge hunts shinies, records your gameplay, tracks your collection in DexTracker and backs up your Pokémon."),
                    new WrapPanel { Margin = new Thickness(-20, 10, 0, 0), Children = { ds, sw } },
                },
            },
        };
    }
}

/// <summary>The games of one console, grouped by platform.</summary>
public sealed class GamesPage : UserControl
{
    public event Action<Game>? Chosen;

    public GamesPage(bool isSwitch)
    {
        var root = new StackPanel { Margin = new Thickness(28, 18, 20, 30), Spacing = 4 };
        root.Children.Add(Skin.Title(isSwitch ? "Choose your Switch game" : "Choose your game"));
        root.Children.Add(Skin.MutedText("Tiles marked BOT have an automatic shiny-hunting bot. Every game supports recording, collection tracking and save export" +
                                         (isSwitch ? " (Pokémon HOME: Pokémon Automation only)." : ".")));
        foreach (var grp in (isSwitch ? GameCatalog.SwitchTiles : GameCatalog.ForHandheld).GroupBy(g => g.PlatformLabel))
        {
            var header = Skin.T(grp.Key.ToUpperInvariant(), 14, Skin.Gold, FontWeight.SemiBold);
            header.Margin = new Thickness(6, 22, 0, 4);
            root.Children.Add(header);
            var wrap = new WrapPanel();
            foreach (var game in grp)
            {
                var tile = new GameTile(game);
                tile.Clicked += g => Chosen?.Invoke(g);
                wrap.Children.Add(tile);
            }
            root.Children.Add(wrap);
        }
        Content = new ScrollViewer { Content = root };
    }
}
