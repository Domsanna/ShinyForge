using System.Globalization;
using ShinyForge.Core;

namespace ShinyForge.Services;

public sealed record EncounterRow(DateTime Time, string Species, bool Shiny, int IvTotal, int[] Ivs, string Nature, string Pid);

/// <summary>Statistics for a hunt, computed from the bots' encounter logs.</summary>
public sealed class HuntStatsResult
{
    public string Source = "";
    public int Gen = 3;
    public bool ShinyCharm;
    public int Total;                 // every logged encounter (repeats of the same RNG frame not counted)
    public int Repeats;               // rows skipped: the same Pokémon (PID) right after itself = the reset hit the same RNG frame
    public int Phase;                 // encounters since the last shiny (the current "phase")
    public int ShiniesFound;
    public double Odds;               // per-encounter shiny chance
    public double ChanceByNow;        // chance to have found one in the current phase
    public double PerHour;            // last hour (or the session)
    public int To50, To90;            // encounters (from 0) needed for a 50 / 90 % chance
    public TimeSpan? Eta90;           // at the current rate, until 90 % (from now)
    public EncounterRow? Last;
    public EncounterRow? BestIv;
    public DateTime? PhaseStart, LastTime;
    public List<(string Name, int Count)> Species = new();
    public List<(string Name, int Count)> Natures = new();
    public int[] PerHourLast24 = new int[24];
    public List<EncounterRow> Shinies = new();
    public string Milestone = "";
}

public static class HuntStats
{
    /// <summary>
    /// The "all time" line of the statistics window. Repeats are explained: the hunter's own counter includes them, so without
    /// this the two numbers didn't match (6,140 logged vs 5,928 shown) and it looked like encounters were lost.
    /// </summary>
    public static string AllTimeText(HuntStatsResult r) =>
        $"All time in this log: {r.Total:N0} encounters, {r.ShiniesFound} shin{(r.ShiniesFound == 1 ? "y" : "ies")}." +
        (r.Repeats > 0 ? $"\n{r.Total + r.Repeats:N0} rows logged - {r.Repeats:N0} were the same Pokémon again right after itself " +
                         "(the reset hit the same RNG frame), so they can't be a new shiny and aren't counted." : "");

    public sealed record Source(string Name, string CsvPath, int Gen, string? GameFilter);

    /// <summary>All encounter logs that exist: the Python hunter (FRLG on Switch) and the 3DS bot (per game).</summary>
    public static List<Source> Sources()
    {
        var list = new List<Source>();
        var bot = AppPaths.FindPythonBotFolder(AppSettings.Current.PythonBotFolder);
        if (bot != null && File.Exists(Path.Combine(bot, "encounters.csv")))
            list.Add(new Source("FireRed/LeafGreen (Switch) - Python hunter", Path.Combine(bot, "encounters.csv"), 3, null));
        var ds = Path.Combine(AppPaths.DataDir, "3ds_encounters.csv");
        if (File.Exists(ds))
        {
            var games = ReadRows(ds, null, out _).Count == 0 ? new List<string>() : ReadGames(ds);
            foreach (var g in games)
                list.Add(new Source($"{g} (3DS bot)", ds, g.Contains("Sun") || g.Contains("Moon") ? 7 : 6, g));
        }
        return list;
    }

    /// <summary>The log that was written most recently (= the hunt that's running now).</summary>
    public static Source? Active() => Sources().OrderByDescending(s => File.GetLastWriteTime(s.CsvPath)).FirstOrDefault();

    private static List<string> ReadGames(string path)
    {
        var lines = ReadAllLinesShared(path);
        if (lines.Length == 0) return new();
        var head = lines[0].Split(',');
        var gi = Array.IndexOf(head, "game");
        return gi < 0 ? new() : lines.Skip(1).Select(l => l.Split(',')).Where(c => c.Length > gi).Select(c => c[gi].Trim('"')).Distinct().ToList();
    }

    private static string[] ReadAllLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    /// <summary>Reads a log (either bot's format - columns are found by name). Consecutive duplicates (stuck resets) are skipped.</summary>
    public static List<EncounterRow> ReadRows(string path, string? gameFilter, out int duplicatesSkipped)
    {
        duplicatesSkipped = 0;
        var rows = new List<EncounterRow>();
        if (!File.Exists(path)) return rows;
        var lines = ReadAllLinesShared(path);
        if (lines.Length < 2) return rows;
        var h = lines[0].Split(',').Select(x => x.Trim()).ToList();
        int I(string n) => h.IndexOf(n);
        int ti = I("time"), si = I("species"), shi = I("shiny"), pidi = I("pid"), nati = I("nature"), gi = I("game");
        var ivi = new[] { "iv_hp", "iv_atk", "iv_def", "iv_spa", "iv_spd", "iv_spe" }.Select(I).ToArray();
        string? lastPid = null;
        foreach (var line in lines.Skip(1))
        {
            var c = SplitCsv(line);
            if (c.Count < h.Count - 1 || ti < 0 || si < 0) continue;
            if (gameFilter != null && gi >= 0 && c[gi] != gameFilter) continue;
            if (!DateTime.TryParse(c[ti], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) &&
                !DateTime.TryParse(c[ti], out t)) continue;
            var pid = pidi >= 0 && pidi < c.Count ? c[pidi] : "";
            if (pid.Length > 0 && pid == lastPid) { duplicatesSkipped++; continue; }
            lastPid = pid;
            var ivs = ivi.Select(i => i >= 0 && i < c.Count && int.TryParse(c[i], out var v) ? v : 0).ToArray();
            var shiny = shi >= 0 && shi < c.Count && (c[shi].Equals("True", StringComparison.OrdinalIgnoreCase) || c[shi] == "1" || c[shi].Equals("yes", StringComparison.OrdinalIgnoreCase));
            rows.Add(new EncounterRow(t, c[si], shiny, ivs.Sum(), ivs, nati >= 0 && nati < c.Count ? c[nati] : "", pid));
        }
        return rows;
    }

