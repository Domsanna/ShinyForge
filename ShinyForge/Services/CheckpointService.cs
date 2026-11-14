using System.Globalization;
using System.Text;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>One Checkpoint backup of a game: sdmc:/…/Checkpoint/saves/&lt;id&gt; &lt;title&gt;/&lt;backup&gt;/.</summary>
public sealed record CheckpointBackup(string Folder, string TitleFolder, string BackupName, DateTime Time, FoundSave Save)
{
    public string Drive => Path.GetPathRoot(Folder) ?? "";
}

/// <summary>
/// Finds a game's saves in Checkpoint's backup folders (Checkpoint by Bernardo Giordano, on the 3DS and the Switch):
///   3DS:    sdmc:/3ds/Checkpoint/saves/0x&lt;unique id&gt; &lt;title&gt;/&lt;backup&gt;/
///   Switch: sdmc:/switch/Checkpoint/saves/&lt;title id&gt; &lt;title&gt;/&lt;backup&gt;/
/// The SD card is read on any drive of this PC (or a folder chosen in Settings). Matching is by title ID, then by name.
/// ShinyForge only reads there; the chosen backup is copied to <see cref="AppPaths.SavesDir"/> before it is used.
/// </summary>
public static class CheckpointService
{
    /// <summary>Game id → title IDs (Switch: 16 hex digits; 3DS: Checkpoint's 5-digit unique ID).</summary>
    public static readonly Dictionary<string, string[]> TitleIds = new()
    {
        ["3ds_x"] = new[] { "0055D" }, ["3ds_y"] = new[] { "0055E" },
        ["3ds_or"] = new[] { "011C4" }, ["3ds_as"] = new[] { "011C5" },
        ["3ds_sun"] = new[] { "01648" }, ["3ds_moon"] = new[] { "0175E" },
        ["3ds_usun"] = new[] { "01B50" }, ["3ds_umoon"] = new[] { "01B51" },
        ["sw_lgp"] = new[] { "010003F003A34000" }, ["sw_lge"] = new[] { "0100187003A36000" },
        ["sw_sword"] = new[] { "0100ABF008968000" }, ["sw_shield"] = new[] { "01008DB008C2C000" },
        ["sw_bd"] = new[] { "0100000011D90000" }, ["sw_sp"] = new[] { "010018E011D92000" },
        ["sw_pla"] = new[] { "01001F5010DFA000" },
        ["sw_scarlet"] = new[] { "0100A3D008C5C000" }, ["sw_violet"] = new[] { "01008F6008C5E000" },
        // FireRed / LeafGreen on Switch: one title per language (JPN, ENG, FRA, ITA, GER, SPA)
        ["sw_frlg_fr"] = new[] { "01006FA0233F8000", "0100554023408000", "01004B3023412000", "010092302342A000", "01007F8023416000", "0100EB702342C000" },
        ["sw_frlg_lg"] = new[] { "0100F1E0233FA000", "010034D02340E000", "010087C02342E000", "01005C7023432000", "0100FD6023430000", "01002B5023434000" },
    };

