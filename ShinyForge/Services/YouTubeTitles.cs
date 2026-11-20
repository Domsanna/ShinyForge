using System.Text;
using PKHeX.Core;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// When a recording stops, a text file next to its videos ("Recording_0012-0015 - YouTube title.txt") suggests catchy
/// YouTube titles from what happened while it ran (encounters logged by the hunter, shinies / stop targets found, how
/// long it was). The titles tease without spoiling: they never name the Pokémon - the file lists the facts separately.
/// </summary>
public static class YouTubeTitles
{
    public const int MaxLength = 100; // YouTube's limit

    public sealed record Facts(string Game, TimeSpan Length, int Encounters, List<(DateTime Time, string Species, bool Shiny)> Finds, bool Hunting);

    /// <summary>Writes the title file for a finished recording. Returns its path, or null when no video was written.</summary>
    public static string? Write(Game game, RecordingService.Session session, DateTime ended)
    {
        try
        {
            var videos = SessionVideos(session);
            if (videos.Count == 0) return null; // nothing was recorded (the capture failed)
            var facts = Collect(game, session, ended);
            var titles = Suggest(facts, new Random());
            var first = Path.GetFileNameWithoutExtension(videos[0]);
            var last = Path.GetFileNameWithoutExtension(videos[^1]);
            var name = videos.Count == 1 ? first : $"{first}-{last.Replace("Recording_", "")}";
            var path = Path.Combine(session.Folder, $"{name} - YouTube title.txt");
            File.WriteAllText(path, Render(facts, titles, videos, session), Encoding.UTF8);
            AppPaths.Log("YouTube title written: " + path);
            return path;
        }
        catch (Exception e)
        {
            AppPaths.Log("Writing the YouTube title: " + e.Message);
            return null;
        }
    }

    /// <summary>The videos of this recording: Recording_NNNN.mp4 from its first number on, with something in them.</summary>
    private static List<string> SessionVideos(RecordingService.Session s)
    {
        if (!Directory.Exists(s.Folder)) return new();
        return Directory.EnumerateFiles(s.Folder, "Recording_*.mp4")
            .Select(f => (File: f, N: int.TryParse(Path.GetFileNameWithoutExtension(f)["Recording_".Length..], out var n) ? n : -1))
            .Where(x => x.N >= s.FirstNumber && new FileInfo(x.File).Length > 0)
            .OrderBy(x => x.N).Select(x => x.File).ToList();
    }

    private static Facts Collect(Game game, RecordingService.Session s, DateTime ended)
    {
        var finds = new List<(DateTime Time, string Species, bool Shiny)>();
        lock (s.Moments) finds.AddRange(s.Moments);
        var encounters = 0;
        var log = game.Bot == BotKind.FrlgSwitch
            ? AppPaths.FindPythonBotFolder(AppSettings.Current.PythonBotFolder) is { } bot ? Path.Combine(bot, "encounters.csv") : null
            : game.Platform == Platform.N3DS ? Path.Combine(AppPaths.DataDir, "3ds_encounters.csv") : null;
        if (log != null && File.Exists(log))
        {
            var rows = HuntStats.ReadRows(log, null, out _).Where(r => r.Time >= s.Started && r.Time <= ended).ToList();
            encounters = rows.Count;
            // shinies the log has that weren't noted live (e.g. found while the page wasn't listening)
            foreach (var r in rows.Where(r => r.Shiny))
                if (!finds.Any(f => f.Shiny && f.Species.Equals(r.Species, StringComparison.OrdinalIgnoreCase) && Math.Abs((f.Time - r.Time).TotalMinutes) < 3))
                    finds.Add((r.Time, r.Species, true));
        }
        return new Facts(GameName(game), ended - s.Started, encounters, finds.OrderBy(f => f.Time).ToList(), encounters > 0 || finds.Count > 0);
    }

