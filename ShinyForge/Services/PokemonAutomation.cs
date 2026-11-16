using System.Diagnostics;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Pokémon Automation's Computer Control ("SerialPrograms", pokemonautomation.github.io): ready-made, tested programs for
/// every Switch Pokémon game - shiny hunts, egg hatching, raids, auto-saving - that work on an UNMODDED Switch through a
/// microcontroller (ESP32-S3, Pico W, ...) as the controller and a capture card as the eyes, or on a modded one through
/// sys-botbase. ShinyForge doesn't copy or bundle any of it (their programs are for personal, non-profit use; other uses need
/// their Discord admins' OK): the user unzips it, ShinyForge finds it, starts it, and links the right program list per game.
/// </summary>
public static class PokemonAutomation
{
    public const string Site = "https://pokemonautomation.github.io/";
    public const string Releases = "https://github.com/PokemonAutomation/ComputerControl/releases/latest";
    public const string SetupGuide = Site + "SetupGuide/index.html";
    private const string Launcher = "SerialPrograms-Windows.cmd";

    /// <summary>Its program list for this game (or the general Switch programs).</summary>
    public static string ProgramsUrl(Game game) => Site + "Programs/index.html#" + (game.Id switch
    {
        "sw_lgp" or "sw_lge" => "pokemon-lets-go-pikachueevee-lgpe",
        "sw_sword" or "sw_shield" => "pokemon-swordshield",
        "sw_bd" or "sw_sp" => "pokemon-brilliant-diamondshining-pearl",
        "sw_pla" => "pokemon-legends-arceus",
        "sw_scarlet" or "sw_violet" => "pokemon-scarlet-and-violet",
        "sw_za" => "pokemon-legends-z-a",
        "sw_frlg_fr" or "sw_frlg_lg" => "pokemon-firered-and-leafgreen-nintendo-switch",
        _ => "general-nintendo-switch-programs",
    });

    /// <summary>
    /// SerialPrograms-Windows.cmd: the path in Settings, the Backup Tools folder, or a "PA-SerialPrograms-*" folder in Downloads,
    /// Desktop or Documents (up to two folders deep). Windows only (the Mac version is an app the user opens).
    /// </summary>
    public static string? Find()
    {
        if (!OsInfo.IsWindows) return null;
        var set = AppSettings.Current.PokemonAutomationPath;
        if (File.Exists(set)) return set;
        if (AppPaths.FindInBackupTools(Launcher) is { } b) return b;
        foreach (var root in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments })
        {
            var home = Environment.GetFolderPath(root);
            var bases = root == Environment.SpecialFolder.UserProfile ? new[] { Path.Combine(home, "Downloads") } : new[] { home };
            foreach (var dir in bases.Where(Directory.Exists))
            {
                var hit = SafeDirs(dir).Where(d => Path.GetFileName(d).StartsWith("PA-SerialPrograms", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(d => new[] { d }.Concat(SafeDirs(d)))
                    .Select(d => Path.Combine(d, Launcher)).FirstOrDefault(File.Exists);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    /// <summary>Starts Computer Control (its own launcher, in its own folder). False when it isn't found.</summary>
    public static bool Launch()
    {
        var cmd = Find();
        if (cmd == null) return false;
        Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(cmd)! });
        AppPaths.Log("Pokémon Automation started: " + cmd);
        return true;
    }

    /// <summary>Opens one of its web pages in the default browser (Windows, macOS, Linux).</summary>
    public static void Open(string url) => OsInfo.Open(url);
}
