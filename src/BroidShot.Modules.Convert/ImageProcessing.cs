using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BroidShot.Modules.Convert;

/// <summary>
/// A decoded image as premultiplied BGRA pixels (the working format for resizing and compositing) plus what the
/// encoders need to keep (DPI, metadata, colour profile). Thread-agnostic: no WPF dispatcher objects are kept alive.
/// </summary>
public sealed class RasterImage
{
    public RasterImage(int width, int height, byte[] pbgra, double dpiX, double dpiY)
    {
        Width = width;
        Height = height;
        Pixels = pbgra;
        DpiX = dpiX;
        DpiY = dpiY;
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Premultiplied BGRA, 4 bytes per pixel, row-major, stride = Width * 4.</summary>
    public byte[] Pixels { get; private set; }
    public double DpiX { get; set; }
    public double DpiY { get; set; }
    public BitmapMetadata? Metadata { get; set; }
    public IList<ColorContext>? ColorContexts { get; set; }
    public int ExifOrientation { get; set; } = 1;
    /// <summary>Number of frames in the source (animated GIF, multi-page TIFF); only the first is converted.</summary>
    public int FrameCount { get; set; } = 1;

    public bool IsOpaque()
    {
        byte[] p = Pixels;
        for (int i = 3; i < p.Length; i += 4) if (p[i] != 255) return false;
        return true;
    }

    internal void Replace(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Straight-alpha (Bgra32) or opaque (Bgr24) bitmap for encoders. Frozen.</summary>
    public BitmapSource ToBitmapSource(bool allowAlpha)
    {
        if (allowAlpha && !IsOpaque())
        {
            byte[] straight = new byte[Pixels.Length];
            Unpremultiply(Pixels, straight);
            return Freeze(BitmapSource.Create(Width, Height, DpiX, DpiY, PixelFormats.Bgra32, null, straight, Width * 4));
        }
        byte[] rgb = new byte[Width * Height * 3];
        for (int i = 0, o = 0; i < Pixels.Length; i += 4, o += 3)
        {
            rgb[o] = Pixels[i]; rgb[o + 1] = Pixels[i + 1]; rgb[o + 2] = Pixels[i + 2];
        }
        return Freeze(BitmapSource.Create(Width, Height, DpiX, DpiY, PixelFormats.Bgr24, null, rgb, Width * 3));
    }

    private static BitmapSource Freeze(BitmapSource source) { source.Freeze(); return source; }

    internal static void Unpremultiply(byte[] source, byte[] destination)
    {
        for (int i = 0; i < source.Length; i += 4)
        {
            byte a = source[i + 3];
            if (a == 255 || a == 0)
            {
                destination[i] = source[i]; destination[i + 1] = source[i + 1]; destination[i + 2] = source[i + 2]; destination[i + 3] = a;
                if (a == 0) { destination[i] = destination[i + 1] = destination[i + 2] = 0; }
            }
            else
            {
                destination[i] = (byte)Math.Min(255, (source[i] * 255 + a / 2) / a);
                destination[i + 1] = (byte)Math.Min(255, (source[i + 1] * 255 + a / 2) / a);
                destination[i + 2] = (byte)Math.Min(255, (source[i + 2] * 255 + a / 2) / a);
                destination[i + 3] = a;
            }
        }
    }
}

/// <summary>Decoding, orientation, resampling, flattening and encoding of raster images with WPF/WIC (no external libraries).</summary>
public static class ImageProcessing
{
    /// <summary>Upper bound to avoid exhausting memory on absurd inputs (pixels).</summary>
    public const long MaxPixels = 150_000_000;

    private const int OrientationTag = 274;

    /// <summary>Decodes the first frame (the largest one for .ico). Throws <see cref="ConvertException"/> with a Turkish message.</summary>
    public static RasterImage Load(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        BitmapFrame frame;
        int frameCount;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frameCount = extension == ".ico" ? 1 : decoder.Frames.Count;
            frame = decoder.Frames.Count == 0
                ? throw new ConvertException($"{extension} dosyasında görüntü bulunamadı.")
                : extension == ".ico" ? decoder.Frames.OrderByDescending(f => f.PixelWidth).First() : decoder.Frames[0];
            frame.Freeze();
        }
        catch (ConvertException) { throw; }
        catch (FileNotFoundException ex) { throw new ConvertException("Dosya bulunamadı.", ex); }
        catch (DirectoryNotFoundException ex) { throw new ConvertException("Dosya bulunamadı.", ex); }
        catch (UnauthorizedAccessException ex) { throw new ConvertException("Dosyaya erişim izni yok.", ex); }
        catch (IOException ex) { throw new ConvertException("Dosya okunamadı: " + ex.Message, ex); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException or ArgumentException or System.IO.FileFormatException)
        {
            throw new ConvertException(DescribeDecodeFailure(extension), ex);
        }

        if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
            throw new ConvertException($"Görsel çok büyük ({frame.PixelWidth}×{frame.PixelHeight} piksel).");

        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        byte[] pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        converted.CopyPixels(pixels, frame.PixelWidth * 4, 0);

        double dpiX = frame.DpiX > 0 ? frame.DpiX : 96, dpiY = frame.DpiY > 0 ? frame.DpiY : 96;
        var image = new RasterImage(frame.PixelWidth, frame.PixelHeight, pixels, dpiX, dpiY);
        try { image.Metadata = frame.Metadata as BitmapMetadata; } catch (NotSupportedException) { }
        try { image.ColorContexts = frame.ColorContexts; } catch (NotSupportedException) { }
        image.ExifOrientation = ReadOrientation(image.Metadata);
        image.FrameCount = frameCount;
        return image;
    }

    /// <summary>Cheap header read (size, DPI, EXIF orientation) without converting pixels. Null when the file cannot be decoded.</summary>
    public static (int Width, int Height, double DpiX, double DpiY, int Orientation)? Probe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            BitmapFrame frame = decoder.Frames[0];
            int orientation = 1;
            try { orientation = ReadOrientation(frame.Metadata as BitmapMetadata); } catch (NotSupportedException) { }
            return (frame.PixelWidth, frame.PixelHeight, frame.DpiX > 0 ? frame.DpiX : 96, frame.DpiY > 0 ? frame.DpiY : 96, orientation);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException or ArgumentException or IOException or UnauthorizedAccessException or System.IO.FileFormatException)
        {
            return null;
        }
    }

    internal static string DescribeDecodeFailure(string extension)
    {
        switch (extension)
        {
            case ".webp":
                return "WebP dosyası açılamadı. Windows'ta WebP codec'i kurulu olmayabilir: Microsoft Store'dan “WebP Görüntü Uzantıları” (WebP Image Extensions) paketini kur, ya da dosya bozuk olabilir.";
            case ".heic":
            case ".heif":
                return $"{extension} dosyası açılamadı. Microsoft Store'dan “HEIF Görüntü Uzantıları” paketini kurman gerekebilir, ya da dosya bozuk olabilir.";
            case ".jxr":
            case ".wdp":
                return $"{extension} dosyası açılamadı. Dosya bozuk olabilir ya da gerekli codec kurulu değil.";
            default:
                return $"{(extension.Length > 0 ? extension : "Bu")} dosyası görsel olarak açılamadı. Dosya bozuk ya da desteklenmeyen bir biçimde olabilir.";
        }
    }

    private static int ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata is null) return 1;
        foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "System.Photo.Orientation" })
        {
            try
            {
                if (metadata.GetQuery(query) is { } value)
                {
                    int orientation = System.Convert.ToInt32(value);
                    if (orientation is >= 1 and <= 8) return orientation;
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or FormatException or InvalidCastException or OverflowException or COMException) { }
        }
        return 1;
    }

    /// <summary>Bakes an EXIF orientation (1–8) into the pixels so the image looks right without the tag.</summary>
    public static void ApplyOrientation(RasterImage image)
    {
        (int w, int h, byte[] pixels) = ApplyOrientation(image.Pixels, image.Width, image.Height, image.ExifOrientation);
        if (!ReferenceEquals(pixels, image.Pixels))
        {
            if (w != image.Width) (image.DpiX, image.DpiY) = (image.DpiY, image.DpiX);
            image.Replace(w, h, pixels);
        }
        image.ExifOrientation = 1;
    }

    /// <summary>Pure pixel transform for EXIF orientation values 2–8 (premultiplied or straight BGRA alike).</summary>
    public static (int Width, int Height, byte[] Pixels) ApplyOrientation(byte[] source, int width, int height, int orientation)
    {
        if (orientation is < 2 or > 8) return (width, height, source);
        bool swap = orientation >= 5;
        int nw = swap ? height : width, nh = swap ? width : height;
        byte[] dest = new byte[source.Length];
        for (int sy = 0; sy < height; sy++)
        {
            for (int sx = 0; sx < width; sx++)
            {
                int dx, dy;
                switch (orientation)
                {
                    case 2: dx = width - 1 - sx; dy = sy; break;
                    case 3: dx = width - 1 - sx; dy = height - 1 - sy; break;
                    case 4: dx = sx; dy = height - 1 - sy; break;
                    case 5: dx = sy; dy = sx; break;
                    case 6: dx = height - 1 - sy; dy = sx; break;
                    case 7: dx = height - 1 - sy; dy = width - 1 - sx; break;
                    default: dx = sy; dy = width - 1 - sx; break; // 8
                }
                Buffer.BlockCopy(source, (sy * width + sx) * 4, dest, (dy * nw + dx) * 4, 4);
            }
        }
        return (nw, nh, dest);
    }

    /// <summary>High-quality resize (triangle filter widened when shrinking, i.e. area-averaging) of premultiplied BGRA.</summary>
    public static byte[] Resample(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        if (sourceWidth == width && sourceHeight == height) return source;
        byte[] horizontal = sourceWidth == width ? source : ResampleHorizontal(source, sourceWidth, sourceHeight, width);
        return sourceHeight == height ? horizontal : ResampleVertical(horizontal, width, sourceHeight, height);
    }

    public static void Resize(RasterImage image, int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if ((long)width * height > MaxPixels) throw new ConvertException($"Hedef boyut çok büyük ({width}×{height} piksel).");
        if (width == image.Width && height == image.Height) return;
        image.Replace(width, height, Resample(image.Pixels, image.Width, image.Height, width, height));
    }

    private readonly record struct Contribution(int Start, float[] Weights);

    private static Contribution[] BuildContributions(int sourceSize, int destSize)
    {
        double scale = sourceSize / (double)destSize;
        double filterScale = Math.Max(scale, 1.0);
        double support = filterScale; // triangle filter radius 1 * filterScale
        var result = new Contribution[destSize];
        for (int i = 0; i < destSize; i++)
        {
            double center = (i + 0.5) * scale;
            int min = Math.Max(0, (int)Math.Floor(center - support + 0.5));
            int max = Math.Min(sourceSize, (int)Math.Floor(center + support + 0.5));
            if (max <= min) { min = Math.Clamp((int)center, 0, sourceSize - 1); max = min + 1; }
            var weights = new float[max - min];
            double total = 0;
            for (int k = 0; k < weights.Length; k++)
            {
                double distance = Math.Abs((min + k - center + 0.5) / filterScale);
                double weight = Math.Max(0, 1 - distance);
                weights[k] = (float)weight;
                total += weight;
            }
            if (total <= 0) { weights[weights.Length / 2] = 1; total = 1; }
            for (int k = 0; k < weights.Length; k++) weights[k] = (float)(weights[k] / total);
            result[i] = new Contribution(min, weights);
        }
        return result;
    }

    private static byte[] ResampleHorizontal(byte[] source, int sw, int sh, int dw)
    {
        Contribution[] contributions = BuildContributions(sw, dw);
        byte[] dest = new byte[dw * sh * 4];
        Parallel.For(0, sh, y =>
        {
            int sourceRow = y * sw * 4, destRow = y * dw * 4;
            for (int x = 0; x < dw; x++)
            {
                Contribution c = contributions[x];
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < c.Weights.Length; k++)
                {
                    int p = sourceRow + (c.Start + k) * 4;
                    float w = c.Weights[k];
                    b += source[p] * w; g += source[p + 1] * w; r += source[p + 2] * w; a += source[p + 3] * w;
                }
                int o = destRow + x * 4;
                dest[o] = Clamp(b); dest[o + 1] = Clamp(g); dest[o + 2] = Clamp(r); dest[o + 3] = Clamp(a);
            }
        });
        return dest;
    }

    private static byte[] ResampleVertical(byte[] source, int width, int sh, int dh)
    {
        Contribution[] contributions = BuildContributions(sh, dh);
        byte[] dest = new byte[width * dh * 4];
        int stride = width * 4;
        Parallel.For(0, dh, y =>
        {
            Contribution c = contributions[y];
            int destRow = y * stride;
            for (int x = 0; x < stride; x += 4)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < c.Weights.Length; k++)
                {
                    int p = (c.Start + k) * stride + x;
                    float w = c.Weights[k];
                    b += source[p] * w; g += source[p + 1] * w; r += source[p + 2] * w; a += source[p + 3] * w;
                }
                dest[destRow + x] = Clamp(b); dest[destRow + x + 1] = Clamp(g); dest[destRow + x + 2] = Clamp(r); dest[destRow + x + 3] = Clamp(a);
            }
        });
        return dest;
    }

    private static byte Clamp(float value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(value + 0.5f);

    /// <summary>Composites transparent areas onto white (for formats without alpha such as JPEG and BMP).</summary>
    public static void FlattenOnWhite(RasterImage image)
    {
        byte[] p = image.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            byte a = p[i + 3];
            if (a == 255) continue;
            int inverse = 255 - a; // premultiplied: result = color + white * (1 - alpha)
            p[i] = (byte)Math.Min(255, p[i] + inverse);
            p[i + 1] = (byte)Math.Min(255, p[i + 1] + inverse);
            p[i + 2] = (byte)Math.Min(255, p[i + 2] + inverse);
            p[i + 3] = 255;
        }
    }

    /// <summary>Returns a copy of the pixels fitted into a size × size transparent square (aspect ratio kept, centred).</summary>
    public static byte[] RenderSquare(RasterImage image, int size)
    {
        (int fw, int fh) = ImageMath.FitWithin(image.Width, image.Height, size, size, allowUpscale: true);
        byte[] fitted = Resample(image.Pixels, image.Width, image.Height, fw, fh);
        if (fw == size && fh == size) return fitted;
        byte[] canvas = new byte[size * size * 4];
        int offsetX = (size - fw) / 2, offsetY = (size - fh) / 2;
        for (int y = 0; y < fh; y++)
            Buffer.BlockCopy(fitted, y * fw * 4, canvas, ((offsetY + y) * size + offsetX) * 4, fw * 4);
        return canvas;
    }

    // ---------- encoding ----------

    /// <summary>Encodes to the requested format. Returns the file bytes and warnings (e.g. metadata that could not be carried over).</summary>
    public static (byte[] Data, List<string> Warnings) Encode(RasterImage image, ImageTarget target, ConvertOptions options)
    {
        var warnings = new List<string>();
        if (target == ImageTarget.Ico)
            return (EncodeIcon(image, options.IconSizes), warnings);

        if (target is ImageTarget.Jpg or ImageTarget.Bmp) FlattenOnWhite(image);
        BitmapSource bitmap = image.ToBitmapSource(allowAlpha: target is not (ImageTarget.Jpg or ImageTarget.Bmp));

        bool keepMetadata = !options.StripMetadata && target is ImageTarget.Jpg or ImageTarget.Tiff or ImageTarget.Png;
        BitmapMetadata? metadata = keepMetadata ? PrepareMetadata(image.Metadata) : null;
        IList<ColorContext>? colorContexts = !options.StripMetadata && image.ColorContexts is { Count: > 0 } ? image.ColorContexts : null;

        byte[] data;
        try
        {
            data = EncodeWith(bitmap, target, options, metadata, colorContexts);
        }
        catch (Exception ex) when ((metadata is not null || colorContexts is not null) && ex is NotSupportedException or InvalidOperationException or ArgumentException or COMException or IOException)
        {
            data = EncodeWith(bitmap, target, options, null, null);
            warnings.Add("Üstveri / renk profili hedef biçime taşınamadı, bu bilgiler dosyada yok.");
        }

        if (target == ImageTarget.Png && options.PngCompression != PngCompression.Fast)
            data = PngRecompressor.Recompress(data, options.PngCompression == PngCompression.Smallest ? CompressionLevel.SmallestSize : CompressionLevel.Optimal);
        return (data, warnings);
    }

    private static BitmapMetadata? PrepareMetadata(BitmapMetadata? original)
    {
        if (original is null) return null;
        try
        {
            var clone = original.Clone();
            // The orientation was baked into the pixels, so the tag must not rotate the image a second time.
            foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try { if (clone.ContainsQuery(query)) clone.SetQuery(query, (ushort)1); }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or COMException) { }
            }
            return clone;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException) { return null; }
    }

    private static byte[] EncodeWith(BitmapSource bitmap, ImageTarget target, ConvertOptions options, BitmapMetadata? metadata, IList<ColorContext>? colorContexts)
    {
        BitmapEncoder encoder = target switch
        {
            ImageTarget.Png => new PngBitmapEncoder(),
            ImageTarget.Jpg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(options.JpgQuality, 1, 100) },
            ImageTarget.Bmp => new BmpBitmapEncoder(),
            ImageTarget.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            ImageTarget.Gif => new GifBitmapEncoder(),
            _ => throw new ConvertException("Desteklenmeyen hedef biçim.")
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, colorContexts is null ? null : new System.Collections.ObjectModel.ReadOnlyCollection<ColorContext>(colorContexts)));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    /// <summary>PNG-compressed frames inside an .ico container (supported by Windows Vista and later).</summary>
    public static byte[] EncodeIcon(RasterImage image, IReadOnlyList<int> requestedSizes)
    {
        int[] sizes = requestedSizes.Where(s => s is >= 8 and <= 256).Distinct().OrderBy(s => s).ToArray();
        if (sizes.Length == 0) throw new ConvertException("Simge için en az bir boyut seç (16–256).");

        var pngs = new List<byte[]>();
        foreach (int size in sizes)
        {
            byte[] square = RenderSquare(image, size);
            var holder = new RasterImage(size, size, square, 96, 96);
            BitmapSource source = holder.ToBitmapSource(allowAlpha: true);
            if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var memory = new MemoryStream();
            encoder.Save(memory);
            pngs.Add(memory.ToArray());
        }

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(pngs[i].Length); writer.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (byte[] png in pngs) writer.Write(png);
        writer.Flush();
        return output.ToArray();
    }

    /// <summary>Re-encodes a PDF page render (PNG bytes) as JPEG.</summary>
    public static byte[] PngToJpeg(byte[] png, int quality)
    {
        using var input = new MemoryStream(png);
        BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
        var raster = new RasterImage(converted.PixelWidth, converted.PixelHeight, new byte[converted.PixelWidth * converted.PixelHeight * 4],
            decoder.Frames[0].DpiX, decoder.Frames[0].DpiY);
        converted.CopyPixels(raster.Pixels, converted.PixelWidth * 4, 0);
        FlattenOnWhite(raster);
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(raster.ToBitmapSource(allowAlpha: false)));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}

