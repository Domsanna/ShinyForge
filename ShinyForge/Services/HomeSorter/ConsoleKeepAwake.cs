using ShinyForge.Core;

namespace ShinyForge.Services.HomeSorter;

/// <summary>
/// Keeps an unmodded Switch / Switch 2 from dimming its screen and going to sleep: every few minutes the PABotBase2 board
/// (the same ESP32-S3 the HOME sorter uses) tilts the right stick a little for 60 ms. Tested on the user's Switch 2: it
/// wakes a dimmed screen and changes nothing in HOME (box, cursor and selection stay put). In a game the camera may move a
/// tiny bit. Skipped while the HOME sorter uses the board (its own presses keep the console awake) and while another program
/// (Pokémon Automation) holds the board's port.
/// </summary>
public static class ConsoleKeepAwake
{
    /// <summary>One user of the board at a time: the sorter, its setup check, or a keep-awake nudge.</summary>
    public static readonly SemaphoreSlim BoardGate = new(1, 1);

    private static System.Threading.Timer? _timer;
    private static int _minutes;
    public static string Status { get; private set; } = "Off.";
    public static event Action? StatusChanged;

    /// <summary>Starts, restarts or stops the nudges to match the HOME sorter's settings.</summary>
    public static void Apply()
    {
        var o = AppSettings.Current.HomeSorter;
        var minutes = Math.Clamp(o.KeepAwakeMinutes, 1, 30);
        if (!o.KeepAwake)
        {
            _timer?.Dispose();
            _timer = null;
            Set("Off.");
            return;
        }
        if (_timer != null && minutes == _minutes) return;
        _minutes = minutes;
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ => Nudge(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(minutes));
        Set($"On - a nudge every {minutes} min.");
    }

    /// <summary>One right-stick nudge now (if the board is free).</summary>
    public static void Nudge()
    {
        if (!BoardGate.Wait(0)) { Set($"On - the sorter is using the controller (it keeps the console awake). {DateTime.Now:HH:mm}"); return; }
        try
        {
            var o = AppSettings.Current.HomeSorter;
            var port = o.ComPort.Length > 0 ? o.ComPort : PokeldnService.FindBoards().Select(b => b.Port).FirstOrDefault();
            if (port == null) { Set("On - no controller board found (ESP32-S3 / Pico W)."); return; }
            using var pad = PaBotBase2.Connect(port, _ => { });
            pad.Hold(PaBotBase2.Button.None, 60, 0x800 + 0x380, 0x800);
            Set($"On - last nudge {DateTime.Now:HH:mm} (every {_minutes} min).");
        }
        catch (Exception e) { Set($"On - couldn't nudge at {DateTime.Now:HH:mm}: {e.Message}"); }
        finally { BoardGate.Release(); }
    }

    private static void Set(string s)
    {
        Status = s;
        StatusChanged?.Invoke();
    }
}
