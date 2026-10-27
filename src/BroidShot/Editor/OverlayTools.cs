using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BroidShot.Capture;
using BroidShot.Services;
using U = BroidShot.Modules.Utilities;

namespace BroidShot.Editor;

/// <summary>Tool modes of the capture overlay. <see cref="Capture"/> is the classic select/annotate behaviour.</summary>
public enum OverlayMode { Capture, ColorPicker, Ruler, Qr, Ocr }

/// <summary>Single-key shortcuts of the overlay tool modes. They never overlap the annotation keys (V A R E P T N H B C).</summary>
public static class OverlayShortcuts
{
    public const Key ColorPicker = Key.I;
    public const Key Ruler = Key.M;
    public const Key Qr = Key.Q;
    public const Key Ocr = Key.O;

    /// <summary>The mode a key switches to, or null. Keys with Ctrl/Alt/Win never switch modes.</summary>
    public static OverlayMode? ModeForKey(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return null;
        return key switch
        {
            ColorPicker => OverlayMode.ColorPicker,
            Ruler => OverlayMode.Ruler,
            Qr => OverlayMode.Qr,
            Ocr => OverlayMode.Ocr,
            _ => null
        };
    }

    /// <summary>Pressing the key of the active mode returns to the capture mode (toggle).</summary>
    public static OverlayMode Toggle(OverlayMode current, OverlayMode requested) => current == requested ? OverlayMode.Capture : requested;
}

/// <summary>An opaque colour read from the frozen screenshot.</summary>
public readonly record struct PixelColor(byte R, byte G, byte B)
{
    private U.Rgb Rgb => new(R, G, B);
    /// <summary>"#RRGGBB".</summary>
    public string Hex => U.ColorFormats.ToHex(Rgb);
    /// <summary>"rgb(r, g, b)".</summary>
    public string RgbText => U.ColorFormats.ToRgbText(Rgb);
    /// <summary>"hsl(h, s%, l%)".</summary>
    public string HslText => U.ColorFormats.ToHslText(Rgb);
    public Color ToColor() => Color.FromRgb(R, G, B);

    /// <summary>Adds the colour to the Renk seçici module's history. False when that module is not running.</summary>
    public bool AddToColorHistory()
    {
        try { return U.ColorPickerModule.RecordExternalPick(Rgb); }
        catch { return false; }
    }
}

/// <summary>
/// Pixel access to the overlay's frozen screenshot. All reads use the captured bitmap (exact physical pixels),
/// never the live screen. Snapshot-local pixel (0,0) is the top-left of <see cref="DesktopBounds"/>, which can be
/// negative on virtual screens with monitors left of / above the primary one.
/// </summary>
public sealed class FrozenFrame
{
    private byte[]? _bgra;

