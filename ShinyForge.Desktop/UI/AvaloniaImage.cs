using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>JPEG / PNG → RGB pixels with Avalonia's decoder (macOS / Linux), for <see cref="ConsoleFtpLauncher.Decode"/>.</summary>
public static class AvaloniaImage
{
    public static RgbImage? Decode(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var bmp = new Bitmap(ms);
            var (w, h) = (bmp.PixelSize.Width, bmp.PixelSize.Height);
            var stride = w * 4;
            var buf = new byte[stride * h];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, stride); }
            finally { handle.Free(); }
            var rgba = bmp.Format == PixelFormats.Rgba8888;
            var rgb = new byte[w * h * 3];
            for (int i = 0, o = 0; i < buf.Length; i += 4, o += 3)
            {
                rgb[o] = rgba ? buf[i] : buf[i + 2];
                rgb[o + 1] = buf[i + 1];
                rgb[o + 2] = rgba ? buf[i + 2] : buf[i];
            }
            return new RgbImage(w, h, rgb);
        }
        catch { return null; }
    }
}
