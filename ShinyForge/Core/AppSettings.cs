using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShinyForge.Core;

public sealed class GameSettings
{
    public string GameFolder { get; set; } = "";       // where the game's exported save files live
    public string SaveFilePath { get; set; } = "";     // a specific save the user chose for the tracker (optional)
    public string RecordingFolder { get; set; } = "";
    public int SegmentMinutes { get; set; } = 10;
    public string VideoSource { get; set; } = "";
    public string AudioDevice { get; set; } = "";
    /// <summary>Console frame drawn around the recording: "auto" (or "") = the game's console, "none", or a ConsoleFrames key.</summary>
    public string Overlay { get; set; } = "auto";
    /// <summary>3DS Capture windows: cut off the black border around DS / Game Boy pictures.</summary>
    public bool CropToGame { get; set; } = true;
    public string SpreadsheetPath { get; set; } = "";  // auto-detected if empty
    public bool AutoMonitor { get; set; }
    public string HuntBot { get; set; } = "";
    /// <summary>Stop when one of these shows up (besides any shiny) - see Services.StopTargets.</summary>
    public List<StopTarget> StopTargets { get; set; } = new();
    /// <summary>The Checkpoint backup folder (on the SD card) last copied for this game; set = the next scan is a rescan.</summary>
    public string CheckpointBackup { get; set; } = "";
}

/// <summary>One stop target: a species, a form (-1 = any), a gender (any / male / female) and shiny (any / shiny / star / square / not).</summary>
public sealed class StopTarget
{
    public ushort Species { get; set; }
    public int Form { get; set; } = -1;
    public string Gender { get; set; } = "any";
    public string Shiny { get; set; } = "any";
}

/// <summary>Hunt options chosen in the "Hunt options" window (used by the Python hunter and the 3DS bot).</summary>
public sealed class HuntOptions
{
    // stop rules (besides shinies)
    /// <summary>Old "species, comma separated" box - moved into each game's StopTargets when its Hunt options are saved.</summary>
    public string StopSpecies { get; set; } = "";
    public List<string> StopNatures { get; set; } = new();
    public int MinIvTotal { get; set; }                        // 0 = off
    public Dictionary<string, int> MinIvs { get; set; } = new(); // HP Atk Def SpA SpD Spe -> 0..31
    public bool RequireAll { get; set; }
    // optional auto-catch (off by default)
    public string AutoCatch { get; set; } = "off";             // off | non_legendary | all
    /// <summary>Old setting: the ball's position in the POKé BALLS pocket (used only while <see cref="BallItem"/> is 0).</summary>
    public int BallSlot { get; set; } = 1;
    /// <summary>The ball auto-catch throws, as its Gen 3 item number (<see cref="Balls"/>); the hunter finds it in the bag.</summary>
    public int BallItem { get; set; }
    /// <summary>FireRed / LeafGreen's Poké Balls (Gen 3 item numbers).</summary>
    public static readonly (int Item, string Name)[] Balls =
    {
        (1, "Master Ball"), (2, "Ultra Ball"), (3, "Great Ball"), (4, "Poké Ball"), (6, "Net Ball"), (7, "Dive Ball"), (8, "Nest Ball"),
        (9, "Repeat Ball"), (10, "Timer Ball"), (11, "Luxury Ball"), (12, "Premier Ball"), (5, "Safari Ball"),
    };
    /// <summary>The ball to show as chosen: the one set, or for old settings the Master Ball (position 1) / Poké Ball.</summary>
    [JsonIgnore] public int ChosenBall => BallItem > 0 ? BallItem : BallSlot == 1 ? 1 : 4;
    public int MaxBalls { get; set; } = 25;
    // optional shiny RNG manipulation (FireRed/LeafGreen Switch, static encounters) - never on unless the user turns it on
    public bool RngEnabled { get; set; }
    public List<string> RngNatures { get; set; } = new();
    public Dictionary<string, int> RngMinIvs { get; set; } = new();

    /// <summary>Nature / IV rules set (the stop targets are per game: GameSettings.StopTargets).</summary>
    public bool HasStopRules => StopNatures.Count > 0 || MinIvTotal > 0 || MinIvs.Values.Any(v => v > 0);
}