    public FrozenFrame(BitmapSource image, PixelRect desktopBounds)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        DesktopBounds = desktopBounds;
    }

    public BitmapSource Image { get; }
    public PixelRect DesktopBounds { get; }
    public int Width => Image.PixelWidth;
    public int Height => Image.PixelHeight;

    /// <summary>Top-down straight-alpha BGRA32 copy of the whole frame, created on first use.</summary>
    public byte[] Bgra => _bgra ??= Load();

    private byte[] Load()
    {
        BitmapSource source = Image.Format == PixelFormats.Bgra32 ? Image : new FormatConvertedBitmap(Image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(Width * Height * 4)];
        source.CopyPixels(pixels, Width * 4, 0);
        return pixels;
    }

    /// <summary>
    /// Maps a point on the displayed surface (device-independent units) to the snapshot pixel under it.
    /// The surface shows the whole bitmap stretched to its size, so the mapping is proportional and correct at any
    /// DPI (e.g. a 3000 px wide bitmap shown 2000 DIP wide at 150 %). The result is clamped to the bitmap.
    /// </summary>
    public static (int X, int Y) SurfaceToPixel(Point surfacePoint, Size surfaceSize, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return (0, 0);
        double x = surfacePoint.X * pixelWidth / Math.Max(1e-9, surfaceSize.Width);
        double y = surfacePoint.Y * pixelHeight / Math.Max(1e-9, surfaceSize.Height);
        return (Math.Clamp((int)Math.Floor(x), 0, pixelWidth - 1), Math.Clamp((int)Math.Floor(y), 0, pixelHeight - 1));
    }

    /// <summary>Centre of a snapshot pixel on the displayed surface (DIP).</summary>
    public static Point PixelToSurface(double x, double y, Size surfaceSize, int pixelWidth, int pixelHeight) =>
        new(x * surfaceSize.Width / Math.Max(1, pixelWidth), y * surfaceSize.Height / Math.Max(1, pixelHeight));

    /// <summary>Physical desktop coordinate of a snapshot pixel (adds the virtual-screen origin).</summary>
    public (int X, int Y) ToDesktop(int x, int y) => (x + DesktopBounds.X, y + DesktopBounds.Y);

    /// <summary>The colour of snapshot pixel (x, y); null outside the frame.</summary>
    public PixelColor? GetPixel(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return null;
        byte[] data = Bgra;
        int i = (y * Width + x) * 4;
        return new PixelColor(data[i + 2], data[i + 1], data[i]);
    }

    /// <summary>The colour at a physical desktop coordinate; null outside the frame.</summary>
    public PixelColor? GetDesktopPixel(int desktopX, int desktopY) => GetPixel(desktopX - DesktopBounds.X, desktopY - DesktopBounds.Y);

    /// <summary>
    /// Pixels of <paramref name="selection"/> (snapshot pixels, enclosing whole pixels, clamped) as a frozen bitmap for
    /// recognition. Redaction boxes drawn by the user are applied so hidden content is never read back.
    /// </summary>
    public BitmapSource ReadableCrop(Rect selection, IEnumerable<Annotation>? annotations = null)
    {
        Rect area = AnnotationRenderer.PixelSelection(selection, Width, Height);
        if (area.IsEmpty || area.Width < 1 || area.Height < 1) throw new InvalidOperationException("Önce okunacak alanı seç.");
        var redactions = (annotations ?? Array.Empty<Annotation>()).Where(a => a.Tool == AnnotationTool.Redact && a.Bounds.IntersectsWith(area)).ToList();
        if (redactions.Count > 0)
        {
            var document = new AnnotationDocument { Selection = area };
            document.Items.AddRange(redactions.Select(a => a.Clone()));
            return AnnotationRenderer.Export(Image, document);
        }
        byte[] pixels = CopyRegion(area);
        var bitmap = BitmapSource.Create((int)area.Width, (int)area.Height, 96, 96, PixelFormats.Bgra32, null, pixels, (int)area.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>True when a redaction box overlaps the (pixel-aligned) area.</summary>
    public static bool HasRedaction(Rect area, IEnumerable<Annotation>? annotations) =>
        (annotations ?? Array.Empty<Annotation>()).Any(a => a.Tool == AnnotationTool.Redact && a.Bounds.IntersectsWith(area));

    /// <summary>Top-down BGRA32 copy of a pixel-aligned area inside the frame.</summary>
    internal byte[] CopyRegion(Rect area)
    {
        int x = (int)area.X, y = (int)area.Y, w = (int)area.Width, h = (int)area.Height;
        int stride = w * 4;
        var pixels = new byte[checked(stride * h)];
        byte[] data = Bgra;
        for (int row = 0; row < h; row++) Buffer.BlockCopy(data, ((y + row) * Width + x) * 4, pixels, row * stride, stride);
        return pixels;
    }
}

/// <summary>A ruler measurement between two snapshot pixels (physical pixels).</summary>
public readonly record struct RulerReading(int StartX, int StartY, int EndX, int EndY, bool Snapped)
{
    public int Dx => EndX - StartX;
    public int Dy => EndY - StartY;
    public double Distance => Math.Sqrt((double)Dx * Dx + (double)Dy * Dy);

    /// <summary>Angle in degrees, [0, 360): 0 = right, 90 = up (screen y grows downwards, so it is inverted).</summary>
    public double AngleDegrees
    {
        get
        {
            if (Dx == 0 && Dy == 0) return 0;
            double angle = Math.Atan2(-Dy, Dx) * 180 / Math.PI;
            return angle < 0 ? angle + 360 : angle;
        }
    }

    /// <summary>"245.97 px · Δx 200 · Δy -143 · 35.6°" — invariant numbers.</summary>
    public string Label => string.Create(CultureInfo.InvariantCulture, $"{Distance:0.##} px   Δx {Dx}   Δy {Dy}   {AngleDegrees:0.#}°");

    /// <summary>Clipboard text.</summary>
    public string ClipboardText => string.Create(CultureInfo.InvariantCulture, $"{Distance:0.##} px (Δx {Dx}, Δy {Dy}, açı {AngleDegrees:0.#}°)");
}

public static class OverlayRuler
{
    /// <summary>Measures between two pixels; with <paramref name="snap"/> (Shift) the end is locked horizontal or vertical (longer axis wins).</summary>
    public static RulerReading Measure(int startX, int startY, int endX, int endY, bool snap)
    {
        (int x, int y) = U.RulerMath.Snap(startX, startY, endX, endY, snap);
        return new RulerReading(startX, startY, x, y, snap);
    }

    /// <summary>"320 × 200 px" for a region selection (whole snapshot pixels).</summary>
    public static string SelectionSize(Rect selection)
    {
        if (selection.IsEmpty) return "0 × 0 px";
        return string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(selection.Width)} × {(int)Math.Round(selection.Height)} px");
    }
}

