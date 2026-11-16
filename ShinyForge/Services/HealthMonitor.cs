using System.Net.Sockets;
using ShinyForge.Core;

namespace ShinyForge.Services;

public enum HealthState { Ok, Warning, Error }

/// <summary>One problem. <see cref="Details"/> holds extra text for error reports (e.g. the stack trace of a crash).</summary>
public sealed record HealthItem(string Feature, HealthState State, string Message, DateTime When, string? Details = null)
{
    /// <summary>Stops or endangers hunting / playing / saves (see <see cref="ErrorDoctor.AffectsGameplay"/>); other problems are only logged.</summary>
    public bool AffectsGameplay => ErrorDoctor.AffectsGameplay(this);
}

/// <summary>
/// Program-wide safety net. Every minute it runs a check for every feature that's in use (hunting bots, recording,
/// collection monitoring, connections to the Switch/3DS, disk space, program files) and collects problems that features
/// report themselves (bot warnings, unexpected errors). New problems that affect hunting/playing raise <see cref="Problem"/> so the
/// UI can warn the user; minor ones are only written to issues.log (<see cref="ErrorDoctor"/> diagnoses both and can report them);
/// checks that can fix things (restart a crashed recording, restart a stuck hunt) do so and report what they did.
/// </summary>
public static class HealthMonitor
{
    private static readonly Dictionary<string, Func<IEnumerable<HealthItem>>> Checks = new();
    private static readonly Dictionary<string, HealthItem> Current = new();
    private static readonly Dictionary<string, DateTime> LastAlert = new();
    private static System.Threading.Timer? _timer;
    private static int _running;

    /// <summary>A new (or returning after 10 minutes) warning/error. Raised on a background thread.</summary>
    public static event Action<HealthItem>? Problem;
    /// <summary>The overall state changed / a check round finished. Raised on a background thread.</summary>
    public static event Action? Updated;
    public static DateTime LastRun { get; private set; }
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Current problems that affect hunting / playing (the ones the user is told about).</summary>
    public static IReadOnlyList<HealthItem> Items => Select(true);
    /// <summary>Current minor problems (overlay, clips, recordings, tracker, backups...): logged to issues.log, not announced.</summary>
    public static IReadOnlyList<HealthItem> MinorItems => Select(false);
    public static HealthState Overall => Items.Select(v => v.State).DefaultIfEmpty(HealthState.Ok).Max();

    private static List<HealthItem> Select(bool gameplay)
    {
        lock (Current) return Current.Values.Where(i => i.AffectsGameplay == gameplay).OrderByDescending(i => i.State).ThenBy(i => i.Feature).ToList();
    }

    /// <summary>Adds (or replaces) a periodic check. Return no items when everything is fine.</summary>
    public static void Register(string key, Func<IEnumerable<HealthItem>> check) { lock (Checks) Checks[key] = check; }
    public static void Unregister(string key)
    {
        lock (Checks) Checks.Remove(key);
        lock (Current) foreach (var k in Current.Keys.Where(k => k.StartsWith(key + "|")).ToList()) Current.Remove(k);
    }

    public static void Start()
    {
        _timer ??= new System.Threading.Timer(_ => RunNow(), null, TimeSpan.FromSeconds(20), Interval);
    }

    public static void Stop() { _timer?.Dispose(); _timer = null; }

    /// <summary>Something a feature noticed itself (a bot warning, an unexpected error...).</summary>
    public static void Report(string feature, HealthState state, string message, string? details = null)
    {
        var item = new HealthItem(feature, state, message, DateTime.Now, details);
        AppPaths.Log($"[health] {state} {feature}: {message}");
        lock (Current) Current["report|" + feature + "|" + message] = item;
        Raise(item, force: true);
        Updated?.Invoke();
    }

    /// <summary>Removes the problems a feature reported itself (e.g. after the user acknowledged them).</summary>
    public static void Clear(string? feature = null)
    {
        lock (Current)
            foreach (var k in Current.Keys.Where(k => k.StartsWith("report|") && (feature == null || k.StartsWith("report|" + feature + "|"))).ToList())
                Current.Remove(k);
        Updated?.Invoke();
    }

