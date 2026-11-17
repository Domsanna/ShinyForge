using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// "Is the hardware for this feature plugged in?" - checked before ShinyForge opens an outside program, so the user is told
/// what's missing instead of getting a program that shows nothing. Detection is by USB vendor / product (Windows) and by the
/// video sources FFmpeg sees; it can't know every board, so callers let the user open the program anyway.
/// Elsewhere (macOS / Linux) USB isn't inspected: null = "can't tell".
/// </summary>
public static class Equipment
{
    /// <summary>A 3DS / DS capture board (3dscapture.com boards use FTDI chips: FT601 0403:601F, FT232H 0403:6014).</summary>
    public static bool? ThreeDSCaptureBoard() =>
        OsInfo.IsWindows ? WinUsbDevice.IsConnected(0x0403, 0x601F) || WinUsbDevice.IsConnected(0x0403, 0x6014) : null;

    /// <summary>
    /// A microcontroller Pokémon Automation can use as a controller: ESP32-S3 (303A), Raspberry Pi Pico W / RP2040 (2E8A),
    /// or an ESP32 behind the usual USB-serial chips (CP210x 10C4:EA60, CH340 1A86:7523).
    /// </summary>
    public static bool? Microcontroller() =>
        OsInfo.IsWindows
            ? WinUsbDevice.IsVendorConnected(0x303A) || WinUsbDevice.IsVendorConnected(0x2E8A)
              || WinUsbDevice.IsConnected(0x10C4, 0xEA60) || WinUsbDevice.IsConnected(0x1A86, 0x7523)
            : null;

    /// <summary>Video capture devices (capture cards, cameras) as FFmpeg lists them.</summary>
    public static List<string> VideoSources()
    {
        try { return RecordingService.ListDevices().Video.ToList(); }
        catch { return new(); }
    }
}