/// <summary>A QR code found on the frozen screenshot. <see cref="Bounds"/> is in snapshot pixels.</summary>
public sealed record QrFinding(string Text, Rect Bounds, Uri? Link);

public static class OverlayQr
{
    /// <summary>
    /// Decodes every QR code inside <paramref name="region"/> of the frozen frame (whole frame when null/empty).
    /// Redaction boxes are respected.
    /// </summary>
    public static IReadOnlyList<QrFinding> Decode(FrozenFrame frame, Rect? region, IEnumerable<Annotation>? annotations = null) =>
        Decode(Prepare(frame, region, annotations));

    /// <summary>Pixels ready for decoding (UI thread: may render redactions); <see cref="Decode(Input)"/> can then run on any thread.</summary>
    public sealed class Input
    {
        internal Input(Rect area, byte[] bgra, int width, int height) { Area = area; Bgra = bgra; Width = width; Height = height; }
        /// <summary>The decoded area in snapshot pixels.</summary>
        public Rect Area { get; }
        internal byte[] Bgra { get; }
        internal int Width { get; }
        internal int Height { get; }
    }

    public static Input Prepare(FrozenFrame frame, Rect? region, IEnumerable<Annotation>? annotations = null)
    {
        Rect area = region is Rect r && !r.IsEmpty && r.Width >= 1 && r.Height >= 1
            ? AnnotationRenderer.PixelSelection(r, frame.Width, frame.Height)
            : new Rect(0, 0, frame.Width, frame.Height);
        if (FrozenFrame.HasRedaction(area, annotations))
        {
            U.RawImage raw = U.QrService.ToRaw(frame.ReadableCrop(area, annotations));
            return new Input(area, raw.Bgra, raw.Width, raw.Height);
        }
        // One copy straight from the frozen frame (large virtual screens); transparent pixels flattened onto white like QrService.ToRaw.
        byte[] pixels = frame.CopyRegion(area);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte alpha = pixels[i + 3];
            if (alpha == 255) continue;
            for (int c = 0; c < 3; c++) pixels[i + c] = (byte)((pixels[i + c] * alpha + 255 * (255 - alpha)) / 255);
            pixels[i + 3] = 255;
        }
        return new Input(area, pixels, (int)area.Width, (int)area.Height);
    }

    public static IReadOnlyList<QrFinding> Decode(Input input)
    {
        Rect area = input.Area;
        var findings = new List<QrFinding>();
        foreach (U.QrHit hit in U.QrService.DecodeAll(input.Bgra, input.Width, input.Height))
        {
            Rect bounds = Rect.Empty;
            foreach (var p in hit.Points) bounds.Union(new Point(p.X + area.X, p.Y + area.Y));
            if (!bounds.IsEmpty)
            {
                // Pattern centres sit 3.5 modules inside the symbol edge.
                double inflate = hit.ModuleSize > 0 ? hit.ModuleSize * 3.5 + 2 : Math.Max(8, Math.Max(bounds.Width, bounds.Height) * .2);
                bounds.Inflate(inflate, inflate);
                bounds.Intersect(area);
            }
            findings.Add(new QrFinding(hit.Text, bounds, TryGetWebLink(hit.Text)));
        }
        return findings;
    }

    /// <summary>An absolute http/https link, or null. Other schemes (file:, javascript:, ms-settings: …) are never offered as links.</summary>
    public static Uri? TryGetWebLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (trimmed.Any(char.IsWhiteSpace)) return null;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(uri.Host)
            ? uri : null;
    }
}

