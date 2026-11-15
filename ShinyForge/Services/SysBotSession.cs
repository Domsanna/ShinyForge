using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ShinyForge.Services;

/// <summary>
/// One sys-botbase connection for driving the Switch: button taps, the running title, screenshots and heap reads.
/// Replies are read straight from the socket (no hidden buffering) so error lines from refused presses can be noticed:
/// sys-botbase's virtual controller sometimes goes stale (e.g. after a homebrew app ran) and then every press answers
/// "hiddbgSetHdlsState: ..." - the session resets the controller and repeats the press once.
/// </summary>
internal sealed class SysBotSession : IDisposable
{
    private static readonly string[] AllButtons = { "A", "B", "X", "Y", "L", "R", "ZL", "ZR", "PLUS", "MINUS", "HOME", "DUP", "DDOWN", "DLEFT", "DRIGHT" };
    private readonly TcpClient _c = new();
    private readonly NetworkStream _s;
    private readonly byte[] _buf = new byte[1 << 16];
    private int _start, _end;

    public SysBotSession(string host, int port)
    {
        if (!_c.ConnectAsync(host, port).Wait(3000) || !_c.Connected)
        {
            _c.Dispose();
            throw new IOException($"sys-botbase doesn't answer at {host}:{port}.");
        }
        _c.ReceiveTimeout = 15000;
        _s = _c.GetStream();
        Send("configure echoCommands 0");
        ResetController();
    }

    public void Send(string cmd)
    {
        var b = Encoding.ASCII.GetBytes(cmd + "\r\n");
        _s.Write(b, 0, b.Length);
    }

    /// <summary>Release everything, detach and attach a fresh virtual controller (what the Python hunter does on connect).</summary>
    private void ResetController()
    {
        foreach (var b in AllButtons) Send("release " + b);
        Send("detachController");
        Thread.Sleep(500);
        Send("setStick LEFT 0 0"); // attaches the new controller (the first input after attaching is lost)
        Thread.Sleep(1000);
        TakePending();
    }

    /// <summary>
    /// A press held long enough not to be dropped over WiFi. A refused press (stale controller) resets the controller and is
    /// repeated once; if it's still refused, an <see cref="IOException"/> says so.
    /// </summary>
    public void Tap(string button, int waitMs = 350)
    {
        for (var attempt = 0; ; attempt++)
        {
            Send("press " + button); Thread.Sleep(160); Send("release " + button); Thread.Sleep(waitMs);
            if (!TakePending().Contains("hiddbg")) return;
            if (attempt == 1)
                throw new IOException("sys-botbase can't press buttons on the Switch right now (its virtual controller is stuck). " +
                                      "Restart the Switch or close the app that's open, then try again.");
            ResetController();
        }
    }

    /// <summary>Hold a button (until <see cref="Release"/>).</summary>
    public void Hold(string button) => Send("press " + button);
    public void Release(string button) => Send("release " + button);

    /// <summary>The running game's title ID, or null when nothing runs (sys-botbase answers with error lines + a blank line).</summary>
    public string? TitleId()
    {
        TakePending();
        Send("getTitleID");
        for (var i = 0; i < 8; i++)
        {
            var line = ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line)) return null;
            if (Regex.IsMatch(line, "^[0-9A-Fa-f]{16}$")) return line.TrimStart('0').Length == 0 ? null : line.ToUpperInvariant();
        }
        return null;
    }

    public byte[]? Screenshot()
    {
        TakePending();
        Send("pixelPeek");
        for (var i = 0; i < 20; i++)
        {
            var line = ReadLine()?.Trim();
            if (line == null) return null;
            if (line.Length < 1000) continue; // an error line, not the picture
            return line.Length % 2 != 0 || !line.All(Uri.IsHexDigit) ? null : Convert.FromHexString(line);
        }
        return null;
    }

    /// <summary>Bytes from the game's heap, or null when sys-botbase can't read there (an error line + a blank line).</summary>
    public byte[]? Peek(uint heapOffset, int size)
    {
        TakePending();
        Send($"peek 0x{heapOffset:X} {size}");
        var line = ReadLine()?.Trim() ?? "";
        if (line.Length == size * 2 && line.All(Uri.IsHexDigit)) return Convert.FromHexString(line);
        if (line.Length > 0) ReadLine(); // failed reads are followed by a blank line
        return null;
    }

    private string? ReadLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var nl = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);
            if (nl >= 0)
            {
                sb.Append(Encoding.ASCII.GetString(_buf, _start, nl - _start));
                _start = nl + 1;
                return sb.ToString().TrimEnd('\r');
            }
            sb.Append(Encoding.ASCII.GetString(_buf, _start, _end - _start));
            _start = _end = 0;
            var n = _s.Read(_buf, 0, _buf.Length);
            if (n <= 0) return sb.Length > 0 ? sb.ToString() : null;
            _end = n;
        }
    }

    /// <summary>Everything sys-botbase sent that nobody asked for (error lines from presses), without waiting.</summary>
    private string TakePending()
    {
        var sb = new StringBuilder();
        while (_start < _end || _c.Available > 0)
        {
            if (_start == _end) { _end = _s.Read(_buf, 0, _buf.Length); _start = 0; if (_end <= 0) { _end = 0; break; } }
            sb.Append(Encoding.ASCII.GetString(_buf, _start, _end - _start));
            _start = _end = 0;
        }
        return sb.ToString();
    }

    /// <summary>Leaves no button held (R especially) and detaches the virtual controller cleanly.</summary>
    public void Dispose()
    {
        try { foreach (var b in AllButtons) Send("release " + b); Send("detachController"); Thread.Sleep(200); } catch { }
        _c.Dispose();
    }
}