    private static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool q = false;
        foreach (var ch in line)
        {
            if (ch == '"') { q = !q; continue; }
            if (ch == ',' && !q) { res.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        res.Add(cur.ToString());
        return res;
    }

    /// <summary>Base shiny odds: 1/8192 up to Gen 5, 1/4096 from Gen 6; the Shiny Charm (Gen 5+) adds 2 extra rolls.</summary>
    public static double BaseOdds(int gen, bool charm)
    {
        var p = gen <= 5 ? 1 / 8192.0 : 1 / 4096.0;
        var rolls = charm && gen >= 5 ? 3 : 1;
        return 1 - Math.Pow(1 - p, rolls);
    }

    public static HuntStatsResult Compute(Source src, bool charm, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        var rows = ReadRows(src.CsvPath, src.GameFilter, out var repeats);
        var r = new HuntStatsResult { Source = src.Name, Gen = src.Gen, ShinyCharm = charm, Total = rows.Count, Repeats = repeats };
        r.Odds = BaseOdds(src.Gen, charm);
        r.Shinies = rows.Where(x => x.Shiny).ToList();
        r.ShiniesFound = r.Shinies.Count;
        var lastShinyIdx = rows.FindLastIndex(x => x.Shiny);
        var phase = rows.Skip(lastShinyIdx + 1).ToList();
        r.Phase = phase.Count;
        r.PhaseStart = phase.FirstOrDefault()?.Time;
        r.Last = rows.LastOrDefault();
        r.LastTime = r.Last?.Time;
        r.BestIv = phase.OrderByDescending(x => x.IvTotal).FirstOrDefault();
        r.ChanceByNow = 1 - Math.Pow(1 - r.Odds, r.Phase);
        r.To50 = (int)Math.Ceiling(Math.Log(0.5) / Math.Log(1 - r.Odds));
        r.To90 = (int)Math.Ceiling(Math.Log(0.1) / Math.Log(1 - r.Odds));
        var lastHour = rows.Count(x => x.Time > t.AddHours(-1));
        if (lastHour >= 3) r.PerHour = lastHour;
        else
        {
            // no recent activity: the rate of the last session (encounters less than 10 minutes apart)
            var sess = new List<EncounterRow>();
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                if (sess.Count > 0 && (sess[^1].Time - rows[i].Time).TotalMinutes > 10) break;
                sess.Add(rows[i]);
            }
            var span = sess.Count > 1 ? (sess[0].Time - sess[^1].Time).TotalHours : 0;
            r.PerHour = span > 0.02 ? sess.Count / span : 0;
        }
        if (r.PerHour > 0 && r.Phase < r.To90)
            r.Eta90 = TimeSpan.FromHours((r.To90 - r.Phase) / r.PerHour);
        r.Species = phase.GroupBy(x => x.Species).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).Take(8).ToList();
        r.Natures = phase.GroupBy(x => x.Nature).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).Take(5).ToList();
        for (int h = 0; h < 24; h++)
        {
            var from = t.AddHours(-(24 - h));
            r.PerHourLast24[h] = rows.Count(x => x.Time > from && x.Time <= from.AddHours(1));
        }
        var multiple = r.Phase * r.Odds; // how many "odds" deep
        r.Milestone = multiple switch
        {
            < 0.5 => $"{r.ChanceByNow:P1} chance so far",
            < 1 => "Past half odds",
            < 2 => $"Past odds ({multiple:0.0}x) - {r.ChanceByNow:P0} of hunters would have it by now",
            < 3 => $"Double odds ({multiple:0.0}x) - unlucky, keep going!",
            _ => $"{multiple:0.0}x odds - very unlucky ({1 - r.ChanceByNow:P1} of hunters are still hunting)",
        };
        return r;
    }

    /// <summary>Plain-text summary (also written to the overlay text files).</summary>
    public static Dictionary<string, string> OverlayTexts(HuntStatsResult r) => new()
    {
        ["counter"] = r.Phase.ToString("N0"),
        ["total"] = r.Total.ToString("N0"),
        ["odds"] = $"{r.ChanceByNow:P1}",
        ["rate"] = $"{r.PerHour:0}/h",
        ["shinies"] = r.ShiniesFound.ToString(),
        ["last"] = r.Last == null ? "" : $"{r.Last.Species} - {r.Last.Nature} - IVs {r.Last.IvTotal}/186",
        ["phase_time"] = r.PhaseStart is { } ps ? $"{(int)(DateTime.Now - ps).TotalHours}h {(DateTime.Now - ps).Minutes:00}m" : "",
        ["milestone"] = r.Milestone,
    };
}
