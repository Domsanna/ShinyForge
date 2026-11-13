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
    public string SpreadsheetPath { get; set; } = "";  // auto-detected if empty
    public bool AutoMonitor { get; set; }
    public string HuntBot { get; set; } = "";
}

/// <summary>Hunt options chosen in the "Hunt options" window (used by the Python hunter and the 3DS bot).</summary>
public sealed class HuntOptions
{
    // stop rules (besides shinies)
    public string StopSpecies { get; set; } = "";              // comma separated
    public List<string> StopNatures { get; set; } = new();
    public int MinIvTotal { get; set; }                        // 0 = off
    public Dictionary<string, int> MinIvs { get; set; } = new(); // HP Atk Def SpA SpD Spe -> 0..31
    public bool RequireAll { get; set; }
    // optional auto-catch (off by default)
    public string AutoCatch { get; set; } = "off";             // off | non_legendary | all
    public int BallSlot { get; set; } = 1;
    public int MaxBalls { get; set; } = 25;
    // optional shiny RNG manipulation (FireRed/LeafGreen Switch, static encounters) - never on unless the user turns it on
    public bool RngEnabled { get; set; }
    public List<string> RngNatures { get; set; } = new();
    public Dictionary<string, int> RngMinIvs { get; set; } = new();

    public bool HasStopRules => StopSpecies.Trim().Length > 0 || StopNatures.Count > 0 || MinIvTotal > 0 || MinIvs.Values.Any(v => v > 0);
}

public sealed class AppSettings
{
    public HuntOptions Hunt { get; set; } = new();
    public string LastPlatform { get; set; } = "";
    public string SwitchIP { get; set; } = "";   // asked for the first time a Switch feature needs it
    public int SwitchPort { get; set; } = 6000;
    /// <summary>Session-only: remembered while ShinyForge runs, asked again after a restart (never saved to settings.json).</summary>
    [JsonIgnore]
    public string ThreeDSIP { get; set; } = "";
    public string SpreadsheetRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Bot Spreadsheets");
    public string Capture3DSPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "3ds_capture.6.1", "3ds_capture.exe");
    public string StreamlabsPath { get; set; } = @"C:\Program Files\Streamlabs OBS\Streamlabs OBS.exe";
    public string FfmpegPath { get; set; } = "";
    public string PKHeXPath { get; set; } = "";
    public string PythonBotFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Switch 1 Pokemon Bot");
    public string SysBotPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads",
        "Sys-EncounterBot.NET-2.7.1", "Sys-EncounterBot.NET-2.7.1", "SysBot.Pokemon.WinForms", "bin", "x64", "Release", "net47", "SysBot.exe");
    public bool CheckUpdatesOnStart { get; set; } = true;
    /// <summary>A ShinyForge release the user chose to skip ("v1.0.12"); no reminder until a newer one comes out.</summary>
    public string SkippedAppVersion { get; set; } = "";

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
