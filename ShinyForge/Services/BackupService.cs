using ShinyForge.Core;

namespace ShinyForge.Services;

public sealed record BackupEntry(string Kind, string Original, string Copy, DateTime When, long Size);

/// <summary>
/// Automatic, timestamped copies of save files and tracker spreadsheets, made BEFORE ShinyForge reads/changes them
/// (tracker sync, Pokémon HOME export). Keeps the newest N per file and can restore any of them.
/// Layout: Data\Backups\{Saves|Spreadsheets}\{name}__{hash}\{yyyyMMdd-HHmmss}{ext}  + original.txt (where it came from).
/// </summary>
public static class BackupService
{
    public static string Root => Path.Combine(AppPaths.DataDir, "Backups");

    /// <summary>Backs up <paramref name="path"/> unless an identical copy (same size + time) already exists. Returns the copy, or null.</summary>
    public static string? Backup(string path, bool spreadsheet)
    {
        if (!AppSettings.Current.AutoBackups || !File.Exists(path)) return null;
        try
        {
            var kind = spreadsheet ? "Spreadsheets" : "Saves";
            var full = Path.GetFullPath(path);
            var name = Path.GetFileName(full);
            var hash = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(full);
            var dir = Path.Combine(Root, kind, $"{Sanitize(Path.GetFileName(Path.GetDirectoryName(full)) ?? "")}_{Sanitize(name)}__{hash:X8}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "original.txt"), full);
            var src = new FileInfo(full);
            var newest = Directory.EnumerateFiles(dir).Where(f => !f.EndsWith("original.txt")).Select(f => new FileInfo(f))
                                  .OrderByDescending(f => f.CreationTime).FirstOrDefault();
            if (newest != null && newest.Length == src.Length && newest.LastWriteTimeUtc == src.LastWriteTimeUtc)
                return null; // unchanged since the last backup
            var copy = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + src.Extension);
            File.Copy(full, copy, overwrite: true);
            File.SetLastWriteTimeUtc(copy, src.LastWriteTimeUtc);
            Prune(dir, spreadsheet ? AppSettings.Current.BackupsToKeepSheets : AppSettings.Current.BackupsToKeepSaves);
            AppPaths.Log($"Backup: {full} -> {copy}");
            return copy;
        }
        catch (Exception e)
        {
            HealthMonitor.Report("Backups", HealthState.Warning, $"Couldn't back up {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    private static void Prune(string dir, int keep)
    {
        foreach (var old in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith("original.txt"))
                     .OrderByDescending(File.GetCreationTime).Skip(Math.Max(1, keep)))
            try { File.Delete(old); } catch { }
    }

    public static List<BackupEntry> List()
    {
        var list = new List<BackupEntry>();
        if (!Directory.Exists(Root)) return list;
        foreach (var kind in new[] { "Saves", "Spreadsheets" })
        {
            var kroot = Path.Combine(Root, kind);
            if (!Directory.Exists(kroot)) continue;
            foreach (var dir in Directory.EnumerateDirectories(kroot))
            {
                var orig = Path.Combine(dir, "original.txt");
                var original = File.Exists(orig) ? File.ReadAllText(orig).Trim() : "?";
                foreach (var f in Directory.EnumerateFiles(dir).Where(f => !f.EndsWith("original.txt")))
                {
                    var fi = new FileInfo(f);
                    list.Add(new BackupEntry(kind == "Saves" ? "Save" : "Spreadsheet", original, f, fi.CreationTime, fi.Length));
                }
            }
        }
        return list.OrderByDescending(e => e.When).ToList();
    }

    /// <summary>Puts a backup back in place. The current file is backed up first, so a restore can be undone.</summary>
    public static void Restore(BackupEntry e)
    {
        if (File.Exists(e.Original))
        {
            var keep = AppSettings.Current.AutoBackups;
            AppSettings.Current.AutoBackups = true;
            try { Backup(e.Original, e.Kind == "Spreadsheet"); } finally { AppSettings.Current.AutoBackups = keep; }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(e.Original)!);
        File.Copy(e.Copy, e.Original, overwrite: true);
        AppPaths.Log($"Restored {e.Copy} -> {e.Original}");
    }

    public static long TotalSize() => Directory.Exists(Root)
        ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 40 ? s[..40] : s;
    }
}