    /// <summary>"FireRed (Switch)" → "Pokémon FireRed".</summary>
    public static string GameName(Game game)
    {
        var t = game.Title;
        var paren = t.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) t = t[..paren];
        return t.StartsWith("Pokémon", StringComparison.OrdinalIgnoreCase) ? t : "Pokémon " + t;
    }

    private static string LengthText(TimeSpan t)
    {
        if (t.TotalMinutes < 60) return Math.Max(1, (int)Math.Round(t.TotalMinutes)) is var m && m == 1 ? "1 Minute" : $"{m} Minutes";
        var h = (int)Math.Round(t.TotalHours);
        return h == 1 ? "1 Hour" : $"{h} Hours";
    }

    private static bool IsLegendary(string species)
    {
        try
        {
            var list = GameInfo.GetStrings("en").specieslist;
            var i = Array.FindIndex(list, n => n.Equals(species.Trim(), StringComparison.OrdinalIgnoreCase));
            return i > 0 && (SpeciesCategory.IsLegendary((ushort)i) || SpeciesCategory.IsSubLegendary((ushort)i) || SpeciesCategory.IsMythical((ushort)i));
        }
        catch { return false; }
    }

    /// <summary>A few titles for what happened, the best fit first. None names the Pokémon that was found.</summary>
    public static List<string> Suggest(Facts f, Random rng)
    {
        var g = f.Game;
        var len = LengthText(f.Length);
        var n = f.Encounters > 0 ? $"{f.Encounters:N0} Encounters" : null;
        var shinies = f.Finds.Count(x => x.Shiny);
        var legendary = f.Finds.Any(x => x.Shiny && IsLegendary(x.Species));
        var list = new List<string>();

        if (shinies >= 2)
        {
            list.Add($"Not One... But {(shinies == 2 ? "TWO" : shinies.ToString())} Shinies?! | {g}");
            list.Add($"{g} Was Feeling Generous Today ✨✨");
            if (n != null) list.Add($"{n}, {shinies} Surprises - {g} Shiny Hunting");
            list.Add($"I Did NOT Expect This Many Shinies in {g}");
        }
        else if (shinies == 1)
        {
            if (n != null) list.Add($"{n} Later... {g} Finally Delivered ✨");
            if (legendary) list.Add($"The Legendary Hunt in {g} Took a Turn I Didn't Expect ✨");
            list.Add($"After {len} of Shiny Hunting in {g}, THIS Happened");
            list.Add($"I Wasn't Ready for What Showed Up in {g} ✨");
            list.Add($"The Moment Everything Changed in {g} (Shiny Hunt)");
        }
        else if (f.Finds.Count > 0)
        {
            list.Add($"It Finally Showed Up... {g} Hunt Update");
            list.Add($"The Moment I've Been Waiting For in {g}");
            if (n != null) list.Add($"{n} Later, Something Finally Stopped the Hunt | {g}");
        }
        else if (f.Hunting)
        {
            if (n != null) list.Add($"{n} and Still No Shiny?! | {g} Shiny Hunt");
            list.Add($"How Long Can One Shiny Hide? ({g}, {len} of Hunting)");
            list.Add($"The {g} Shiny Hunt Continues... Will It Ever Show Up?");
            if (n != null) list.Add($"I Reset {g} {f.Encounters:N0} Times... Here's Why");
        }
        else
        {
            list.Add($"{len} of {g}... and It Got Interesting");
            list.Add($"Back in {g} - You'll Want to See How This Ends");
            list.Add($"Playing {g} Until Something Happened");
            list.Add($"{g}, But I Wasn't Ready for This");
        }

        // the best fit stays first; the other ideas in a different order each time
        var rest = list.Skip(1).OrderBy(_ => rng.Next()).ToList();
        return list.Take(1).Concat(rest).Select(t => t.Length <= MaxLength ? t : t[..(MaxLength - 1)] + "…").Distinct().ToList();
    }

    private static string Render(Facts f, List<string> titles, List<string> videos, RecordingService.Session s)
    {
        var started = s.Started;
        var sb = new StringBuilder();
        var files = videos.Count == 1 ? Path.GetFileName(videos[0]) : $"{Path.GetFileName(videos[0])} to {Path.GetFileName(videos[^1])} ({videos.Count} files)";
        sb.AppendLine($"YouTube title for {files}");
        sb.AppendLine($"{f.Game} - recorded {started:yyyy-MM-dd HH:mm}, " +
                      (f.Length.TotalHours >= 1 ? $"{(int)f.Length.TotalHours} h {f.Length.Minutes} min" : f.Length.TotalMinutes >= 1 ? $"{(int)f.Length.TotalMinutes} min" : $"{(int)f.Length.TotalSeconds} s"));
        sb.AppendLine();
        sb.AppendLine("TITLE:");
        sb.AppendLine(titles[0]);
        if (titles.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine("Other ideas:");
            foreach (var t in titles.Skip(1)) sb.AppendLine("- " + t);
        }
        sb.AppendLine();
        sb.AppendLine("What happened (for you - kept out of the titles so the video isn't spoiled):");
        if (f.Encounters > 0) sb.AppendLine($"- {f.Encounters:N0} encounters logged by the hunter");
        foreach (var x in f.Finds)
        {
            sb.AppendLine($"- {(x.Shiny ? "Shiny " : "Found (stop target) ")}{x.Species} at {x.Time:HH:mm}{WhereInVideos(x.Time, s)}");
        }
        if (f.Encounters == 0 && f.Finds.Count == 0) sb.AppendLine("- No hunt was running - just gameplay.");
        return sb.ToString();
    }

    /// <summary>" (Recording_0013.mp4 at 4:12)": which video of the recording has the moment, and where in it.</summary>
    private static string WhereInVideos(DateTime time, RecordingService.Session s)
    {
        var at = time - s.Started;
        if (at < TimeSpan.Zero || s.SegmentSeconds <= 0) return "";
        var index = (int)(at.TotalSeconds / s.SegmentSeconds);
        var inFile = TimeSpan.FromSeconds(at.TotalSeconds % s.SegmentSeconds);
        return $" (Recording_{s.FirstNumber + index:0000}.mp4 at {(int)inFile.TotalMinutes}:{inFile.Seconds:00})";
    }
}