    /// <summary>The "saves" folders on this PC: every ready drive's 3ds/ and switch/ Checkpoint folders, plus the one chosen in Settings.</summary>
    public static List<string> FindRoots(bool switchGame)
    {
        var sub = switchGame ? @"switch\Checkpoint\saves" : @"3ds\Checkpoint\saves";
        var roots = new List<string>();
        var chosen = AppSettings.Current.CheckpointFolder;
        if (!string.IsNullOrWhiteSpace(chosen) && Directory.Exists(chosen))
        {
            // Accept the SD card root, the Checkpoint folder or the saves folder itself.
            var hit = new[] { Path.Combine(chosen, sub), Path.Combine(chosen, "saves"), chosen }.FirstOrDefault(c => Directory.Exists(c) && LooksLikeSaves(c));
            if (hit != null) roots.Add(hit);
        }
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.Network) continue;
                var p = Path.Combine(d.RootDirectory.FullName, sub);
                if (Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase)) roots.Add(p);
            }
            catch { /* drive not ready */ }
        }
        return roots;
    }

    private static bool LooksLikeSaves(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).Any(d => SplitFolder(Path.GetFileName(d)).Id != null); }
        catch { return false; }
    }

    /// <summary>"0x01B51 Pokémon Ultra Moon" → ("01B51", "Pokémon Ultra Moon"); "0100ABF008968000 Pokémon Sword" → (id, name).</summary>
    public static (string? Id, string Name) SplitFolder(string folderName)
    {
        var sp = folderName.IndexOf(' ');
        var first = sp > 0 ? folderName[..sp] : folderName;
        var rest = sp > 0 ? folderName[(sp + 1)..] : "";
        var hex = first.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? first[2..] : first;
        var isId = hex.Length is >= 4 and <= 16 && hex.All(Uri.IsHexDigit) && (first.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || hex.Length == 16);
        if (isId) return (hex.ToUpperInvariant(), rest);
        // DS cartridges on a 3DS: a 4-character game code ("IRBO Pokémon Black")
        if (first.Length == 4 && first.All(c => char.IsUpper(c) || char.IsDigit(c)) && rest.Length > 0) return (first, rest);
        return (null, folderName);
    }

    /// <summary>Lower-case letters/digits only, accents removed, without "Pokémon" and "Version".</summary>
    public static string NormalizeName(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Replace("pokemon", "").Replace("version", "");
    }

    private static IEnumerable<string> NamesFor(Game g)
    {
        var title = System.Text.RegularExpressions.Regex.Replace(g.Title, @"\s*\(.*?\)", "");
        yield return NormalizeName(title);
        if (!string.IsNullOrWhiteSpace(g.Spreadsheet)) yield return NormalizeName(g.Spreadsheet);
    }

    /// <summary>Does a Checkpoint title folder belong to this game? By title ID first, else by name.</summary>
    public static bool Matches(Game g, string folderName)
    {
        var (id, name) = SplitFolder(folderName);
        if (id != null && TitleIds.TryGetValue(g.Id, out var ids))
            return ids.Any(x => x.TrimStart('0').Equals(id.TrimStart('0'), StringComparison.OrdinalIgnoreCase));
        var n = NormalizeName(name);
        return n.Length > 0 && NamesFor(g).Contains(n);
    }

    /// <summary>Every backup of the game in the given roots that holds a readable save, newest first.</summary>
    public static List<CheckpointBackup> FindBackups(Game game, IEnumerable<string> roots)
    {
        var list = new List<CheckpointBackup>();
        foreach (var root in roots)
        {
            IEnumerable<string> titles;
            try { titles = Directory.EnumerateDirectories(root).Where(t => Matches(game, Path.GetFileName(t))).ToList(); }
            catch { continue; }
            foreach (var title in titles)
            {
                IEnumerable<string> backups;
                try { backups = Directory.EnumerateDirectories(title).Prepend(title).ToList(); }
                catch { continue; }
                foreach (var b in backups)
                {
                    // the title folder itself only counts if files were put straight into it
                    if (b == title && !Directory.EnumerateFiles(b).Any()) continue;
                    var save = SaveService.FindSaves(b, game).FirstOrDefault();
                    if (save == null) continue;
                    var name = b == title ? "(no sub-folder)" : Path.GetFileName(b);
                    list.Add(new CheckpointBackup(b, title, name, LatestWrite(b), save));
                }
            }
        }
        return list.OrderByDescending(x => x.Time).ToList();
    }

    private static DateTime LatestWrite(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTime).DefaultIfEmpty(Directory.GetLastWriteTime(dir)).Max(); }
        catch { return Directory.GetLastWriteTime(dir); }
    }

    /// <summary>The folder the game's Checkpoint copies and exports go to: SavesDir\&lt;game title&gt;.</summary>
    public static string GameFolder(Game g, string? savesRoot = null) => Path.Combine(savesRoot ?? AppPaths.SavesDir, SaveService.Sanitize(g.Title));

    /// <summary>Copies a backup to SavesDir\&lt;game&gt;\Checkpoint\&lt;backup&gt; (file times kept) and returns the copied save.</summary>
    public static FoundSave CopyToPc(CheckpointBackup b, Game game, string? savesRoot = null)
    {
        var dest = Path.Combine(GameFolder(game, savesRoot), "Checkpoint", SaveService.Sanitize(b.BackupName == "(no sub-folder)" ? b.Time.ToString("yyyyMMdd-HHmmss") : b.BackupName));
        foreach (var file in Directory.EnumerateFiles(b.Folder, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(b.Folder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            File.SetLastWriteTime(target, File.GetLastWriteTime(file));
        }
        var copied = Path.Combine(dest, Path.GetRelativePath(b.Folder, b.Save.Path));
        AppPaths.Log($"Checkpoint backup copied: {b.Folder} -> {dest}");
        return new FoundSave(copied, b.Save.Save);
    }
}
