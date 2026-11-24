using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ShinyForge.Core;

namespace ShinyForge.UI;

/// <summary>
/// Self-test mode (--screenshot DIR): opens the main pages and every window, saves each as a PNG and exits. Lets the
/// macOS / Linux builds be checked on a machine without anyone clicking through them.
/// </summary>
public static class Screenshots
{
    public static async Task Run(string dir, IClassicDesktopStyleApplicationLifetime desktop)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        try
        {
            var main = App.MainWindow!;
            await Settle(2500);
            await Snap(main, Path.Combine(dir, "1_platform.png"), log);
            main.Navigate(() => new GamesPage(true), "Nintendo Switch");
            await Settle(1500);
            await Snap(main, Path.Combine(dir, "2_games_switch.png"), log);
            foreach (var id in new[] { "sw_frlg_lg", "sw_shield", "3ds_umoon" })
            {
                var game = GameCatalog.Get(id);
                main.Navigate(() => new GamePage(game), "Pokémon " + game.Title);
                await Settle(3000);
                await Snap(main, Path.Combine(dir, $"3_game_{id}.png"), log);
            }
            await Window(new SettingsWindow(), Path.Combine(dir, "5_settings.png"), log);
            await Window(new ToolsWindow(true), Path.Combine(dir, "5_tools.png"), log);
            await Window(new HuntOptionsWindow(GameCatalog.Get("sw_frlg_lg"), true), Path.Combine(dir, "5_hunt_options.png"), log);
            await Window(new HuntStatsWindow(), Path.Combine(dir, "5_hunt_stats.png"), log, 2000);
            await Window(new HuntProfilesWindow(), Path.Combine(dir, "5_hunt_profiles.png"), log, 2000);
            await Window(new HealthWindow(), Path.Combine(dir, "5_health.png"), log);
            await Window(new BackupsWindow(), Path.Combine(dir, "5_backups.png"), log);
            await Window(new PokeldnWindow(), Path.Combine(dir, "5_pokeldn.png"), log, 8000);
            log.Add("ALL SCREENS RENDERED");
        }
        catch (Exception e) { log.Add("FAILED: " + e); }
        File.WriteAllLines(Path.Combine(dir, "screens.log"), log);
        desktop.Shutdown();
    }

    private static async Task Settle(int ms)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) await Task.Delay(100);
    }

    private static async Task Window(Window w, string file, List<string> log, int wait = 1200)
    {
        w.Show();
        await Settle(wait);
        await Snap(w, file, log);
        w.Close();
    }

    private static Task Snap(TopLevel t, string file, List<string> log)
    {
        var size = new PixelSize((int)Math.Max(1, t.Bounds.Width), (int)Math.Max(1, t.Bounds.Height));
        using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        rtb.Render(t);
#pragma warning disable CS0618 // the simple PNG save is all a test screenshot needs
        rtb.Save(file);
#pragma warning restore CS0618
        log.Add($"rendered {Path.GetFileName(file)} ({size.Width}x{size.Height})");
        return Task.CompletedTask;
    }
}
