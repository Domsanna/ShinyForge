using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// One sys-botbase connection for driving the Switch: button taps, the running title, screenshots and heap reads.
/// Works with olliz0r's sys-botbase 2.x and Pokémon Automation's sys-botbase-cpp 3.x (backwards-compatible mode), over WiFi
/// or USB (<see cref="AppSettings.SwitchConnection"/>).
///
/// Refused presses are noticed: the virtual controller sometimes goes stale (e.g. after a homebrew app ran) and then every
/// press answers "hiddbgSetHdlsState: ..." - the session resets the controller and repeats the press once.
/// Failed reads: 2.x answers an error line plus a blank line, 3.x answers "0". Nothing ever waits for the blank line
/// (that hung 15 s with 3.x); leftovers are drained before the next request.
/// </summary>
internal sealed class SysBotSession : IDisposable
{
    private static readonly string[] AllButtons = { "A", "B", "X", "Y", "L", "R", "ZL", "ZR", "PLUS", "MINUS", "HOME", "DUP", "DDOWN", "DLEFT", "DRIGHT" };
    private readonly SysBotLink _link;
    private readonly bool _controller;

    /// <summary>
    /// Connects the way the settings say: USB (cable) or WiFi (<paramref name="host"/>:<paramref name="port"/>).
    /// <paramref name="controller"/> false = read-only use (title, memory, screenshots): the virtual controller is left alone,
    /// so a hunt that's pressing buttons through its own connection keeps working (sys-botbase has ONE virtual controller).
    /// </summary>
    public SysBotSession(string host, int port, bool controller = true) : this(OpenLink(host, port), controller) { }

    /// <summary>
    /// The connection that's set - or the other one, when only that answers: sys-botbase listens on ONE of them (its config.cfg)
    /// and a change only takes effect after the Switch restarts, so whatever answers is what the Switch runs; the setting follows.
    /// </summary>
    private static SysBotLink OpenLink(string host, int port)
    {
        var s = AppSettings.Current;
        try { return s.SwitchUsb ? new UsbLink() : new WifiLink(host, port); }
        catch (IOException)
        {
            var other = OtherLink(host, port, s.SwitchUsb);
            if (other == null) throw; // neither answers: the error for the set connection
            s.SwitchConnection = other.IsUsb ? "USB" : "WiFi";
            s.Save();
            AppPaths.Log($"sys-botbase answers over {s.SwitchConnection}, not the set connection - setting changed to match");
            return other;
        }
    }

    /// <summary>The connection that ISN'T set, if it answers (USB only when a Switch is plugged in).</summary>
    private static SysBotLink? OtherLink(string host, int port, bool usbIsSet)
    {
        try
        {
            if (usbIsSet) return new WifiLink(host, port);
            return WinUsbDevice.IsConnected(0x057E, 0x3000) ? new UsbLink() : null;
        }
        catch (IOException) { return null; }
    }

    internal SysBotSession(SysBotLink link, bool controller = true)
    {
        _link = link;
        _controller = controller;
        if (!_link.IsUsb) Send("configure echoCommands 0"); // USB never echoes
        if (controller) ResetController();
    }

    public bool IsUsb => _link.IsUsb;

    public void Send(string cmd) => _link.Send(cmd);

    /// <summary>Release everything, detach and attach a fresh virtual controller (what the Python hunter does on connect).</summary>
    private void ResetController()
    {
        foreach (var b in AllButtons) Send("release " + b);
        Send("detachController");
        Thread.Sleep(500);
        Send("setStick LEFT 0 0"); // attaches the new controller (the first input after attaching is lost)
        Thread.Sleep(1000);
        _link.TakePending();
    }