public sealed class AppSettings
{
    public HuntOptions Hunt { get; set; } = new();
    public string LastPlatform { get; set; } = "";
    public string SwitchIP { get; set; } = "";   // asked for the first time a Switch feature needs it
    public int SwitchPort { get; set; } = 6000;
    /// <summary>
    /// How ShinyForge talks to sys-botbase: "WiFi" (TCP, the Switch can stay docked) or "USB" (cable; the Switch is out of the
    /// dock, so no capture card picture, and the WiFi-only Python hunter can't run). sys-botbase-cpp listens on one of them
    /// at a time (its config.cfg) - <see cref="Services.SysBotBaseInstaller.SetMode"/> changes both together.
    /// </summary>
    public string SwitchConnection { get; set; } = "WiFi";
    /// <summary>
    /// The capture card that shows the Switch, for screenshots while it runs sys-botbase-cpp ("" = found by itself: the
    /// card the Switch games record from, else an Elgato / Game Capture card) - see Services.SwitchScreen.
    /// </summary>
    public string SwitchScreenDevice { get; set; } = "";
    [JsonIgnore] public bool SwitchUsb => SwitchConnection.Equals("USB", StringComparison.OrdinalIgnoreCase);
    /// <summary>Session-only: remembered while ShinyForge runs, asked again after a restart (never saved to settings.json).</summary>
    [JsonIgnore]
    public string ThreeDSIP { get; set; } = "";
    public string SpreadsheetRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Bot Spreadsheets");
    public string Capture3DSPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "3ds_capture.6.1", "3ds_capture.exe");
    /// <summary>Pokémon Automation's SerialPrograms-Windows.cmd (unzipped by the user; found automatically when empty).</summary>
    public string PokemonAutomationPath { get; set; } = "";
    public string StreamlabsPath { get; set; } = @"C:\Program Files\Streamlabs OBS\Streamlabs OBS.exe";
    public string FfmpegPath { get; set; } = "";
    public string PKHeXPath { get; set; } = "";
    public string PythonBotFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Switch 1 Pokemon Bot");
    public string SysBotPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads",
        "Sys-EncounterBot.NET-2.7.1", "Sys-EncounterBot.NET-2.7.1", "SysBot.Pokemon.WinForms", "bin", "x64", "Release", "net47", "SysBot.exe");
    public bool CheckUpdatesOnStart { get; set; } = true;
    /// <summary>A ShinyForge release the user chose to skip ("v1.0.12"); no reminder until a newer one comes out.</summary>
    public string SkippedAppVersion { get; set; } = "";

    // pokeldn (local wireless trades through an ESP32-S3)
    /// <summary>pokeldn.exe chosen by hand (blank = the copy ShinyForge installs in its Tools folder).</summary>
    public string PokeldnPath { get; set; } = "";
    /// <summary>Location of the user's Switch prod.keys. Only the path is stored - the file is never copied.</summary>
    public string ProdKeysPath { get; set; } = "";
    /// <summary>The ESP32-S3 introduction and first-time setup were completed.</summary>
    public bool PokeldnSetupDone { get; set; }

    /// <summary>A Checkpoint saves folder chosen by hand (e.g. a copy of the SD card on the PC); blank = search the drives.</summary>
    public string CheckpointFolder { get; set; } = "";

    // ---- notifications (phone / Discord)
    public string DiscordWebhookUrl { get; set; } = "";
    public bool DiscordMentionEveryone { get; set; }
    public string NtfyTopic { get; set; } = "";
    public string NtfyServer { get; set; } = "https://ntfy.sh";
    public bool NotifyShiny { get; set; } = true;
    public bool NotifyProblems { get; set; } = true;
    public bool NotifyWarnings { get; set; }

    // ---- safety / convenience
    public bool KeepAwake { get; set; } = true;
    public bool AutoBackups { get; set; } = true;
    public int BackupsToKeepSaves { get; set; } = 20;
    public int BackupsToKeepSheets { get; set; } = 3;

    // ---- shiny clips (rolling buffer while hunting)
    public bool ShinyClips { get; set; } = true;
    public int ShinyClipSeconds { get; set; } = 60;

    /// <summary>Folder with the console frame PNGs for recordings (blank = Desktop\Console Frames).</summary>
    public string ConsoleFramesFolder { get; set; } = "";

    // ---- stream overlay + phone remote (small built-in web server)
    public bool OverlayEnabled { get; set; }
    public bool RemoteEnabled { get; set; }
    public int WebPort { get; set; } = 8765;
    public string RemotePin { get; set; } = "";
    public Dictionary<string, GameSettings> Games { get; set; } = new();

    public GameSettings For(string gameId)
    {
        if (!Games.TryGetValue(gameId, out var g))
            Games[gameId] = g = new GameSettings();
        return g;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static AppSettings Current { get; private set; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception e) { AppPaths.Log("Settings load failed: " + e.Message); }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json)); }
        catch (Exception e) { AppPaths.Log("Settings save failed: " + e.Message); }
    }
}
