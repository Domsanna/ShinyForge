using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>JPEG / PNG → RGB pixels with System.Drawing (Windows), for <see cref="ConsoleFtpLauncher.Decode"/>.</summary>
public static class GdiImage
{
    public static RgbImage? Decode(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var src = new Bitmap(ms);
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var bits = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[bits.Stride];
                var rgb = new byte[src.Width * src.Height * 3];
                for (var y = 0; y < src.Height; y++)
                {
                    Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, bits.Stride);
                    for (var x = 0; x < src.Width; x++)
                    {
                        var o = (y * src.Width + x) * 3;
                        rgb[o] = row[x * 3 + 2]; rgb[o + 1] = row[x * 3 + 1]; rgb[o + 2] = row[x * 3]; // BGR → RGB
                    }
                }
                return new RgbImage(src.Width, src.Height, rgb);
            }
            finally { src.UnlockBits(bits); }
        }
        catch { return null; }
    }
}
