using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using ShinyForge.Core;
using ShinyForge.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ShinyForge.UI;

/// <summary>
/// Windows' built-in text recognition (Windows.Media.Ocr, Windows 10+) for the HOME sorter, plus a PNG decoder that keeps
/// transparency (Pokémon Automation's sprite sheets). Set up in Program.cs as HomeVision.Ocr / HomeVision.DecodeRgba.
/// </summary>
public static class WinOcr
{
    private static readonly Dictionary<string, OcrEngine?> Engines = new();
    private static readonly object Gate = new();

    /// <summary>Languages Windows can read (installed OCR language packs), as BCP-47 tags.</summary>
    public static IReadOnlyList<string> Languages() => OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    private static OcrEngine? Engine(string lang)
    {
        lock (Gate)
        {
            if (Engines.TryGetValue(lang, out var e)) return e;
            e = null;
            try
            {
                var match = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l =>
                    l.LanguageTag.Equals(lang, StringComparison.OrdinalIgnoreCase) || l.LanguageTag.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase));
                e = match != null ? OcrEngine.TryCreateFromLanguage(match) : null;
                e ??= OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch (Exception ex) { AppPaths.Log("Windows OCR: " + ex.Message); }
            Engines[lang] = e;
            return e;
        }
    }

    /// <summary>
    /// Reads the text in a picture part, enlarged smoothly first (small screen text reads much better). Windows' OCR sometimes
    /// returns nothing for a single short word ("Jolly", "Damp"); then it tries again at other sizes with a margin.
    /// </summary>
    public static string? Read(RgbImage img, string lang)
    {
        string? last = null;
        foreach (var text in ReadAll(img, lang))
        {
            last = text;
            if (text.Trim().Length > 0) return text;
        }
        return last;
    }

    /// <summary>Every reading at the different sizes / margins, lazily (for text that has to match a pattern).</summary>
    public static IEnumerable<string> ReadAll(RgbImage img, string lang)
    {
        var engine = Engine(lang);
        if (engine == null) yield break;
        using var src = ToBitmap(img);
        foreach (var (k, pad) in new[] { (2, 0), (3, 20), (2, 20), (3, 0), (4, 10) })
        {
            var text = Recognize(engine, src, k, pad);
            if (text == null) yield break;
            yield return text;
        }
    }

    private static string? Recognize(OcrEngine engine, Bitmap src, int k, int pad)
    {
        try
        {
            using var big = new Bitmap(src.Width * k + 2 * pad, src.Height * k + 2 * pad, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(big))
            {
                g.Clear(src.GetPixel(src.Width - 1, 0));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, pad, pad, src.Width * k, src.Height * k);
            }
            var bits = big.LockBits(new Rectangle(0, 0, big.Width, big.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bgra = new byte[big.Width * big.Height * 4];
            for (var y = 0; y < big.Height; y++) Marshal.Copy(bits.Scan0 + y * bits.Stride, bgra, y * big.Width * 4, big.Width * 4);
            big.UnlockBits(bits);
            using var bmp = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, big.Width, big.Height, BitmapAlphaMode.Premultiplied);
            var result = engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();
            return string.Join("\n", result.Lines.Select(l => l.Text));
        }
        catch (Exception ex) { AppPaths.Log("Windows OCR: " + ex.Message); return null; }
    }

    private static Bitmap ToBitmap(RgbImage img)
    {
        var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new byte[img.Width * 4];
        for (var y = 0; y < img.Height; y++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                var s = (y * img.Width + x) * 3;
                row[x * 4] = img.Pixels[s + 2]; row[x * 4 + 1] = img.Pixels[s + 1]; row[x * 4 + 2] = img.Pixels[s]; row[x * 4 + 3] = 255;
            }
            Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
        }
        bmp.UnlockBits(bits);
        return bmp;
    }

    /// <summary>PNG → RGBA bytes, transparency kept.</summary>
    public static (int W, int H, byte[] Rgba)? DecodeRgba(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var src = new Bitmap(ms);
            var bits = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bits.Stride];
                var rgba = new byte[src.Width * src.Height * 4];
                for (var y = 0; y < src.Height; y++)
                {
                    Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, bits.Stride);
                    for (var x = 0; x < src.Width; x++)
                    {
                        var o = (y * src.Width + x) * 4;
                        rgba[o] = row[x * 4 + 2]; rgba[o + 1] = row[x * 4 + 1]; rgba[o + 2] = row[x * 4]; rgba[o + 3] = row[x * 4 + 3];
                    }
                }
                return (src.Width, src.Height, rgba);
            }
            finally { src.UnlockBits(bits); }
        }
        catch { return null; }
    }
}
