using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// A USB device driven through Windows' WinUSB driver (winusb.dll + SetupAPI, no extra packages): finds it by VID/PID among
/// the connected USB devices, opens its first bulk IN / OUT pipes and reads / writes them with a timeout. The device must use
/// the WinUSB driver (installed once with Zadig). Windows only - elsewhere <see cref="Open"/> returns null.
/// </summary>
internal sealed class WinUsbDevice : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly IntPtr _usb;
    private readonly byte _in, _out;

    private WinUsbDevice(SafeFileHandle file, IntPtr usb, byte pipeIn, byte pipeOut)
    {
        (_file, _usb, _in, _out) = (file, usb, pipeIn, pipeOut);
        TimeoutMs = 15000;
    }

    /// <summary>How long a read or write may take (WinUSB pipe timeout).</summary>
    public uint TimeoutMs
    {
        set
        {
            var t = value;
            WinUsb_SetPipePolicy(_usb, _in, PipeTransferTimeout, 4, ref t);
            WinUsb_SetPipePolicy(_usb, _out, PipeTransferTimeout, 4, ref t);
        }
    }

    /// <summary>Is a device with this VID/PID plugged in (any driver)?</summary>
    public static bool IsConnected(ushort vid, ushort pid) => OsInfo.IsWindows && DevicePaths(vid, pid).Any();

    /// <summary>Is any device from this vendor plugged in (any product, any driver)?</summary>
    public static bool IsVendorConnected(ushort vid) => OsInfo.IsWindows && DevicePaths(vid, null).Any();

    /// <summary>Opens the first matching device, or null when none is plugged in. Throws when it's there but can't be used (driver).</summary>
    public static WinUsbDevice? Open(ushort vid, ushort pid)
    {
        if (!OsInfo.IsWindows) return null;
        foreach (var path in DevicePaths(vid, pid))
        {
            var file = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
                                  FileAttributeNormal | FileFlagOverlapped, IntPtr.Zero);
            if (file.IsInvalid) continue;
            if (!WinUsb_Initialize(file, out var usb))
            {
                file.Dispose();
                throw new IOException("The Switch is connected by USB but Windows uses the wrong driver for it. Install the WinUSB " +
                                      "driver for \"Nintendo Switch\" (VID 057E, PID 3000) with Zadig, then try again.");
            }
            byte pin = 0, pout = 0;
            if (WinUsb_QueryInterfaceSettings(usb, 0, out var iface))
                for (byte i = 0; i < iface.bNumEndpoints; i++)
                    if (WinUsb_QueryPipe(usb, 0, i, out var pipe) && pipe.PipeType == UsbdPipeTypeBulk)
                    {
                        if ((pipe.PipeId & 0x80) != 0) { if (pin == 0) pin = pipe.PipeId; }
                        else if (pout == 0) pout = pipe.PipeId;
                    }
            if (pin == 0 || pout == 0) { WinUsb_Free(usb); file.Dispose(); continue; }
            return new WinUsbDevice(file, usb, pin, pout);
        }
        return null;
    }

    public void Write(byte[] data)
    {
        if (!WinUsb_WritePipe(_usb, _out, data, (uint)data.Length, out var sent, IntPtr.Zero) || sent != data.Length)
            throw new IOException("Writing to the Switch over USB failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes (as many transfers as it takes). Nothing arriving in time: <see cref="TimeoutException"/>.</summary>
    public byte[] ReadExactly(int count)
    {
        var result = new byte[count];
        var got = 0;
        var chunk = new byte[Math.Min(Math.Max(count, 64), 1 << 20)];
        while (got < count)
        {
            if (!WinUsb_ReadPipe(_usb, _in, chunk, (uint)chunk.Length, out var n, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ErrorSemTimeout) throw new TimeoutException("The Switch didn't answer over USB in time.");
                throw new IOException("Reading from the Switch over USB failed: " + new Win32Exception(err).Message);
            }
            if (n == 0) continue;
            var take = (int)Math.Min(n, (uint)(count - got));
            Array.Copy(chunk, 0, result, got, take);
            got += take;
        }
        return result;
    }

    public void Dispose()
    {
        WinUsb_Free(_usb);
        _file.Dispose();
    }

    // ------------------------------------------------------------------ finding the device
    private static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED"); // GUID_DEVINTERFACE_USB_DEVICE

    private static IEnumerable<string> DevicePaths(ushort vid, ushort? pid)
    {
        var tag = pid is { } p ? $"vid_{vid:x4}&pid_{p:x4}" : $"vid_{vid:x4}&";
        var list = new List<string>();
        var guid = UsbDeviceInterface;
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        try
        {
            var data = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var need, IntPtr.Zero);
                if (need == 0) continue;
                var detail = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, need, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (path.Contains(tag, StringComparison.OrdinalIgnoreCase)) list.Add(path);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    // ------------------------------------------------------------------ Windows API
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80, FileFlagOverlapped = 0x40000000;
    private const uint DigcfPresent = 2, DigcfDeviceInterface = 0x10;
    private const uint PipeTransferTimeout = 3;
    private const int ErrorSemTimeout = 121;
    private const int UsbdPipeTypeBulk = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsbInterfaceDescriptor
    {
        public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinUsbPipeInformation { public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr devInfo);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr usb);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr usb);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryInterfaceSettings(IntPtr usb, byte altSetting, out UsbInterfaceDescriptor descriptor);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryPipe(IntPtr usb, byte altSetting, byte pipeIndex, out WinUsbPipeInformation info);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipeId, uint policy, uint length, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(IntPtr usb, byte pipeId, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(IntPtr usb, byte pipeId, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
}