    /// <summary>
    /// A press held long enough not to be dropped over WiFi. A refused press (stale controller) resets the controller and is
    /// repeated once; if it's still refused, an <see cref="IOException"/> says so.
    /// </summary>
    public void Tap(string button, int waitMs = 350)
    {
        if (!_controller) throw new InvalidOperationException("This sys-botbase session is read-only.");
        for (var attempt = 0; ; attempt++)
        {
            Send("press " + button); Thread.Sleep(160); Send("release " + button); Thread.Sleep(waitMs);
            if (!_link.TakePending().Contains("hiddbg")) return;
            if (attempt == 1)
                throw new IOException("sys-botbase can't press buttons on the Switch right now (its virtual controller is stuck). " +
                                      "Restart the Switch or close the app that's open, then try again.");
            ResetController();
        }
    }

    /// <summary>Hold a button (until <see cref="Release"/>).</summary>
    public void Hold(string button) => Send("press " + button);
    public void Release(string button) => Send("release " + button);

    /// <summary>The running game's title ID, or null when nothing runs.</summary>
    public string? TitleId()
    {
        var reply = _link.Request("getTitleID", SysBotReply.Value);
        if (reply == null) return null;
        var id = _link.IsUsb
            ? (reply.Length == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(reply).ToString("X16") : null)
            : Encoding.ASCII.GetString(reply).Trim();
        return id != null && Regex.IsMatch(id, "^[0-9A-Fa-f]{16}$") && id.TrimStart('0').Length > 0 ? id.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Does sys-botbase answer at all? getTitleID is known by every version and always answers (zeros on the HOME menu). A
    /// sys-botbase-cpp whose command thread crashed still accepts connections but never answers anything.
    /// </summary>
    public bool Answers(int timeoutMs = 3000)
    {
        _link.ReadTimeoutMs = timeoutMs;
        try { return _link.Request("getTitleID", SysBotReply.Value) != null; }
        catch (IOException) { return false; }
    }

    /// <summary>The Switch's screen as a JPEG, or null.</summary>
    public byte[]? Screenshot() => _link.Request("pixelPeek", SysBotReply.Image);

    /// <summary>Bytes from the game's heap, or null when sys-botbase can't read there.</summary>
    public byte[]? Peek(uint heapOffset, int size)
    {
        var reply = _link.Request($"peek 0x{heapOffset:X} {size}", SysBotReply.Bytes);
        return reply != null && reply.Length == size ? reply : null;
    }

    /// <summary>
    /// sys-botbase's version ("2.5", "3.33", ...), or null if it doesn't say. Very old versions don't know the command and
    /// never answer, so this waits only <paramref name="timeoutMs"/> (the session isn't usable afterwards if it timed out).
    /// </summary>
    public string? Version(int timeoutMs = 3000)
    {
        _link.ReadTimeoutMs = timeoutMs;
        try { return _link.Request("getVersion", SysBotReply.Text) is { } v ? Encoding.ASCII.GetString(v).Trim() : null; }
        catch (IOException) { return null; }
    }

    /// <summary>Leaves no button held (R especially) and detaches the virtual controller cleanly.</summary>
    public void Dispose()
    {
        if (_controller)
            try { foreach (var b in AllButtons) Send("release " + b); Send("detachController"); Thread.Sleep(200); } catch { }
        _link.Dispose();
    }
}

/// <summary>What a request answers: a hex value (title ID), hex bytes (peek), a hex picture, or plain text (version).</summary>
internal enum SysBotReply { Value, Bytes, Image, Text }

/// <summary>A transport to sys-botbase. Commands without an answer are just sent; requests return the answer's payload.</summary>
internal abstract class SysBotLink : IDisposable
{
    public abstract bool IsUsb { get; }
    /// <summary>How long a request waits for its answer.</summary>
    public abstract int ReadTimeoutMs { set; }
    public abstract void Send(string cmd);
    /// <summary>The decoded answer (WiFi: the hex line turned into bytes; USB: the raw bytes), or null for an error answer.</summary>
    public abstract byte[]? Request(string cmd, SysBotReply kind);
    /// <summary>Everything sys-botbase sent that nobody asked for (error lines from presses), without waiting.</summary>
    public abstract string TakePending();
    public abstract void Dispose();
}

/// <summary>WiFi: one text line per command ("\r\n"), answers are text lines (hex for data).</summary>
internal sealed class WifiLink : SysBotLink
{
    private readonly TcpClient _c = new();
    private readonly NetworkStream _s;
    private readonly byte[] _buf = new byte[1 << 16];
    private int _start, _end;

