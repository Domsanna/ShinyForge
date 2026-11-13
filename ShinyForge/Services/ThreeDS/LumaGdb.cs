using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ShinyForge.Services.ThreeDS;

public sealed record GdbProcess(int Pid, string Name, string Raw);

/// <summary>
/// Minimal GDB Remote Serial Protocol client for Luma3DS's built-in debugger (Rosalina → Debugger options → Enable debugger,
/// TCP 4000). Lists processes, attaches to the game and reads memory; it never writes memory. Each read briefly pauses the
/// game (interrupt → read → continue). Protocol details follow SirSnorealot/3DSShinyHunter's rsp.py (GPL-2.0).
/// </summary>
public sealed class LumaGdb : IDisposable
{
    public const int DefaultPort = 4000;
    private const int MaxRead = 0x180;
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _tcp;
    private NetworkStream? _s;
    private int _pid = -1;
    private bool _running;
    private readonly object _lock = new();

    public LumaGdb(string host, int port = DefaultPort)
    {
        _host = host;
        _port = port;
    }

    public int Pid => _pid;

    public void Connect()
    {
        _tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 4000, SendTimeout = 4000 };
        if (!_tcp.ConnectAsync(_host, _port).Wait(4000))
            throw new IOException($"Couldn't connect to the 3DS debugger at {_host}:{_port}. In Rosalina choose Debugger options → Enable debugger.");
        _s = _tcp.GetStream();
        var sup = Request("qSupported:multiprocess+;xmlRegisters=i386");
        if (!sup.Contains("multiprocess+"))
            throw new IOException("The 3DS debugger answered unexpectedly (is this Luma3DS?).");
        if (Request("!") != "OK")
            throw new IOException("The 3DS debugger refused extended mode.");
    }

    // ------------------------------------------------------------------ packets
    private static string Checksum(string payload) => (payload.Sum(c => (int)c) & 0xFF).ToString("x2");

    private void SendPacket(string payload)
    {
        var bytes = Encoding.ASCII.GetBytes($"${payload}#{Checksum(payload)}");
        _s!.Write(bytes, 0, bytes.Length);
    }

    private int ReadByte()
    {
        var b = _s!.ReadByte();
        if (b < 0) throw new IOException("The 3DS debugger closed the connection.");
        return b;
    }

    /// <summary>Reads one packet payload (skips +/- acks), verifies the checksum, acknowledges, unescapes and expands RLE.</summary>
    private string ReadPacket()
    {
        int b;
        while ((b = ReadByte()) != '$') { /* skip acks / noise */ }
        var raw = new List<byte>();
        while ((b = ReadByte()) != '#') raw.Add((byte)b);
        var sum = $"{(char)ReadByte()}{(char)ReadByte()}";
        var calc = (raw.Sum(x => x) & 0xFF).ToString("x2");
        if (!string.Equals(sum, calc, StringComparison.OrdinalIgnoreCase))
        {
            _s!.WriteByte((byte)'-');
            throw new IOException("Corrupted packet from the 3DS debugger.");
        }
        _s!.WriteByte((byte)'+');

        var sb = new StringBuilder();
        for (int i = 0; i < raw.Count; i++)
        {
            var c = raw[i];
            if (c == 0x7D) { sb.Append((char)(raw[++i] ^ 0x20)); continue; }
            if (c == 0x2A && sb.Length > 0) // run-length: previous char repeated (n - 29) more times
            {
                var count = raw[++i] - 29;
                sb.Append(sb[^1], count);
                continue;
            }
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    private string Request(string payload)
    {
        SendPacket(payload);
        return ReadPacket();
    }

    /// <summary>Waits for a reply that isn't an asynchronous stop notification (S/T), unless <paramref name="wantStop"/>.</summary>
    private string ReadReply(bool wantStop = false)
    {
        while (true)
        {
            var p = ReadPacket();
            if (p.StartsWith("W") || p.StartsWith("X"))
            {
                _pid = -1;
                throw new IOException("The game closed on the 3DS.");
            }
            var isStop = p.StartsWith("S") || p.StartsWith("T");
            if (isStop == wantStop) return p;
        }
    }

    // ------------------------------------------------------------------ processes
    public List<GdbProcess> ListProcesses()
    {
        lock (_lock)
        {
            var xml = new StringBuilder();
            int offset = 0;
            while (true)
            {
                var r = Request($"qXfer:osdata:read:processes:{offset:x},300");
                if (r.Length == 0 || r.StartsWith("E")) break;
                xml.Append(r[1..]);
                offset += r.Length - 1;
                if (r[0] == 'l') break;
            }
            var list = new List<GdbProcess>();
            foreach (Match item in Regex.Matches(xml.ToString(), "<item>(.*?)</item>", RegexOptions.Singleline))
            {
                var cols = Regex.Matches(item.Groups[1].Value, "<column name=\"([^\"]+)\">(.*?)</column>", RegexOptions.Singleline)
                    .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
                if (!cols.TryGetValue("pid", out var pidText)) continue;
                var pid = pidText.StartsWith("0x") ? Convert.ToInt32(pidText[2..], 16) : int.Parse(pidText);
                cols.TryGetValue("command", out var name);
                list.Add(new GdbProcess(pid, name ?? "", string.Join(" ", cols.Values)));
            }
            return list;
        }
    }

    public void Attach(int pid)
    {
        lock (_lock)
        {
            var r = Request($"vAttach;{pid:x}");
            if (r.StartsWith("E"))
                throw new IOException($"Couldn't attach to process {pid} on the 3DS.");
            _pid = pid;
            _running = false; // attach stops the target
            Continue();
        }
    }

    private void Continue()
    {
        SendPacket("vCont;c"); // the stop reply arrives asynchronously, later
        _running = true;
    }

    private void Interrupt()
    {
        if (!_running) return;
        _s!.WriteByte(0x03);
        ReadReply(wantStop: true);
        _running = false;
    }

    /// <summary>Reads memory from the attached game (pauses it for a moment).</summary>
    public byte[] Read(uint address, int length)
    {
        lock (_lock)
        {
            if (_pid < 0) throw new InvalidOperationException("Not attached to the game.");
            Interrupt();
            try
            {
                var result = new byte[length];
                for (int off = 0; off < length; off += MaxRead)
                {
                    var n = Math.Min(MaxRead, length - off);
                    SendPacket($"m{address + (uint)off:x},{n:x}");
                    var hex = ReadReply();
                    if (hex.StartsWith("E") || hex.Length != n * 2)
                        throw new IOException($"Memory read failed at 0x{address + (uint)off:X8} ({hex}).");
                    Convert.FromHexString(hex).CopyTo(result, off);
                }
                return result;
            }
            finally
            {
                Continue();
            }
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            if (_pid < 0) return;
            try
            {
                Interrupt();
                var r = Request($"D;{_pid:x}");
                if (r.StartsWith("E")) Request("D");
            }
            catch { }
            _pid = -1;
        }
    }

    public void Dispose()
    {
        try { Detach(); } catch { }
        _tcp?.Dispose();
    }
}
