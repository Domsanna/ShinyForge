using System.Diagnostics;
using ShinyForge.Core;

namespace ShinyForge.Services.ThreeDS;

/// <summary>
/// Presses 3DS buttons by hand from the PC with TPPFLUSH (u93132/TPPFLUSH, MIT - "The Python Presser Library For Luma3DS
/// Unbricked Systems that are Hacked", by hlixed). Runs Assets\TPPFLUSH\shinyforge_tppflush.py with Python and sends it one
/// command per line (press / hold / release / touch / cpad / cstick / clear / quit). Needs Luma3DS InputRedirection on.
/// </summary>
public sealed class TppflushController : IDisposable
{
    public static string Folder => Path.Combine(AppPaths.AssetsDir, "TPPFLUSH");
    public static string Script => Path.Combine(Folder, "shinyforge_tppflush.py");

    public static readonly string[] Buttons =
        { "A", "B", "X", "Y", "L", "R", "ZL", "ZR", "START", "SELECT", "DPADUP", "DPADDOWN", "DPADLEFT", "DPADRIGHT", "HOME", "POWER" };

    private Process? _p;
    private readonly object _lock = new();
    public string Host { get; }
    /// <summary>A line from the script ("ready", "ok press", "error: ...").</summary>
    public event Action<string>? Output;
    public bool Running => _p is { HasExited: false };

    public TppflushController(string host) => Host = host;

    public void Start()
    {
        if (Running) return;
        var py = DependencyService.PathOf("Python 3") ?? throw new FileNotFoundException("Python 3 wasn't found - install it first (Tools & Updates).");
        if (!File.Exists(Script)) throw new FileNotFoundException("TPPFLUSH isn't installed with this copy of ShinyForge.", Script);
        var psi = new ProcessStartInfo(py)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Folder,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var a in new[] { "-I", "-u", Script, Host }) psi.ArgumentList.Add(a);
        _p = Process.Start(psi) ?? throw new InvalidOperationException("Python didn't start.");
        _p.OutputDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
        _p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Output?.Invoke("error: " + e.Data); };
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
    }

    /// <summary>Sends one command (started first if needed).</summary>
    public void Send(string command)
    {
        lock (_lock)
        {
            if (!Running) Start();
            _p!.StandardInput.WriteLine(command);
            _p.StandardInput.Flush();
        }
    }

    public void Press(string button, double seconds = 0.15) => Send(FormattableString.Invariant($"press {button} {seconds:0.###}"));
    public void Hold(string button) => Send("hold " + button);
    public void Release(string button) => Send("release " + button);
    public void Touch(int x, int y, double seconds = 0.15) => Send(FormattableString.Invariant($"touch {Math.Clamp(x, 0, 319)} {Math.Clamp(y, 0, 239)} {seconds:0.###}"));
    public void CirclePad(double x, double y) => Send(FormattableString.Invariant($"cpad {x:0.###} {y:0.###}"));
    public void CStick(double x, double y) => Send(FormattableString.Invariant($"cstick {x:0.###} {y:0.###}"));

    /// <summary>Lets go of everything and gives the buttons back to the 3DS.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_p == null) return;
            try
            {
                if (!_p.HasExited)
                {
                    _p.StandardInput.WriteLine("quit");
                    _p.StandardInput.Flush();
                    if (!_p.WaitForExit(2000)) _p.Kill();
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
            _p.Dispose();
            _p = null;
        }
    }
}
