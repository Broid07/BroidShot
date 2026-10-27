using System;

namespace BroidShot.Modules.Convert;

/// <summary>Pure size arithmetic (resize, fitting, page sizes). No imaging dependencies.</summary>
public static class ImageMath
{
    public const double A4WidthPoints = 595.276;
    public const double A4HeightPoints = 841.89;

    /// <summary>Resized dimensions for a percentage; each side is at least 1.</summary>
    public static (int Width, int Height) ScalePercent(int width, int height, double percent)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "Görsel boyutu geçersiz.");
        if (double.IsNaN(percent) || percent <= 0) throw new ArgumentOutOfRangeException(nameof(percent), "Yüzde sıfırdan büyük olmalı.");
        return (Math.Max(1, (int)Math.Round(width * percent / 100.0, MidpointRounding.AwayFromZero)),
                Math.Max(1, (int)Math.Round(height * percent / 100.0, MidpointRounding.AwayFromZero)));
    }

    /// <summary>
    /// Fits (width, height) inside maxWidth × maxHeight keeping the aspect ratio. A limit of 0 (or less) means
    /// "no limit". Images are only shrunk unless <paramref name="allowUpscale"/> is true.
    /// </summary>
    public static (int Width, int Height) FitWithin(int width, int height, int maxWidth, int maxHeight, bool allowUpscale = false)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "Görsel boyutu geçersiz.");
        double scale = double.PositiveInfinity;
        if (maxWidth > 0) scale = Math.Min(scale, maxWidth / (double)width);
        if (maxHeight > 0) scale = Math.Min(scale, maxHeight / (double)height);
        if (double.IsInfinity(scale)) return (width, height);
        if (!allowUpscale && scale >= 1) return (width, height);

        int w = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero));
        int h = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero));
        // Rounding must never exceed a limit.
        if (maxWidth > 0) w = Math.Min(w, maxWidth);
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return (w, h);
    }

    /// <summary>Output size for the user's resize choice.</summary>
    public static (int Width, int Height) Resolve(int width, int height, ConvertOptions options) => options.ResizeMode switch
    {
        ResizeKind.Percent => ScalePercent(width, height, options.ResizePercent),
        ResizeKind.MaxSize => FitWithin(width, height, options.MaxWidth, options.MaxHeight),
        _ => (width, height)
    };

    /// <summary>Pixel size of a PDF page (given in 1/96 inch units by Windows.Data.Pdf) at a DPI.</summary>
    public static (int Width, int Height) PagePixels(double widthDip, double heightDip, int dpi) =>
        (Math.Max(1, (int)Math.Round(widthDip * dpi / 96.0, MidpointRounding.AwayFromZero)),
         Math.Max(1, (int)Math.Round(heightDip * dpi / 96.0, MidpointRounding.AwayFromZero)));

    /// <summary>
    /// Page size in points for an image placed on an A4 page: portrait or landscape by image shape, then the
    /// image is scaled to fit inside the page and centred. Returns the page size and the image rectangle.
    /// </summary>
    public static (double PageWidth, double PageHeight, double X, double Y, double Width, double Height) FitToA4(int pixelWidth, int pixelHeight)
    {
        bool landscape = pixelWidth > pixelHeight;
        double pageW = landscape ? A4HeightPoints : A4WidthPoints;
        double pageH = landscape ? A4WidthPoints : A4HeightPoints;
        double scale = Math.Min(pageW / pixelWidth, pageH / pixelHeight);
        double w = pixelWidth * scale, h = pixelHeight * scale;
        return (pageW, pageH, (pageW - w) / 2, (pageH - h) / 2, w, h);
    }

    /// <summary>Page size in points when the image keeps its original size (pixels at its own DPI, 96 when unknown).</summary>
    public static (double Width, double Height) OriginalPageSize(int pixelWidth, int pixelHeight, double dpiX, double dpiY)
    {
        if (double.IsNaN(dpiX) || dpiX < 10 || dpiX > 4800) dpiX = 96;
        if (double.IsNaN(dpiY) || dpiY < 10 || dpiY > 4800) dpiY = 96;
        double w = pixelWidth * 72.0 / dpiX, h = pixelHeight * 72.0 / dpiY;
        const double max = 14400; // PDF viewers support pages up to 200 inches
        double shrink = Math.Min(1, max / Math.Max(w, h));
        return (Math.Max(1, w * shrink), Math.Max(1, h * shrink));
    }
}
