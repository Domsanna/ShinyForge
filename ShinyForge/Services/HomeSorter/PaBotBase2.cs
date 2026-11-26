using System.Buffers.Binary;
using System.IO.Ports;
using System.Runtime.Intrinsics.X86;

namespace ShinyForge.Services.HomeSorter;

/// <summary>
/// Talks to a microcontroller flashed with Pokémon Automation's PABotBase2 firmware (ESP32-S3, ESP32, Pico W, RP2040...) over
/// its USB serial port. The board plugs into an UNMODDED Switch / Switch 2 as a wired Pro Controller, so ShinyForge can press
/// buttons without sys-botbase. ShinyForge only speaks the protocol - the firmware itself is Pokémon Automation's (free for
/// non-commercial use) and is flashed by the user with their tools.
///
/// The protocol is reimplemented from Pokémon Automation's open source (github.com/PokemonAutomation/Arduino-Source, MIT,
/// Common/PABotBase2): a reliable stream over serial (packets: magic 0x81, seqnum, length, opcode, CRC32C seeded with the
/// session id; every request is acked and re-sent until it is) carrying messages (u16 length, opcode, id). Controller input
/// goes through the device's command queue: "hold this button state for N ms", each answered by "command finished".
/// </summary>
public sealed class PaBotBase2 : IDisposable
{
    // ---- packet layer (PABotBase2_PacketProtocol.h)
    private const byte Magic = 0x81;
    private const byte RetransmitFlag = 0x80;
    private const byte AskReset = 0x01, RetReset = 0x41, AskVersion = 0x02, RetVersion = 0x42, AskPacketSize = 0x03, RetPacketSize = 0x43;
    private const byte AskBufferSlots = 0x04, RetBufferSlots = 0x44, AskStreamData = 0x12, RetStreamData = 0x52;
    private const uint ConnectionProtocol = 2026061800;

    // ---- message layer (PABotBase2_MessageProtocol.h)
    private const byte MsgRet = 0x11, MsgRetU32 = 0x12, MsgRetData = 0x13, MsgRetU32Data = 0x14;
    private const byte MsgProtocolVersion = 0x20, MsgFirmwareVersion = 0x21, MsgDeviceId = 0x22, MsgDeviceName = 0x23;
    private const byte MsgControllerList = 0x24, MsgSetLogging = 0x25, MsgCqCapacity = 0x26;
    private const byte MsgRequestStatus = 0x31, MsgReadControllerMode = 0x32, MsgChangeControllerMode = 0x33, MsgConsoleDisconnect = 0x37;
    private const byte MsgCqFinished = 0x43, MsgUsbDisallowed = 0x95, CmdOemButtons = 0x97;

    /// <summary>PABB_CID_NintendoSwitch_WiredProController - what Pokémon Automation uses on Switch 1 and Switch 2.</summary>
    public const uint WiredProController = 0x1100;

    private readonly SerialPort _port;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly Thread _reader, _resender;
    private volatile bool _closed;

    // sender state
    private uint _session;
    private byte _nextSeq;
    private int _maxPacket = 24;          // until the device says (0 = 256)
    private int _window = 1;              // unacked packets allowed (device's buffer slots)
    private ushort _sendOffset;           // stream offset of the next byte we send
    private readonly Dictionary<byte, (byte[] Packet, DateTime SentAt)> _unacked = new();

    // receiver state (the device's stream to us)
    private ushort _recvOffset;
    private readonly Dictionary<ushort, byte[]> _early = new();
    private readonly List<byte> _messages = new();
    private readonly List<byte> _rx = new();

    // requests and the command queue
    private byte _requestId = 1, _commandId = 1;
    private readonly Dictionary<byte, byte[]?> _responses = new();
    private readonly HashSet<byte> _pendingCommands = new();
    private int _queueSize = 4;
    private uint _remoteConnectionProtocol;

