namespace ShinyForge.Core;

/// <summary>
/// Where ShinyForge keeps its data. Portable mode (a "portable.flag" file next to ShinyForge.exe - on macOS next to
/// ShinyForge.app) keeps everything inside the program folder so it can live on a flash drive / external SSD; otherwise
/// data goes to %AppData%\ShinyForge (Windows), ~/Library/Application Support/ShinyForge (macOS) or
/// ~/.local/share/ShinyForge (Linux).
/// </summary>
public static class AppPaths
{
    /// <summary>The program's own files (Assets, PythonBot, the guide...).</summary>
    public static string AppDir { get; } = AppContext.BaseDirectory;
    /// <summary>The folder the user sees the program in (next to ShinyForge.exe / ShinyForge.app): portable data goes here.</summary>
    public static string LaunchDir { get; } = OsInfo.LaunchDir;
    public static bool IsPortable { get; } = File.Exists(Path.Combine(LaunchDir, "portable.flag"));

    public static string DataDir { get; } = Ensure(IsPortable
        ? Path.Combine(LaunchDir, "Data")
        : Path.Combine(OsInfo.DataRoot, "ShinyForge"));

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ToolsDir => Ensure(Path.Combine(DataDir, "Tools"));
    public static string LogFile => Path.Combine(DataDir, "shinyforge.log");

    /// <summary>
    /// "Backup Tools" folder: the user can drop programs here (FFmpeg, PKHeX, SysBot, 3DS Capture, portable Python...).
    /// At startup ShinyForge first looks for each tool on the computer, then in this folder. Portable: next to ShinyForge.exe;
    /// installed: Documents\ShinyForge\Backup Tools (easy to find).
    /// </summary>
    public static string BackupToolsDir { get; } = IsPortable
        ? Path.Combine(LaunchDir, "Backup Tools")
        : Path.Combine(OsInfo.DocumentsDir, "ShinyForge", "Backup Tools");

    /// <summary>
    /// Copies of saves ShinyForge took from Checkpoint (and their Pokémon HOME exports): portable = next to ShinyForge.exe,
    /// installed = Documents\ShinyForge\Saves. Saves are always copied here first - ShinyForge never writes to the SD card.
    /// </summary>
    public static string SavesDir { get; } = IsPortable
        ? Path.Combine(LaunchDir, "Saves")
        : Path.Combine(OsInfo.DocumentsDir, "ShinyForge", "Saves");

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
                    (OsInfo.IsWindows ? "" : "On macOS and Linux: FFmpeg, Python 3, pokeldn and esptool (the program files without .exe).\r\n") +
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
        static bool IsBot(string dir) => Directory.Exists(Path.Combine(dir, "shinybot")) && File.Exists(Path.Combine(dir, "config.yaml"));
        if (!string.IsNullOrWhiteSpace(configured) && IsBot(configured)) return configured;
        var bundled = Path.Combine(AppDir, "PythonBot"); // shipped with ShinyForge
        if (IsBot(bundled))
        {
            // Installed macOS / Linux apps live in read-only places (/opt, an .app bundle): the hunter runs from a copy in
            // the data folder, where it can write its logs. Program files are refreshed from each new version; the
            // user's config.yaml and logs are kept.
            if (OsInfo.IsWindows || IsPortable) return bundled;
            var working = Path.Combine(DataDir, "PythonBot");
            try
            {
                foreach (var file in Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(bundled, file);
                    if (rel.Contains("__pycache__")) continue;
                    var target = Path.Combine(working, rel);
                    if (File.Exists(target) && (rel == "config.yaml" || File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(file))) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                }
                return working;
            }
            catch (Exception e) { Log("Copying the Python hunter to the data folder failed: " + e.Message); return bundled; }
        }
        if (!Directory.Exists(BackupToolsDir)) return null;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        return Directory.EnumerateFiles(BackupToolsDir, "config.yaml", opts).Select(Path.GetDirectoryName).FirstOrDefault(d => d != null && IsBot(d));
    }

    public static bool IsInBackupTools(string? path) =>
        path != null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(BackupToolsDir), OsInfo.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
