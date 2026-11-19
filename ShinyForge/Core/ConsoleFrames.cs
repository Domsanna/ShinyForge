using System.Text.Json;

namespace ShinyForge.Core;

/// <summary>Where one game screen sits inside a console frame picture (pixels of the full-size PNG).</summary>
public sealed record FrameScreen(string Name, int X, int Y, int Width, int Height);

/// <summary>A console picture (PNG with transparent screen holes) that recordings can be placed inside.</summary>
public sealed record ConsoleFrame(string Key, string Label, string File, int Width, int Height, FrameScreen[] Screens);

/// <summary>
/// The console frame overlays: Game Boy, Game Boy Color, Game Boy Advance, Nintendo DS, Nintendo 3DS, Switch 1 and Switch 2.
/// The PNGs live in a folder chosen in Settings (default Desktop\Console Frames). The screen holes below were measured
/// from the PNGs' transparency; a screen_positions.json in the folder overrides them, so new pictures still line up.
/// </summary>
public static class ConsoleFrames
{
    /// <summary>The frame of the game's own console (Switch games: Switch 1 or Switch 2 from the capture card). Also what "" means.</summary>
    public const string Auto = "auto";
    /// <summary>No frame.</summary>
    public const string None = "none";

    private static readonly ConsoleFrame[] BuiltIn =
    {
        new("gb", "Game Boy", "gameboy_frame.png", 2000, 2800, new[] { new FrameScreen("main", 660, 604, 712, 656) }),
        new("gbc", "Game Boy Color", "gameboy_color_frame.png", 1480, 2480, new[] { new FrameScreen("main", 340, 277, 809, 716) }),
        new("gba", "Game Boy Advance", "gameboy_advance_frame.png", 2946, 1659, new[] { new FrameScreen("main", 825, 300, 1302, 885) }),
        new("nds", "Nintendo DS", "nintendo_ds_frame.png", 1872, 1952, new[] { new FrameScreen("top", 496, 144, 880, 656), new FrameScreen("bottom", 496, 1136, 880, 656) }),
        new("3ds", "Nintendo 3DS", "3DS_frame.png", 2326, 2160, new[] { new FrameScreen("top", 392, 166, 1544, 920), new FrameScreen("bottom", 546, 1102, 1234, 916) }),
        new("switch1", "Nintendo Switch", "switch_one_frame.png", 2856, 1216, new[] { new FrameScreen("main", 654, 174, 1540, 866) }),
        new("switch2", "Nintendo Switch 2", "switch_2_frame.png", 2928, 1248, new[] { new FrameScreen("main", 502, 118, 1920, 1050) }),
    };

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Console Frames");

    public static string Folder => Directory.Exists(AppSettings.Current.ConsoleFramesFolder) ? AppSettings.Current.ConsoleFramesFolder : DefaultFolder;

    /// <summary>All frames, with the hole positions from the folder's screen_positions.json when it has them.</summary>
    public static IReadOnlyList<ConsoleFrame> All
    {
        get
        {
            var json = Path.Combine(Folder, "screen_positions.json");
            if (!System.IO.File.Exists(json)) return BuiltIn;
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(json));
                var measured = new Dictionary<string, (int W, int H, FrameScreen[] S)>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var size = e.GetProperty("image_size");
                    measured[e.GetProperty("file").GetString()!] = (size[0].GetInt32(), size[1].GetInt32(),
                        e.GetProperty("screens").EnumerateArray().Select(s => new FrameScreen(s.GetProperty("screen").GetString()!,
                            s.GetProperty("x").GetInt32(), s.GetProperty("y").GetInt32(), s.GetProperty("width").GetInt32(), s.GetProperty("height").GetInt32())).ToArray());
                }
                return BuiltIn.Select(f => measured.TryGetValue(f.File, out var m) && m.S.Length == f.Screens.Length
                    ? f with { Width = m.W, Height = m.H, Screens = m.S } : f).ToList();
            }
            catch (Exception e) { AppPaths.Log("screen_positions.json unreadable: " + e.Message); return BuiltIn; }
        }
    }

    public static ConsoleFrame? Get(string key) => string.IsNullOrEmpty(key) ? null : All.FirstOrDefault(f => f.Key == key);

    /// <summary>
    /// The frame a recording of a <paramref name="platform"/> game from <paramref name="source"/> gets for the user's choice:
    /// "auto" / "" (and any frame of another console) = the game's own console; Switch games use the Switch 2 frame when the
    /// capture device is the Switch 2's ("USB Video", MS2109 card), otherwise the Switch 1 frame. Null = no frame.
    /// </summary>
    public static ConsoleFrame? Pick(string choice, Platform platform, string source)
    {
        if (choice == None) return null;
        var suits = For(platform).ToList();
        if (suits.FirstOrDefault(f => f.Key == choice) is { } chosen) return chosen;
        if (platform is Platform.GB or Platform.GBC or Platform.GBA or Platform.NDS or Platform.N3DS) return suits.FirstOrDefault();
        bool switch2 = source.Contains("USB Video", StringComparison.OrdinalIgnoreCase) || source.Contains("MS2109", StringComparison.OrdinalIgnoreCase)
                       || source.Contains("Switch 2", StringComparison.OrdinalIgnoreCase);
        return Get(switch2 ? "switch2" : "switch1");
    }

    public static string PathOf(ConsoleFrame f) => Path.Combine(Folder, f.File);

    public static bool Exists(ConsoleFrame f) => System.IO.File.Exists(PathOf(f));

    /// <summary>The frames that suit a game: its own console, or Switch 1 / Switch 2 for Switch games.</summary>
    public static IEnumerable<ConsoleFrame> For(Platform p) => p switch
    {
        Platform.GB => All.Where(f => f.Key == "gb"),
        Platform.GBC => All.Where(f => f.Key == "gbc"),
        Platform.GBA => All.Where(f => f.Key == "gba"),
        Platform.NDS => All.Where(f => f.Key == "nds"),
        Platform.N3DS => All.Where(f => f.Key == "3ds"),
        _ => All.Where(f => f.Key is "switch1" or "switch2"),
    };
}
