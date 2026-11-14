using System.Runtime.InteropServices;
using Microsoft.Win32;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Keeps Windows from going to sleep while a hunt or a recording is running (the screen may still turn off), and reports
/// it through the system check if the PC slept anyway.
/// </summary>
public static class PowerGuard
{
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_AWAYMODE_REQUIRED = 0x40;

    /// <summary>Is anything running that must not be interrupted? (set by the features, checked every minute)</summary>
    public static Func<string?> BusyReason { get; set; } = () => null;
#pragma warning disable IDE0052 // kept in a field so the timer isn't garbage-collected
    private static System.Threading.Timer? _timer;
#pragma warning restore IDE0052
    private static bool _held;
    private static Thread? _thread;
    private static readonly AutoResetEvent Wake = new(false);
    public static string? HeldFor { get; private set; }

    public static void Start()
    {
        // SetThreadExecutionState is per thread: keep one dedicated thread that holds/release the state.
        _thread = new Thread(Loop) { IsBackground = true, Name = "PowerGuard" };
        _thread.Start();
        _timer = new System.Threading.Timer(_ => Wake.Set(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        if (OperatingSystem.IsWindows())
            SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == PowerModes.Resume && HeldFor != null)
                    HealthMonitor.Report("Keep awake", HealthState.Error,
                        $"The PC went to sleep while {HeldFor} was running - check the power settings (a laptop lid or the power button can still force sleep).");
            };
    }

    private static System.Diagnostics.Process? _inhibitor;

    /// <summary>Windows: SetThreadExecutionState. macOS: caffeinate -i. Linux: systemd-inhibit --what=sleep. (Each until released.)</summary>
    private static void Hold(bool on, string reason)
    {
        if (OperatingSystem.IsWindows())
        {
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED : ES_CONTINUOUS);
            return;
        }
        try { if (_inhibitor is { HasExited: false }) _inhibitor.Kill(); } catch { }
        _inhibitor = null;
        if (!on) return;
        try
        {
            var psi = OperatingSystem.IsMacOS()
                ? new System.Diagnostics.ProcessStartInfo("caffeinate", $"-i -w {Environment.ProcessId}")
                : new System.Diagnostics.ProcessStartInfo("systemd-inhibit", $"--what=sleep:idle --who=ShinyForge --why=\"{reason.Replace("\"", "")}\" --mode=block sleep infinity");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            _inhibitor = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception e) { AppPaths.Log("Keep awake isn't available here: " + e.Message); }
    }

    private static void Loop()
    {
        while (true)
        {
            Wake.WaitOne();
            string? reason = null;
            try { reason = AppSettings.Current.KeepAwake ? BusyReason() : null; } catch { }
            if (reason != null && !_held)
            {
                Hold(true, reason);
                _held = true;
                AppPaths.Log("Keep awake ON: " + reason);
            }
            else if (reason == null && _held)
            {
                Hold(false, "");
                _held = false;
                AppPaths.Log("Keep awake OFF");
            }
            HeldFor = reason;
        }
    }
}