    /// <summary>Runs every check now (also used by the "Check now" button).</summary>
    public static void RunNow()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            List<KeyValuePair<string, Func<IEnumerable<HealthItem>>>> checks;
            lock (Checks) checks = Checks.ToList();
            foreach (var (key, check) in checks)
            {
                List<HealthItem> found;
                try { found = check().ToList(); }
                catch (Exception e) { found = new() { new HealthItem(key, HealthState.Error, "The check itself failed: " + e.Message, DateTime.Now) }; }
                lock (Current)
                {
                    foreach (var k in Current.Keys.Where(k => k.StartsWith(key + "|")).ToList()) Current.Remove(k);
                    foreach (var f in found) Current[key + "|" + f.Feature + "|" + f.Message] = f;
                }
                foreach (var f in found.Where(f => f.State != HealthState.Ok)) Raise(f, force: false);
            }
            LastRun = DateTime.Now;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            Updated?.Invoke();
        }
    }

    private static void Raise(HealthItem item, bool force)
    {
        var key = item.Feature + "|" + item.Message;
        lock (LastAlert)
        {
            if (!force && LastAlert.TryGetValue(key, out var t) && (DateTime.Now - t).TotalMinutes < 10) return;
            LastAlert[key] = DateTime.Now;
        }
        if (item.State == HealthState.Ok) return;
        AppPaths.Log($"[health] {item.State} {item.Feature}: {item.Message}");
        if (item.AffectsGameplay) Problem?.Invoke(item);
        else ErrorDoctor.LogQuietly(item);   // minor: no pop-up, no sound - kept in issues.log so it can still be reported
    }

    // ------------------------------------------------------------------ helpers for checks
    public static HealthItem Warn(string feature, string msg) => new(feature, HealthState.Warning, msg, DateTime.Now);
    public static HealthItem Error(string feature, string msg) => new(feature, HealthState.Error, msg, DateTime.Now);

    /// <summary>True if a TCP port answers within <paramref name="timeoutMs"/>.</summary>
    public static bool CanReach(string host, int port, int timeoutMs = 2000)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        try
        {
            using var c = new TcpClient();
            var task = c.ConnectAsync(host, port);
            return task.Wait(timeoutMs) && c.Connected;
        }
        catch { return false; }
    }

    /// <summary>Does the Switch's sys-botbase answer - over the set connection (USB cable / WiFi) or the other one?</summary>
    public static bool SwitchReachable(int timeoutMs = 2500)
    {
        // either connection counts: sessions follow whichever one sys-botbase really listens on
        var s = AppSettings.Current;
        bool Usb() => WinUsbDevice.IsConnected(0x057E, 0x3000);
        return s.SwitchUsb ? Usb() || CanReach(s.SwitchIP, s.SwitchPort, timeoutMs) : CanReach(s.SwitchIP, s.SwitchPort, timeoutMs) || Usb();
    }

    /// <summary>Checks that are always on: disk space, program files, settings, the log.</summary>
    public static void RegisterBuiltIns()
    {
        Register("system", () =>
        {
            var items = new List<HealthItem>();
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(AppPaths.DataDir)!);
                var gb = drive.AvailableFreeSpace / 1e9;
                if (gb < 1) items.Add(Error("Disk space", $"Only {gb:0.0} GB free on {drive.Name} - recordings and saves may fail."));
                else if (gb < 5) items.Add(Warn("Disk space", $"Only {gb:0.0} GB free on {drive.Name}."));
            }
            catch { }
            if (!File.Exists(Path.Combine(AppPaths.AssetsDir, "Dex", "dex_index.json")))
                items.Add(Error("Program files", "Pokédex data (Assets\\Dex) is missing - reinstall or re-extract ShinyForge."));
            try
            {
                var probe = Path.Combine(AppPaths.DataDir, ".health");
                File.WriteAllText(probe, DateTime.Now.ToString("s"));
                File.Delete(probe);
            }
            catch (Exception e) { items.Add(Error("Settings / data folder", $"Can't write to {AppPaths.DataDir}: {e.Message}")); }
            return items;
        });
    }
}
