namespace ShinyForge.Core;

/// <summary>
/// Where ShinyForge keeps its data. Portable mode (a "portable.flag" file next to ShinyForge.exe) keeps
/// everything inside the program folder so it can live on a flash drive / external SSD; otherwise data
/// goes to %AppData%\ShinyForge.
/// </summary>
public static class AppPaths
{
    public static string AppDir { get; } = AppContext.BaseDirectory;
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppDir, "portable.flag"));

    public static string DataDir { get; } = Ensure(IsPortable
        ? Path.Combine(AppDir, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShinyForge"));

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ToolsDir => Ensure(Path.Combine(DataDir, "Tools"));
    public static string LogFile => Path.Combine(DataDir, "shinyforge.log");

    /// <summary>
    /// "Backup Tools" folder: the user can drop programs here (FFmpeg, PKHeX, SysBot, 3DS Capture, portable Python...).
    /// At startup ShinyForge first looks for each tool on the computer, then in this folder. Portable: next to ShinyForge.exe;
    /// installed: Documents\ShinyForge\Backup Tools (easy to find).
    /// </summary>
    public static string BackupToolsDir { get; } = IsPortable
        ? Path.Combine(AppDir, "Backup Tools")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShinyForge", "Backup Tools");

    /// <summary>
    /// Copies of saves ShinyForge took from Checkpoint (and their Pokémon HOME exports): portable = next to ShinyForge.exe,
    /// installed = Documents\ShinyForge\Saves. Saves are always copied here first - ShinyForge never writes to the SD card.
    /// </summary>
    public static string SavesDir { get; } = IsPortable
        ? Path.Combine(AppDir, "Saves")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShinyForge", "Saves");

    /// <summary>Creates the backup folder and its README (safe to call every start).</summary>
    public static void EnsureBackupToolsDir()
    {
        try
        {
            Directory.CreateDirectory(BackupToolsDir);
            var readme = Path.Combine(BackupToolsDir, "README.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme,
                    "SHINYFORGE BACKUP TOOLS FOLDER\r\n" +
                    "==============================\r\n\r\n" +
                    "Every time ShinyForge starts it first looks for the programs it needs on this computer.\r\n" +
                    "Anything it can't find there, it looks for in THIS folder (including sub-folders).\r\n\r\n" +
                    "Put the program folders (unzipped) or files in here, for example:\r\n" +
                    "  FFmpeg            ...\\ffmpeg.exe\r\n" +
                    "  PKHeX             ...\\PKHeX.exe\r\n" +
                    "  Sys-EncounterBot  ...\\SysBot.exe   (keep its whole folder together)\r\n" +
                    "  3DS Capture       ...\\3ds_capture.exe\r\n" +
                    "  Python 3          ...\\python.exe   (the 'Windows embeddable package' works)\r\n" +
                    "  Streamlabs        ...\\Streamlabs OBS.exe\r\n\r\n" +
                    "Microsoft Excel can't be used from here - it has to be installed normally.\r\n" +
                    "Restart ShinyForge (or press 'Check for updates' in Tools & Updates) after adding something.\r\n");
        }
        catch (Exception e) { Log("Backup tools folder: " + e.Message); }
    }

    /// <summary>Finds the first of <paramref name="exeNames"/> anywhere inside the backup folder (sub-folders included).</summary>
    public static string? FindInBackupTools(params string[] exeNames)
    {
        if (exeNames.Length == 0 || !Directory.Exists(BackupToolsDir)) return null;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        foreach (var name in exeNames)
        {
            try
            {
                // Several copies (e.g. a custom SysBot build next to the official one): the newest file wins.
                var hit = Directory.EnumerateFiles(BackupToolsDir, name, opts).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (hit != null) return hit;
            }
            catch { }
        }
        return null;
    }

    /// <summary>The ShinyForge Python bot folder: the one set in Settings, else a copy in the Backup Tools folder.</summary>
    public static string? FindPythonBotFolder(string configured)
    {
        static bool IsBot(string dir) => File.Exists(Path.Combine(dir, "start_hunt.bat")) && Directory.Exists(Path.Combine(dir, "shinybot"));
        if (!string.IsNullOrWhiteSpace(configured) && IsBot(configured)) return configured;
        var bundled = Path.Combine(AppDir, "PythonBot"); // shipped with ShinyForge
        if (IsBot(bundled)) return bundled;
        var bat = FindInBackupTools("start_hunt.bat");
        if (bat != null && IsBot(Path.GetDirectoryName(bat)!)) return Path.GetDirectoryName(bat);
        if (!Directory.Exists(BackupToolsDir)) return null;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        return Directory.EnumerateFiles(BackupToolsDir, "start_hunt.bat", opts).Select(Path.GetDirectoryName).FirstOrDefault(d => d != null && IsBot(d));
    }

    public static bool IsInBackupTools(string? path) =>
        path != null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(BackupToolsDir), StringComparison.OrdinalIgnoreCase);
    public static string AssetsDir => Path.Combine(AppDir, "Assets");
    public static string ArtDir => Path.Combine(AssetsDir, "Art");
    /// <summary>Drop official game logo images here (named by game id, e.g. "gba_firered.png" or "sw_scarlet.png") to show them on the tiles.</summary>
    public static string LogoDir => Path.Combine(AssetsDir, "Logos");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Log(string message)
    {
        try { File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}"); }
        catch { /* logging must never crash the app */ }
    }
}
