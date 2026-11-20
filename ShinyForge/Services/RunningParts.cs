using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// The parts of ShinyForge that keep working in the background: shiny hunts (Python hunter, SysBot.exe, 3DS bot) and
/// recordings. When the user closes ShinyForge they are listed and, if the user agrees, stopped together with it - the
/// hunter used to keep pressing buttons on the Switch after ShinyForge was gone.
/// </summary>
public static class RunningParts
{
    public sealed record Part(string Name, Action Stop);

    private static readonly List<(object Owner, Func<string?> Describe, Action Stop)> Others = new();

    /// <summary>A part that lives in a page (the 3DS bot): <paramref name="describe"/> returns its name while it runs, else null.</summary>
    public static void Register(object owner, Func<string?> describe, Action stop)
    {
        lock (Others) Others.Add((owner, describe, stop));
    }

    public static void Unregister(object owner)
    {
        lock (Others) Others.RemoveAll(o => ReferenceEquals(o.Owner, owner));
    }

    /// <summary>Everything that is running now (empty = ShinyForge can just close).</summary>
    public static List<Part> Find()
    {
        var parts = new List<Part>();
        try
        {
            if (HuntService.IsPythonHuntRunning())
            {
                var held = HuntService.HeldFind;
                parts.Add(new Part("The shiny hunter (Python bot)" + (held != null ? $" - {held} is waiting for your answer" : ""),
                    () => HuntService.StopPythonHunt(wait: true)));
            }
            foreach (var p in HuntService.SysBotProcesses())
                parts.Add(new Part("The SysBot.exe hunting bot", () => HuntService.StopSysBot(p, wait: true)));
        }
        catch (Exception e) { AppPaths.Log("Looking for running hunts: " + e.Message); }
        foreach (var r in RecordingService.Running())
            parts.Add(new Part("A gameplay recording" + (r.Name.Length > 0 ? $" ({r.Name})" : ""), r.Stop));
        lock (Others)
            foreach (var (_, describe, stop) in Others)
                if (describe() is { } name) parts.Add(new Part(name, stop));
        return parts;
    }

    /// <summary>The question asked before closing.</summary>
    public static string Question(IReadOnlyList<Part> parts) =>
        "Part of ShinyForge is still running:\n\n" + string.Join("\n", parts.Select(p => "  •  " + p.Name)) + "\n\n" +
        $"Closing ShinyForge will also stop {(parts.Count == 1 ? "it" : "them")}" +
        (parts.Any(p => p.Name.Contains("recording")) ? " (recordings are saved first)" : "") + ". Is that OK?";

    /// <summary>Stops the parts (waits for each, a few seconds at most) and every shiny-clip buffer.</summary>
    public static void StopAll(IEnumerable<Part> parts)
    {
        foreach (var p in parts)
        {
            try { p.Stop(); AppPaths.Log("Closing ShinyForge stopped: " + p.Name); }
            catch (Exception e) { AppPaths.Log($"Stopping {p.Name}: {e.Message}"); }
        }
        ShinyClipService.StopAll();
    }
}
