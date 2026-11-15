using System.Text;
using PKHeX.Core;
using ShinyForge.Core;

namespace ShinyForge.Services;

public sealed record FoundSave(string Path, SaveFile Save);

/// <summary>Finds exported save files, extracts Pokémon with PKHeX.Core and writes the "game info" document.</summary>
public static class SaveService
{
    private static readonly string[] SkipExt = { ".exe", ".dll", ".png", ".jpg", ".mp4", ".txt", ".xlsx", ".xlsm", ".pk1", ".pk2",
        ".pk3", ".pk4", ".pk5", ".pk6", ".pk7", ".pk8", ".pk9", ".pa8", ".pa9", ".pb7", ".pb8", ".json", ".zip", ".nds", ".gba", ".gb", ".gbc", ".3ds", ".cia" };

    /// <summary>Searches <paramref name="folder"/> (and up to 3 sub-folder levels) for save files of <paramref name="game"/>.</summary>
    public static List<FoundSave> FindSaves(string folder, Game? game)
    {
        var result = new List<FoundSave>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return result;

        foreach (var file in EnumerateFiles(folder, 3))
        {
            try
            {
                var info = new FileInfo(file);
                if (SkipExt.Contains(info.Extension.ToLowerInvariant()) || info.Length > 16 * 1024 * 1024 || info.Length < 0x2000)
                    continue;
                if (info.FullName.Contains("Pokemon Home for", StringComparison.OrdinalIgnoreCase))
                    continue;
                // No size pre-filter: Switch saves (SwSh, SV, Z-A...) have variable sizes and are recognised by content.
                if (!SaveUtil.TryGetSaveFile(file, out var sav) || sav is null)
                    continue;
                if (game is null || game.Versions.Contains(sav.Version) || game.Versions.Any(v => v.Contains(sav.Version)) || IsSameFamily(game, sav))
                    result.Add(new FoundSave(file, sav));
            }
            catch (Exception e) { AppPaths.Log($"Save probe failed for {file}: {e.Message}"); }
        }
        return result.OrderByDescending(s => File.GetLastWriteTime(s.Path)).ToList();
    }

    // Some saves only know their pair: a Gen 1 save is "RB" (Red or Blue), a Gen 2 save "GS" (Gold or Silver) - the save
    // itself can't tell which cartridge it came from - and a Gen 3 FR/LG save before the version is set.
    private static bool IsSameFamily(Game game, SaveFile sav) => sav.Generation == game.Gen && game.Gen <= 3 &&
        (sav.Version is GameVersion.Any or GameVersion.Invalid || game.Versions.Any(v => sav.Version.Contains(v)));

    private static IEnumerable<string> EnumerateFiles(string folder, int depth)
    {
        IEnumerable<string> files = Array.Empty<string>();
        try { files = Directory.EnumerateFiles(folder); } catch { }
        foreach (var f in files) yield return f;
        if (depth <= 0) yield break;
        IEnumerable<string> dirs = Array.Empty<string>();
        try { dirs = Directory.EnumerateDirectories(folder); } catch { }
        foreach (var d in dirs)
            foreach (var f in EnumerateFiles(d, depth - 1))
                yield return f;
    }

    public static IEnumerable<PKM> Party(SaveFile sav) => sav.HasParty ? sav.PartyData.Where(p => p.Species != 0) : Enumerable.Empty<PKM>();

    public static IEnumerable<(int Box, int Slot, PKM Pk)> Boxes(SaveFile sav)
    {
        if (!sav.HasBox)
            yield break;
        for (int b = 0; b < sav.BoxCount; b++)
        {
            var data = sav.GetBoxData(b);
            for (int s = 0; s < data.Length; s++)
                if (data[s].Species != 0)
                    yield return (b, s, data[s]);
        }
    }

    public static string HomeFolderName(Game game) => $"Pokemon Home for {Sanitize(game.Title)}";

    /// <summary>Exports every party and box Pokémon as .pkX files to "&lt;baseFolder&gt;\Pokemon Home for &lt;Title&gt;".</summary>
    public static (string Folder, int Count) ExportPokemon(SaveFile sav, Game game, string baseFolder)
    {
        var root = Path.Combine(baseFolder, HomeFolderName(game));
        Directory.CreateDirectory(root);
        int count = 0;

        var partyDir = Path.Combine(root, "Party");
        int i = 1;
        foreach (var pk in Party(sav))
        {
            Directory.CreateDirectory(partyDir);
            Write(pk, Path.Combine(partyDir, $"{i++:00} - {Sanitize(pk.FileName)}"), party: true);
            count++;
        }
        foreach (var (box, slot, pk) in Boxes(sav))
        {
            var dir = Path.Combine(root, $"Box {box + 1:00}");
            Directory.CreateDirectory(dir);
            Write(pk, Path.Combine(dir, $"{slot + 1:00} - {Sanitize(pk.FileName)}"), party: false);
            count++;
        }
        return (root, count);
    }

    private static void Write(PKM pk, string path, bool party)
    {
        var buffer = new byte[party ? pk.SIZE_PARTY : pk.SIZE_STORED];
        if (party)
            pk.WriteDecryptedDataParty(buffer);
        else
            pk.WriteDecryptedDataStored(buffer);
        File.WriteAllBytes(path, buffer);
    }