    public string DeviceName { get; private set; } = "";
    public uint ProtocolVersion { get; private set; }
    public uint FirmwareVersion { get; private set; }
    public uint ControllerMode { get; private set; }
    public List<uint> Controllers { get; } = new();
    /// <summary>Set when the console turned the controller away (off, rejected, wired communication disabled).</summary>
    public string? ConsoleProblem { get; private set; }

    private PaBotBase2(SerialPort port, Action<string> log)
    {
        _port = port;
        _log = log;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "PABotBase2 reader" };
        _resender = new Thread(ResendLoop) { IsBackground = true, Name = "PABotBase2 resend" };
    }

    /// <summary>COM ports that look like a PABotBase2 board (all of them - the board is found by talking to it).</summary>
    public static string[] Ports() => SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p).ToArray();

    /// <summary>
    /// Opens the port, finds the baud rate (921,600 or 115,200), resets the session, reads the device's details and makes
    /// sure it acts as a wired Pro Controller. Throws with a readable reason when it isn't a PABotBase2 board.
    /// </summary>
    public static PaBotBase2 Connect(string portName, Action<string>? log = null)
    {
        log ??= _ => { };
        var port = new SerialPort(portName, 921600) { ReadTimeout = 50, WriteTimeout = 1000, Handshake = System.IO.Ports.Handshake.None };
        try { port.Open(); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            throw new IOException($"{portName} is in use or gone. Close Pokémon Automation (it keeps the board's port open) and try again.", e);
        }
        // ESP32 boards reset on (DTR=0, RTS=1): bring the lines to the steady state DTR=1, RTS=0 like Pokémon Automation does
        port.RtsEnable = false;
        port.DtrEnable = true;
        var link = new PaBotBase2(port, log);
        link._reader.Start();
        link._resender.Start();
        try
        {
            if (!link.Handshake(TimeSpan.FromSeconds(3)))
            {
                log("No answer - trying to bring an ESP32 out of its bootloader.");
                port.DtrEnable = false; port.RtsEnable = true; Thread.Sleep(50);
                port.DtrEnable = true; Thread.Sleep(50);
                port.RtsEnable = false;
                if (!link.Handshake(TimeSpan.FromSeconds(2)))
                    throw new IOException($"{portName} didn't answer like a PABotBase2 board. Flash it with the firmware from Pokémon Automation's Firmware folder.");
            }
            link.QueryDevice();
            return link;
        }
        catch { link.Dispose(); throw; }
    }

    private bool Handshake(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        do
        {
            foreach (var baud in new[] { 921600, 115200 })
            {
                try { _port.BaudRate = baud; } catch (IOException) { }
                if (!Reset(TimeSpan.FromMilliseconds(150))) continue;
                _log($"PABotBase2 answered at {baud:N0} baud.");
                SendPacket(AskVersion, ReadOnlySpan<byte>.Empty);
                if (!WaitAcked(TimeSpan.FromSeconds(2))) return false;
                var major = _remoteConnectionProtocol / 100;
                if (major != ConnectionProtocol / 100)
                    _log($"Warning: the board's connection protocol is {_remoteConnectionProtocol}, ShinyForge expects {ConnectionProtocol / 100}xx - " +
                         "flash the firmware that came with your Pokémon Automation if buttons don't work.");
                SendPacket(AskPacketSize, ReadOnlySpan<byte>.Empty);
                WaitAcked(TimeSpan.FromSeconds(2));
                SendPacket(AskBufferSlots, ReadOnlySpan<byte>.Empty);
                WaitAcked(TimeSpan.FromSeconds(2));
                return true;
            }
        } while (DateTime.UtcNow < until);
        return false;
    }

    /// <summary>Starts a new session: forget everything in flight, send ASK_RESET (its CRC is seeded with 0xFFFFFFFF).</summary>
    private bool Reset(TimeSpan timeout)
    {
        lock (_lock)
        {
            _session = _session == 0 ? (uint)Random.Shared.Next() | 1u : _session + 1;
            _nextSeq = 0;
            _unacked.Clear();
            _sendOffset = 0;
            _recvOffset = 0;
            _early.Clear();
            _messages.Clear();
            _rx.Clear();
            _window = 1;
            var data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, _session);
            var packet = Build(_nextSeq++, AskReset, data, 0xFFFFFFFF);
            _unacked[packet[1]] = (packet, DateTime.UtcNow);
            Write(packet);
        }
        return WaitAcked(timeout);
    }

    private bool WaitAcked(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        lock (_lock)
            while (_unacked.Count > 0)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || _closed) return false;
                Monitor.Wait(_lock, left);
            }
        return true;
    }

    private void QueryDevice()
    {
        ProtocolVersion = QueryU32(MsgProtocolVersion);
        FirmwareVersion = QueryU32(MsgFirmwareVersion);
        var id = QueryU32(MsgDeviceId);
        DeviceName = System.Text.Encoding.ASCII.GetString(QueryData(MsgDeviceName)).TrimEnd('\0');
        _log($"Board: {DeviceName} (program id 0x{id:x2}), firmware {FirmwareVersion}, message protocol {ProtocolVersion}.");
        SendMessage(MsgSetLogging, U32(0)); // no log chatter from the board
        var list = QueryData(MsgControllerList);
        for (var i = 0; i + 4 <= list.Length; i += 4) Controllers.Add(BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(i)));
        _queueSize = (int)Math.Clamp(QueryU32(MsgCqCapacity), 4, 32);
        ControllerMode = QueryU32(MsgReadControllerMode);
        if (ControllerMode != WiredProController)
        {
            if (!Controllers.Contains(WiredProController))
                throw new IOException($"{DeviceName} can't be a wired Pro Controller (it offers {string.Join(", ", Controllers.Select(c => "0x" + c.ToString("x")))}).");
            _log($"Switching the board from controller 0x{ControllerMode:x} to a wired Pro Controller.");
            SendMessage(MsgChangeControllerMode, U32(WiredProController));
            Thread.Sleep(1500); // the board re-enumerates as the new controller
            ControllerMode = QueryU32(MsgReadControllerMode);
        }
    }

    /// <summary>Is the Switch talking to the controller? (status bit 0 = connected), null when the board doesn't say.</summary>
    public bool? ConsoleConnected()
    {
        var r = Query(MsgRequestStatus, ReadOnlySpan<byte>.Empty, TimeSpan.FromSeconds(2));
        if (r == null || r.Length < 9) return null;
        return (r[8] & 1) != 0;
    }

    // ================================================================ buttons
    [Flags]
    public enum Button : uint
    {
        None = 0,
        Y = 1 << 0, X = 1 << 1, B = 1 << 2, A = 1 << 3, R = 1 << 6, ZR = 1 << 7,                                  // byte 3
        Minus = 1 << 8, Plus = 1 << 9, RStick = 1 << 10, LStick = 1 << 11, Home = 1 << 12, Capture = 1 << 13,    // byte 4
        Down = 1 << 16, Up = 1 << 17, Right = 1 << 18, Left = 1 << 19, L = 1 << 22, ZL = 1 << 23,                // byte 5
    }

    /// <summary>Holds the buttons for <paramref name="holdMs"/>, then nothing for <paramref name="releaseMs"/>; returns when done.</summary>
    public void Press(Button buttons, int holdMs = 80, int releaseMs = 80)
    {
        var a = Command(buttons, holdMs);
        var b = Command(Button.None, Math.Max(releaseMs, 8));
        WaitCommand(a);
        WaitCommand(b);
    }

    /// <summary>
    /// Holds buttons and/or tilts the right stick (12-bit values, 0x800 = centre) for <paramref name="holdMs"/>, then lets go.
    /// </summary>
    public void Hold(Button buttons, int holdMs, int rightX = 0x800, int rightY = 0x800, int releaseMs = 80)
    {
        var a = Command(buttons, holdMs, rightX, rightY);
        var b = Command(Button.None, Math.Max(releaseMs, 8));
        WaitCommand(a);
        WaitCommand(b);
    }

    /// <summary>Queues "hold this state for ms" on the board (CMD_NS1_OEM_CONTROLLER_BUTTONS) and returns its id.</summary>
    private byte Command(Button buttons, int ms, int rightX = 0x800, int rightY = 0x800)
    {
        byte id;
        lock (_lock)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (_pendingCommands.Count >= _queueSize)
            {
                if (_closed) throw new IOException("The controller board disconnected.");
                if (DateTime.UtcNow > until) throw new TimeoutException("The controller board stopped finishing button presses.");
                Monitor.Wait(_lock, 200);
            }
            id = _commandId++;
            if (_commandId == 0) _commandId = 1;
            _pendingCommands.Add(id);
        }
        var body = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)Math.Clamp(ms, 1, 65535));
        var bits = (uint)buttons;
        body[2] = (byte)bits; body[3] = (byte)(bits >> 8); body[4] = (byte)(bits >> 16);
        body[5] = 0x00; body[6] = 0x08; body[7] = 0x80; // sticks centred (12-bit 0x800, 0x800)
        rightX = Math.Clamp(rightX, 0, 0xFFF); rightY = Math.Clamp(rightY, 0, 0xFFF);
        body[8] = (byte)rightX; body[9] = (byte)((rightX >> 8) | ((rightY & 0x0F) << 4)); body[10] = (byte)(rightY >> 4);
        body[11] = 0;                                    // vibrator
        SendMessage(CmdOemButtons, body, id);
        return id;
    }

    private void WaitCommand(byte id)
    {
        lock (_lock)
        {
            // a press takes well under a second (hold + release are a few hundred ms at most)
            var until = DateTime.UtcNow.AddSeconds(15);
            while (_pendingCommands.Contains(id))
            {
                if (_closed) throw new IOException("The controller board disconnected.");
                if (DateTime.UtcNow > until) throw new TimeoutException("The controller board never finished a button press.");
                Monitor.Wait(_lock, 200);
            }
        }
    }

    // ================================================================ requests
    private uint QueryU32(byte opcode)
    {
        var r = Query(opcode, ReadOnlySpan<byte>.Empty, TimeSpan.FromSeconds(3));
        if (r == null || r.Length < 8) throw new IOException($"The board didn't answer request 0x{opcode:x2}.");
        return BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(4));
    }

    private byte[] QueryData(byte opcode)
    {
        var r = Query(opcode, ReadOnlySpan<byte>.Empty, TimeSpan.FromSeconds(3));
        if (r == null || r.Length < 4) throw new IOException($"The board didn't answer request 0x{opcode:x2}.");
        return r[4..];
    }

    private byte[]? Query(byte opcode, ReadOnlySpan<byte> body, TimeSpan timeout)
    {
        byte id;
        lock (_lock)
        {
            id = _requestId++;
            if (_requestId == 0) _requestId = 1;
            _responses[id] = null;
        }
        SendMessage(opcode, body, id);
        var until = DateTime.UtcNow + timeout;
        lock (_lock)
        {
            while (_responses.TryGetValue(id, out var r) && r == null)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || _closed) { _responses.Remove(id); return null; }
                Monitor.Wait(_lock, left);
            }
            _responses.Remove(id, out var got);
            return got;
        }
    }

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    /// <summary>Sends one message (u16 total length, opcode, id, body) on the reliable stream.</summary>
    private void SendMessage(byte opcode, ReadOnlySpan<byte> body, byte id = 0)
    {
        var msg = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(msg, (ushort)msg.Length);
        msg[2] = opcode;
        msg[3] = id;
        body.CopyTo(msg.AsSpan(4));
        SendStream(msg);
    }

    /// <summary>Cuts the bytes into ASK_STREAM_DATA packets (u16 stream offset + data) no bigger than the device takes.</summary>
    private void SendStream(byte[] data)
    {
        var pos = 0;
        while (pos < data.Length)
        {
            lock (_lock)
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                while (_unacked.Count >= _window)
                {
                    if (_closed) throw new IOException("The controller board disconnected.");
                    if (DateTime.UtcNow > until) throw new TimeoutException("The controller board stopped answering.");
                    Monitor.Wait(_lock, 100);
                }
                var max = (_maxPacket == 0 ? 256 : _maxPacket) - 10; // header 4 + offset 2 + crc 4
                var n = Math.Min(max, data.Length - pos);
                var payload = new byte[2 + n];
                BinaryPrimitives.WriteUInt16LittleEndian(payload, _sendOffset);
                Array.Copy(data, pos, payload, 2, n);
                _sendOffset += (ushort)n;
                pos += n;
                var packet = Build(_nextSeq++, AskStreamData, payload, _session);
                _unacked[packet[1]] = (packet, DateTime.UtcNow);
                Write(packet);
            }
        }
    }

    /// <summary>A request packet that has to be acked (version, packet size, buffer slots).</summary>
    private void SendPacket(byte opcode, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            var packet = Build(_nextSeq++, opcode, data, _session);
            _unacked[packet[1]] = (packet, DateTime.UtcNow);
            Write(packet);
        }
    }

    private static byte[] Build(byte seq, byte opcode, ReadOnlySpan<byte> data, uint crcSeed)
    {
        var len = 4 + data.Length + 4;
        var p = new byte[len];
        p[0] = Magic; p[1] = seq; p[2] = (byte)len; p[3] = opcode; // 256 overflows to 0, like the device expects
        data.CopyTo(p.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(len - 4), Crc32C(crcSeed, p.AsSpan(0, len - 4)));
        return p;
    }

    private void Write(byte[] packet)
    {
        try { _port.Write(packet, 0, packet.Length); }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException) { Close(e.Message); }
    }

    /// <summary>Re-sends every unacked packet older than 80 ms (marked with the retransmit flag, CRC recomputed).</summary>
    private void ResendLoop()
    {
        while (!_closed)
        {
            Thread.Sleep(40);
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                foreach (var (seq, item) in _unacked.ToList())
                {
                    if (now - item.SentAt < TimeSpan.FromMilliseconds(80)) continue;
                    var p = (byte[])item.Packet.Clone();
                    var seed = (p[3] & 0x7F) == AskReset ? 0xFFFFFFFF : _session;
                    p[3] |= RetransmitFlag;
                    var len = p.Length;
                    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(len - 4), Crc32C(seed, p.AsSpan(0, len - 4)));
                    _unacked[seq] = (item.Packet, now);
                    Write(p);
                }
            }
        }
    }

    // ================================================================ receiving
    private void ReadLoop()
    {
        var buf = new byte[4096];
        while (!_closed)
        {
            int n;
            try { n = _port.Read(buf, 0, buf.Length); }
            catch (TimeoutException) { continue; }
            catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException) { Close("The controller board's port closed: " + e.Message); return; }
            lock (_lock)
            {
                for (var i = 0; i < n; i++) _rx.Add(buf[i]);
                ParsePackets();
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>Takes complete, CRC-correct packets off the front of the received bytes (resyncing on the magic byte).</summary>
    private void ParsePackets()
    {
        while (true)
        {
            var start = _rx.IndexOf(Magic);
            if (start < 0) { _rx.Clear(); return; }
            if (start > 0) _rx.RemoveRange(0, start);
            if (_rx.Count < 8) return;
            var len = _rx[2] == 0 ? 256 : _rx[2];
            if (len < 8) { _rx.RemoveAt(0); continue; }
            if (_rx.Count < len) return;
            var p = _rx.GetRange(0, len).ToArray();
            var opcode = (byte)(p[3] & 0x7F);
            var seed = opcode == AskReset ? 0xFFFFFFFF : _session;
            if (Crc32C(seed, p.AsSpan(0, len - 4)) != BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(len - 4)))
            {
                _rx.RemoveAt(0); // not a packet start after all - look for the next magic byte
                continue;
            }
            _rx.RemoveRange(0, len);
            OnPacket(p, opcode);
        }
    }

    private void OnPacket(byte[] p, byte opcode)
    {
        var seq = p[1];
        uint U() => p.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)) : 0;
        switch (opcode)
        {
            case RetReset:
            case RetStreamData:
                _unacked.Remove(seq);
                break;
            case RetVersion:
                _remoteConnectionProtocol = U();
                _unacked.Remove(seq);
                break;
            case RetPacketSize:
                _maxPacket = (int)(U() & 0xFF);
                _unacked.Remove(seq);
                break;
            case RetBufferSlots:
                _window = (int)Math.Clamp(U(), 1, 64);
                _unacked.Remove(seq);
                break;
            case AskStreamData:
                ReceiveStream(p, seq);
                break;
            // 0x10-0x28 are the board's out-of-band info/log packets, 0x30-0x32 its complaints about our packets
            case >= 0x30 and <= 0x32:
                _log($"The controller board rejected a packet (code 0x{opcode:x2}).");
                break;
        }
    }

    /// <summary>The board's stream: ack every packet (RET_STREAM_DATA + free bytes), keep the data in stream order.</summary>
    private void ReceiveStream(byte[] p, byte seq)
    {
        var ack = Build(seq, RetStreamData, U32(16384), _session);
        Write(ack);
        if (p.Length < 10) return;
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(4));
        var data = p.AsSpan(6, p.Length - 10).ToArray();
        if (data.Length == 0) return;
        var ahead = (ushort)(offset - _recvOffset);
        if (ahead >= 0x8000) return;                    // already have it (a re-send)
        _early[offset] = data;
        while (_early.Remove(_recvOffset, out var chunk))
        {
            _messages.AddRange(chunk);
            _recvOffset += (ushort)chunk.Length;
        }
        ParseMessages();
    }

    private void ParseMessages()
    {
        while (_messages.Count >= 4)
        {
            var len = _messages[0] | _messages[1] << 8;
            if (len < 4) { _messages.Clear(); _log("The controller board's message stream got corrupted."); return; }
            if (_messages.Count < len) return;
            var m = _messages.GetRange(0, len).ToArray();
            _messages.RemoveRange(0, len);
            var opcode = m[2];
            var id = m[3];
            switch (opcode)
            {
                case MsgRet or MsgRetU32 or MsgRetData or MsgRetU32Data:
                    if (_responses.ContainsKey(id)) _responses[id] = m;
                    break;
                case MsgCqFinished:
                    _pendingCommands.Remove(id);
                    break;
                case MsgConsoleDisconnect:
                    ConsoleProblem = "The Switch disconnected the controller (turned off, or re-pair it).";
                    break;
                case MsgUsbDisallowed:
                    ConsoleProblem = "Turn on System Settings > Controllers and Sensors > Pro Controller Wired Communication on the Switch.";
                    break;
            }
        }
    }

    private void Close(string why)
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            Monitor.PulseAll(_lock);
        }
        _log(why);
    }

    public void Dispose()
    {
        if (!_closed)
        {
            try { Command(Button.None, 20); } catch { } // let go of everything
            Thread.Sleep(50);
        }
        Close("Controller board closed.");
        try { _port.RtsEnable = false; _port.Close(); } catch { }
        _port.Dispose();
    }

    // ================================================================ CRC32C (polynomial 0x1EDC6F41, no final inversion)
    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0x82F63B78u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>Same as the device's _mm_crc32_u8 loop: CRC32C update with the seed as the starting value, nothing inverted.</summary>
    public static uint Crc32C(uint seed, ReadOnlySpan<byte> data)
    {
        var crc = seed;
        if (Sse42.IsSupported)
        {
            foreach (var b in data) crc = Sse42.Crc32(crc, b);
            return crc;
        }
        foreach (var b in data) crc = Table[(byte)crc ^ b] ^ (crc >> 8);
        return crc;
    }
}
