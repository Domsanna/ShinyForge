using System.Diagnostics;

namespace ShinyForge.Core;

/// <summary>Which operating system ShinyForge runs on, and the few things that differ between Windows, macOS and Linux.</summary>
public static class OsInfo
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMac => OperatingSystem.IsMacOS();
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static string Name => IsWindows ? "Windows" : IsMac ? "macOS" : "Linux";

    /// <summary>"ffmpeg" → "ffmpeg.exe" on Windows.</summary>
    public static string Exe(string name) => IsWindows ? name + ".exe" : name;

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The user's Documents folder (on Linux/macOS ~/Documents, created if needed).</summary>
    public static string DocumentsDir
    {
        get
        {
            if (IsWindows) return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var xdg = IsLinux ? XdgUserDir("DOCUMENTS") : null;
            return xdg ?? Path.Combine(Home, "Documents");
        }
    }

    /// <summary>Where installed (not portable) ShinyForge keeps settings and data.</summary>
    public static string DataRoot =>
        IsWindows ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        : IsMac ? Path.Combine(Home, "Library", "Application Support")
        : Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".local", "share");

    /// <summary>
    /// The folder the user sees the program in: next to ShinyForge.exe on Windows/Linux; on macOS the folder that holds
    /// ShinyForge.app (the program itself sits in ShinyForge.app/Contents/MacOS). portable.flag is looked for here.
    /// </summary>
    public static string LaunchDir
    {
        get
        {
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var i = dir.IndexOf(".app" + Path.DirectorySeparatorChar + "Contents", StringComparison.Ordinal);
            return IsMac && i > 0 ? Path.GetDirectoryName(dir[..(i + 4)])! : dir;
        }
    }

    /// <summary>Finds a program on the PATH (and Homebrew's folders on macOS). Null if not found.</summary>
    public static string? Which(string command)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (IsMac) dirs.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin" });
        if (!IsWindows) dirs.AddRange(new[] { "/usr/bin", "/usr/local/bin", "/bin" });
        foreach (var d in dirs.Distinct())
        {
            try
            {
                var p = Path.Combine(d.Trim(), Exe(command));
                if (File.Exists(p) && !p.Contains("WindowsApps")) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Opens a file, folder or web address with the system's default program.</summary>
    public static void Open(string target)
    {
        if (IsWindows) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        else Process.Start(new ProcessStartInfo(IsMac ? "open" : "xdg-open") { ArgumentList = { target }, UseShellExecute = false });
    }

    /// <summary>Shows a folder in the file manager.</summary>
    public static void OpenFolder(string folder)
    {
        if (IsWindows) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        else Open(folder);
    }

    /// <summary>XDG user directory (~/.config/user-dirs.dirs), e.g. DOCUMENTS.</summary>
    private static string? XdgUserDir(string key)
    {
        try
        {
            var file = Path.Combine(Home, ".config", "user-dirs.dirs");
            if (!File.Exists(file)) return null;
            foreach (var line in File.ReadAllLines(file))
                if (line.StartsWith($"XDG_{key}_DIR=", StringComparison.Ordinal))
                {
                    var v = line[(line.IndexOf('=') + 1)..].Trim('"').Replace("$HOME", Home);
                    return v.Length > 0 && v != Home ? v : null;
                }
        }
        catch { }
        return null;
    }
}