/// <summary>Re-deflates the IDAT data of a PNG with a stronger zlib level (WPF's encoder has no compression setting).</summary>
public static class PngRecompressor
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    internal static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>Returns the recompressed PNG, or the input unchanged when it cannot be parsed or the result is not smaller.</summary>
    public static byte[] Recompress(byte[] png, CompressionLevel level)
    {
        try
        {
            if (png.Length < 12 || !png.AsSpan(0, 8).SequenceEqual(Signature)) return png;
            var chunks = new List<(string Type, byte[] Data)>();
            using var idat = new MemoryStream();
            int position = 8, firstIdat = -1;
            while (position + 12 <= png.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position, 4));
                if (length < 0 || position + 12 + length > png.Length) return png;
                string type = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
                var data = png.AsSpan(position + 8, length);
                if (type == "IDAT")
                {
                    if (firstIdat < 0) { firstIdat = chunks.Count; chunks.Add(("IDAT", Array.Empty<byte>())); }
                    idat.Write(data);
                }
                else chunks.Add((type, data.ToArray()));
                position += 12 + length;
                if (type == "IEND") break;
            }
            if (firstIdat < 0) return png;

            byte[] raw;
            idat.Position = 0;
            using (var inflate = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
            using (var rawStream = new MemoryStream())
            {
                inflate.CopyTo(rawStream);
                raw = rawStream.ToArray();
            }
            using var deflated = new MemoryStream();
            using (var zlib = new ZLibStream(deflated, level, leaveOpen: true)) zlib.Write(raw, 0, raw.Length);
            byte[] newIdat = deflated.ToArray();
            if (newIdat.Length >= idat.Length) return png;

            using var output = new MemoryStream(png.Length);
            output.Write(Signature);
            byte[] number4 = new byte[4];
            for (int i = 0; i < chunks.Count; i++)
            {
                (string type, byte[] data) = chunks[i];
                if (i == firstIdat) data = newIdat;
                byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
                Span<byte> number = number4;
                BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
                output.Write(number);
                output.Write(typeBytes);
                output.Write(data);
                BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typeBytes, data));
                output.Write(number);
            }
            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OverflowException)
        {
            return png;
        }
    }
}
