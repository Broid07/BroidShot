using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace BroidShot.Modules.Utilities;

public enum QrErrorLevel { Low, Medium, Quartile, High }

/// <summary>Raw top-down BGRA32 pixels.</summary>
internal sealed record RawImage(int Width, int Height, byte[] Bgra);

/// <summary>One decoded QR code: its text, pattern centre points (pixels) and estimated module size (pixels, 0 = unknown).</summary>
internal sealed record QrHit(string Text, IReadOnlyList<(double X, double Y)> Points, double ModuleSize);

/// <summary>QR creation and reading on raw pixel buffers (ZXing.Net core, no System.Drawing).</summary>
internal static class QrService
{
    public const int MinSize = 64;
    public const int MaxSize = 4096;
    public const int MaxTextLength = 2900;

    public static string ErrorLevelName(QrErrorLevel level) => level switch
    {
        QrErrorLevel.Low => "Düşük (L, %7)",
        QrErrorLevel.Medium => "Orta (M, %15)",
        QrErrorLevel.Quartile => "Yüksek (Q, %25)",
        _ => "En yüksek (H, %30)"
    };

    /// <summary>Renders the text as a black-on-white QR code of at least <paramref name="size"/> pixels (whole-pixel modules, 4-module quiet zone).</summary>
    public static RawImage Generate(string text, int size, QrErrorLevel level)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("QR için metin boş olamaz.", nameof(text));
        size = Math.Clamp(size, MinSize, MaxSize);
        ErrorCorrectionLevel ec = level switch
        {
            QrErrorLevel.Low => ErrorCorrectionLevel.L,
            QrErrorLevel.Medium => ErrorCorrectionLevel.M,
            QrErrorLevel.Quartile => ErrorCorrectionLevel.Q,
            _ => ErrorCorrectionLevel.H
        };
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.ERROR_CORRECTION] = ec,
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
            [EncodeHintType.MARGIN] = 4,
        };
        BitMatrix matrix;
        try { matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size, hints); }
        catch (WriterException ex) { throw new InvalidOperationException("Bu metin QR koduna sığmıyor. Metni kısalt veya hata düzeltme düzeyini düşür.", ex); }

        int width = matrix.Width, height = matrix.Height;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = matrix[x, y] ? (byte)0 : (byte)255;
                int i = (y * width + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        }
        return new RawImage(width, height, pixels);
    }

    private static BarcodeReaderGeneric CreateReader()
    {
        var options = new DecodingOptions
        {
            TryHarder = true,
            TryInverted = true,
            PureBarcode = false,
            PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
            CharacterSet = "UTF-8",
        };
        return new BarcodeReaderGeneric { AutoRotate = true, Options = options };
    }

    private static byte[] HalfSize(byte[] bgra, int width, int height, out int w, out int h)
    {
        w = width / 2; h = height / 2;
        byte[] small = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Buffer.BlockCopy(bgra, ((y * 2) * width + x * 2) * 4, small, (y * w + x) * 4, 4);
        return small;
    }

    /// <summary>Reads one QR code from raw BGRA pixels. Returns null when nothing is found.</summary>
    public static string? Decode(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;
        var reader = CreateReader();
        // Passing the buffer by reference: the luminance source copies what it needs.
        Result? result = reader.Decode(new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32));
        if (result is not null) return result.Text;

        // Large screenshots: a second attempt on a half-size copy often finds codes the first pass missed.
        if (width >= 800 && height >= 800)
        {
            byte[] small = HalfSize(bgra, width, height, out int w, out int h);
            result = reader.Decode(new RGBLuminanceSource(small, w, h, RGBLuminanceSource.BitmapFormat.BGRA32));
        }
        return result?.Text;
    }

    /// <summary>
    /// Reads every QR code in raw BGRA pixels (several codes on one screenshot). Points are the finder/alignment
    /// pattern centres in the buffer's pixel coordinates; <see cref="QrHit.ModuleSize"/> is the estimated module size
    /// (0 when unknown). Codes with identical text are reported once. Empty when nothing is found.
    /// </summary>
    public static IReadOnlyList<QrHit> DecodeAll(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return Array.Empty<QrHit>();
        var reader = CreateReader();
        Result[]? results = reader.DecodeMultiple(new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32));
        double scale = 1;
        if ((results is null || results.Length == 0) && width >= 800 && height >= 800)
        {
            byte[] small = HalfSize(bgra, width, height, out int w, out int h);
            results = reader.DecodeMultiple(new RGBLuminanceSource(small, w, h, RGBLuminanceSource.BitmapFormat.BGRA32));
            scale = 2;
        }
        if (results is null || results.Length == 0) return Array.Empty<QrHit>();
        var hits = new List<QrHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Result result in results)
        {
            if (result?.Text is not string text || !seen.Add(text)) continue;
            var points = new List<(double X, double Y)>();
            double module = 0;
            foreach (ResultPoint? point in result.ResultPoints ?? Array.Empty<ResultPoint>())
            {
                if (point is null) continue;
                points.Add((point.X * scale, point.Y * scale));
                if (point is FinderPattern finder) module = Math.Max(module, finder.EstimatedModuleSize * scale);
            }
            hits.Add(new QrHit(text, points, module));
        }
        return hits;
    }

    public static string? Decode(RawImage image) => Decode(image.Bgra, image.Width, image.Height);

    public static string? Decode(BitmapSource source)
    {
        RawImage raw = ToRaw(source);
        return Decode(raw);
    }

    public static RawImage ToRaw(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        byte[] pixels = new byte[checked(width * height * 4)];
        converted.CopyPixels(pixels, width * 4, 0);
        // Transparent pixels would read as black; flatten onto white.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a == 255) continue;
            pixels[i] = (byte)((pixels[i] * a + 255 * (255 - a)) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * a + 255 * (255 - a)) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * a + 255 * (255 - a)) / 255);
            pixels[i + 3] = 255;
        }
        return new RawImage(width, height, pixels);
    }

    public static BitmapSource ToBitmap(RawImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static void SavePng(RawImage image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmap(image)));
        string temp = path + ".tmp";
        using (var stream = File.Create(temp)) encoder.Save(stream);
        File.Move(temp, path, true);
    }

    /// <summary>Loads an image file (PNG, JPG, BMP, GIF, TIFF, ...) as a frozen bitmap. Does not keep the file locked.</summary>
    public static BitmapSource LoadImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
