using System.Net.Sockets;

namespace ShinyForge.Services.ThreeDS;

[Flags]
public enum N3DSButton : uint
{
    None = 0,
    A = 1 << 0, B = 1 << 1, Select = 1 << 2, Start = 1 << 3,
    Right = 1 << 4, Left = 1 << 5, Up = 1 << 6, Down = 1 << 7,
    R = 1 << 8, L = 1 << 9, X = 1 << 10, Y = 1 << 11,
}

/// <summary>
/// Sends controller input to a 3DS running Luma3DS with Rosalina's InputRedirection started.
/// Protocol (UDP 4950, 20 bytes, little endian): HID buttons (active-low, neutral 0xFFF), touch (0x02000000 = none),
/// circle pad ((y &lt;&lt; 12) | x, neutral 0x7FF/0x7FF), New 3DS extras (0x80800081 neutral), special buttons (bit 0 HOME).
/// Based on the protocol as implemented by SirSnorealot/3DSShinyHunter (GPL-2.0) and Luma3DS.
/// The 3DS keeps the last state it received, so the state is re-sent several times for each change.
/// </summary>
public sealed class InputRedirection : IDisposable
{
    public const int Port = 4950;
    private const uint NeutralHid = 0xFFF, NoTouch = 0x02000000, NeutralPad = 0x007FF7FF, NeutralN3DS = 0x80800081;
    private readonly UdpClient _udp = new();
    private readonly string _host;
    private N3DSButton _held;
    private uint _pad = NeutralPad;

    public InputRedirection(string host)
    {
        _host = host;
        _udp.Connect(host, Port);
    }

    public string Host => _host;

    private byte[] Packet()
    {
        var p = new byte[20];
        BitConverter.GetBytes(NeutralHid & ~(uint)_held).CopyTo(p, 0);
        BitConverter.GetBytes(NoTouch).CopyTo(p, 4);
        BitConverter.GetBytes(_pad).CopyTo(p, 8);
        BitConverter.GetBytes(NeutralN3DS).CopyTo(p, 12);
        // bytes 16-19: special buttons (HOME etc.) - always released
        return p;
    }

    /// <summary>Send the current state <paramref name="times"/> times, 10 ms apart (UDP has no acknowledgement).</summary>
    public void Flush(int times = 3)
    {
        var p = Packet();
        for (int i = 0; i < times; i++)
        {
            _udp.Send(p, p.Length);
            Thread.Sleep(10);
        }
    }

    /// <summary>Keep transmitting the current state for <paramref name="ms"/> milliseconds.</summary>
    public void HoldFor(int ms)
    {
        var p = Packet();
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            _udp.Send(p, p.Length);
            Thread.Sleep(10);
        }
    }

    public void Press(N3DSButton buttons, int holdMs = 150, int afterMs = 150)
    {
        _held |= buttons;
        HoldFor(holdMs);
        _held &= ~buttons;
        Flush(5);
        HoldFor(afterMs);
    }

    /// <summary>Tilts the circle pad (dx/dy in -1..1) for <paramref name="ms"/> milliseconds, then centres it.</summary>
    public void Tilt(int dx, int dy, int ms)
    {
        int x = dx > 0 ? 0xFFF : dx < 0 ? 0x000 : 0x7FF;
        int y = dy > 0 ? 0xFFF : dy < 0 ? 0x000 : 0x7FF;
        _pad = (uint)((y << 12) | x);
        HoldFor(ms);
        _pad = NeutralPad;
        Flush(5);
        HoldFor(100);
    }

    public void ReleaseAll()
    {
        _held = N3DSButton.None;
        _pad = NeutralPad;
        Flush(5);
    }

    public void Dispose()
    {
        try { ReleaseAll(); } catch { }
        _udp.Dispose();
    }
}