    public static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    // ------------------------------------------------------------------ game info document
    public static string BuildGameInfo(SaveFile sav, Game game, string sourcePath)
    {
        var party = Party(sav).ToList();
        var boxed = Boxes(sav).Select(b => b.Pk).ToList();
        var all = party.Concat(boxed).ToList();
        string Name(ushort sp) => SpeciesName.GetSpeciesName(sp, (int)LanguageID.English);

        var sb = new StringBuilder();
        sb.AppendLine("=====================================================");
        sb.AppendLine($"  GAME INFO - Pokémon {game.Title}");
        sb.AppendLine($"  Generated by ShinyForge on {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("=====================================================");
        sb.AppendLine();
        sb.AppendLine("TRAINER");
        sb.AppendLine($"  Name ............ {sav.OT}");
        sb.AppendLine($"  Gender .......... {(sav.Gender == 0 ? "Male" : "Female")}");
        sb.AppendLine($"  Trainer ID (TID)  {sav.DisplayTID}");
        sb.AppendLine($"  Secret ID (SID) . {(sav.Generation <= 2 ? "n/a (Gen 1-2)" : sav.DisplaySID.ToString())}");
        if (sav.Generation >= 7)
            sb.AppendLine($"  ID format ....... 6-digit (TID {sav.TrainerTID7:000000}, SID {sav.TrainerSID7:0000})");
        else if (sav.Generation >= 3)
            sb.AppendLine($"  16-bit IDs ...... TID {sav.TID16:00000}  SID {sav.SID16:00000}");
        sb.AppendLine($"  Language ........ {(LanguageID)sav.Language}");
        sb.AppendLine();
        sb.AppendLine("GAME");
        sb.AppendLine($"  Version ......... {sav.Version} (Generation {sav.Generation})");
        sb.AppendLine($"  Play time ....... {Safe(() => $"{sav.PlayedHours}h {sav.PlayedMinutes:00}m {sav.PlayedSeconds:00}s")}");
        var start = StartDate(sav);
        sb.AppendLine($"  Game started .... {(start is null ? "not recorded in this game's save" : start.Value.ToString("yyyy-MM-dd HH:mm"))}");
        sb.AppendLine($"  Money ........... {Safe(() => sav.Money.ToString("N0"))}");
        sb.AppendLine($"  Save file ....... {sourcePath}");
        sb.AppendLine($"  Last saved ...... {File.GetLastWriteTime(sourcePath):yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("POKéDEX");
        sb.AppendLine($"  Seen ............ {Safe(() => sav.SeenCount.ToString())}");
        sb.AppendLine($"  Caught .......... {Safe(() => sav.CaughtCount.ToString())}");
        sb.AppendLine();
        sb.AppendLine("COLLECTION");
        sb.AppendLine($"  Party ........... {party.Count}");
        sb.AppendLine($"  In boxes ........ {boxed.Count}  ({sav.BoxCount} boxes)");
        sb.AppendLine($"  Unique species .. {all.Select(p => p.Species).Distinct().Count()}");
        sb.AppendLine($"  Shiny Pokémon ... {all.Count(p => p.IsShiny)}");
        sb.AppendLine($"  Eggs ............ {all.Count(p => p.IsEgg)}");
        sb.AppendLine($"  Caught by you ... {all.Count(p => IsOwnOT(p, sav))}  (your OT)");
        sb.AppendLine();
        sb.AppendLine("PARTY");
        foreach (var p in party)
            sb.AppendLine($"  {Name(p.Species),-14} Lv.{p.CurrentLevel,-3} {(p.IsShiny ? "★ shiny " : "")}{(p.IsEgg ? "(egg)" : "")} OT {p.OriginalTrainerName}");
        var shinies = all.Where(p => p.IsShiny).ToList();
        if (shinies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("SHINY POKéMON");
            foreach (var p in shinies)
                sb.AppendLine($"  ★ {Name(p.Species),-14} Lv.{p.CurrentLevel,-3} OT {p.OriginalTrainerName}");
        }
        return sb.ToString();
    }

    /// <summary>A value from the save, or "not readable" when this save doesn't have it (a missing / damaged block must not
    /// stop the whole Game Info document).</summary>
    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch (Exception e) { AppPaths.Log("Game info: " + e.Message); return "not readable from this save"; }
    }

    public static bool IsOwnOT(PKM pk, SaveFile sav) =>
        pk.OriginalTrainerName == sav.OT && pk.TID16 == sav.TID16 && (sav.Generation <= 2 || pk.SID16 == sav.SID16);

    private static DateTime? StartDate(SaveFile sav)
    {
        try
        {
            if (sav.Generation >= 4 && sav.SecondsToStart > 0)
                return new DateTime(2000, 1, 1).AddSeconds(sav.SecondsToStart);
        }
        catch { /* not every save type supports this */ }
        return null;
    }

    public static string WriteGameInfo(SaveFile sav, Game game, string sourcePath, string folder)
    {
        var path = Path.Combine(folder, "game info.txt");
        File.WriteAllText(path, BuildGameInfo(sav, game, sourcePath), Encoding.UTF8);
        return path;
    }
}