    public WifiLink(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || !_c.ConnectAsync(host, port).Wait(3000) || !_c.Connected)
        {
            _c.Dispose();
            throw new IOException($"sys-botbase doesn't answer at {host}:{port}.");
        }
        _c.ReceiveTimeout = 15000;
        _s = _c.GetStream();
    }

    public override bool IsUsb => false;
    public override int ReadTimeoutMs { set => _c.ReceiveTimeout = value; }

    public override void Send(string cmd)
    {
        var b = Encoding.ASCII.GetBytes(cmd + "\r\n");
        _s.Write(b, 0, b.Length);
    }

    public override byte[]? Request(string cmd, SysBotReply kind)
    {
        TakePending();
        Send(cmd);
        // 2.x: a failed request answers error lines ("svc...", "pmdmnt...") and then a blank line; 3.x answers "0".
        for (var i = 0; i < 8; i++)
        {
            var line = ReadLine()?.Trim();
            if (line == null) return null;
            if (line.Length == 0) return null;                 // the blank line after 2.x error lines
            if (kind == SysBotReply.Text) return Encoding.ASCII.GetBytes(line);
            if (line.Length % 2 == 0 && line.All(Uri.IsHexDigit) && line.Length >= (kind == SysBotReply.Image ? 1000 : 2))
                return kind == SysBotReply.Value ? Encoding.ASCII.GetBytes(line) : Convert.FromHexString(line);
            if (line == "0") return null;                      // 3.x: the read failed
            // an error line: more may follow (2.x), keep reading
        }
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

    public override string TakePending()
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

    public override void Dispose() => _c.Dispose();
}

/// <summary>
/// USB (usb-botbase protocol, as SysBot.NET speaks it): each command is a 4-byte little-endian length (command + 2) followed by
/// the command without "\r\n"; each answer is a 4-byte length followed by the raw bytes (no hex). The Switch shows up as
/// VID 057E / PID 3000 and needs the WinUSB driver (Zadig) - <see cref="WinUsbDevice"/>.
/// </summary>
internal sealed class UsbLink : SysBotLink
{
    private readonly WinUsbDevice _dev;

    public UsbLink()
    {
        if (!OsInfo.IsWindows) throw new IOException("The USB connection to the Switch works on Windows only - use WiFi (Settings).");
        _dev = WinUsbDevice.Open(0x057E, 0x3000) ?? throw new IOException(
            "No Switch running sys-botbase is connected by USB. Plug the Switch (out of the dock) into this PC with a USB-C cable, " +
            "set sys-botbase to USB (Switch card → sys-botbase), and install the WinUSB driver for it with Zadig once.");
    }

    public override bool IsUsb => true;
    public override int ReadTimeoutMs { set => _dev.TimeoutMs = (uint)value; }

    public override void Send(string cmd)
    {
        var body = Encoding.ASCII.GetBytes(cmd);
        var head = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)body.Length + 2);
        _dev.Write(head);
        _dev.Write(body);
    }

    /// <summary>
    /// The raw answer, or null when there's none: sys-botbase-cpp sends NOTHING over USB when a result is empty (e.g. a
    /// failed screenshot), so a request that times out is treated as "no answer" rather than an error. A failed memory read
    /// answers "0\n" - callers check the length they expect.
    /// </summary>
    public override byte[]? Request(string cmd, SysBotReply kind)
    {
        Send(cmd);
        try
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(_dev.ReadExactly(4));
            return size <= 0 || size > 0x200000 ? null : _dev.ReadExactly(size);
        }
        catch (TimeoutException) { return null; }
    }

    public override string TakePending() => ""; // USB answers only requests

    public override void Dispose() => _dev.Dispose();
}