/// <summary>Text recognition used by the overlay; replaceable in tests.</summary>
public interface IOverlayTextRecognizer
{
    bool IsAvailable { get; }
    /// <summary>For status text, e.g. "Türkçe + İngilizce".</summary>
    string LanguageLabel { get; }
    Task<string> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken);
}

/// <summary>The app's offline OCR (<see cref="OcrService"/>: Turkish + English, falls back to the installed model).</summary>
public sealed class OcrServiceRecognizer : IOverlayTextRecognizer
{
    private readonly OcrService _service;
    public OcrServiceRecognizer(OcrService? service = null) { _service = service ?? new OcrService(); }
    public bool IsAvailable => _service.IsAvailable;
    public string LanguageLabel => _service.LanguageLabel;
    public Task<string> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken) => _service.RecognizeAsync(image, cancellationToken);
}

public static class OverlayOcr
{
    /// <summary>Small screen crops are enlarged before recognition; screen text is often below the size OCR models expect.</summary>
    public static int UpscaleFactor(int width, int height) => width > 0 && height > 0 && (long)width * height <= 1600L * 1200 ? 2 : 1;

    /// <summary>The bitmap handed to the recogniser for a crop (upscaled when small).</summary>
    public static BitmapSource Prepare(BitmapSource crop)
    {
        int factor = UpscaleFactor(crop.PixelWidth, crop.PixelHeight);
        if (factor == 1) return crop;
        int w = crop.PixelWidth * factor, h = crop.PixelHeight * factor;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            context.DrawImage(crop, new Rect(0, 0, w, h));
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>Recognises the text of a region of the frozen frame. Result is trimmed with Windows line endings.</summary>
    public static async Task<string> RecognizeAsync(IOverlayTextRecognizer recognizer, FrozenFrame frame, Rect selection, IEnumerable<Annotation>? annotations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recognizer);
        if (!recognizer.IsAvailable) throw new InvalidOperationException("Metin tanıma kullanılamıyor: OCR dil dosyaları (tessdata) bulunamadı.");
        BitmapSource image = Prepare(frame.ReadableCrop(selection, annotations));
        string text = await recognizer.RecognizeAsync(image, cancellationToken) ?? "";
        return NormalizeText(text);
    }

    public static string NormalizeText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd());
        return string.Join(Environment.NewLine, lines).Trim();
    }
}
