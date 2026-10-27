using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BroidShot.Capture;
using BroidShot.Editor;
using ZXing;
using ZXing.QrCode;

/// <summary>Pure-logic checks for the capture overlay tool modes (colour picker, ruler, QR, OCR).</summary>
internal static class OverlayTests
{
    private static BitmapSource Pattern(int width, int height, PixelFormat format)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bytes[i] = (byte)(x % 256);         // B
                bytes[i + 1] = (byte)(y % 256);     // G
                bytes[i + 2] = (byte)((x * 7 + y * 3) % 256); // R
                bytes[i + 3] = 255;
            }
        var source = BitmapSource.Create(width, height, 96, 96, format, null, bytes, width * 4);
        source.Freeze();
        return source;
    }

    public static void PixelMapping()
    {
        // A 300×200 physical-pixel snapshot shown at 150 %: the surface is 200×133.33 DIP.
        var desktop = new PixelRect(-1920, -1080, 300, 200);
        var surface = new Size(200, 200 / 1.5);
        Equal((150, 75), FrozenFrame.SurfaceToPixel(new Point(100.2, 50.1), surface, 300, 200));
        Equal((0, 0), FrozenFrame.SurfaceToPixel(new Point(-5, -5), surface, 300, 200));
        Equal((299, 199), FrozenFrame.SurfaceToPixel(new Point(200, 133.34), surface, 300, 200));
        Equal((1, 0), FrozenFrame.SurfaceToPixel(new Point(0.67, 0.1), surface, 300, 200)); // 0.67 DIP = 1.005 px
        Point centre = FrozenFrame.PixelToSurface(150.5, 75.5, surface, 300, 200);
        Equal((150, 75), FrozenFrame.SurfaceToPixel(centre, surface, 300, 200));

        foreach (PixelFormat format in new[] { PixelFormats.Bgra32, PixelFormats.Bgr32, PixelFormats.Pbgra32 })
        {
            var frame = new FrozenFrame(Pattern(300, 200, format), desktop);
            PixelColor expected = new((byte)((150 * 7 + 75 * 3) % 256), 75, 150);
            Equal<PixelColor?>(expected, frame.GetPixel(150, 75));
            Equal((-1770, -1005), frame.ToDesktop(150, 75));
            Equal<PixelColor?>(expected, frame.GetDesktopPixel(-1770, -1005));
            Check(frame.GetPixel(300, 0) is null && frame.GetPixel(-1, 5) is null && frame.GetDesktopPixel(0, 0) is null, "Outside the frame reads null.");
        }

        var colour = new PixelColor(217, 64, 70);
        Equal("#D94046", colour.Hex);
        Equal("rgb(217, 64, 70)", colour.RgbText);
        Equal("hsl(358, 67%, 55%)", colour.HslText);
        Check(!colour.AddToColorHistory(), "Without a running colour picker module the history call reports false instead of throwing.");
    }

    public static void Ruler()
    {
        RulerReading r = OverlayRuler.Measure(10, 10, 310, 410, false);
        Equal(300, r.Dx); Equal(400, r.Dy); Close(500, r.Distance); Close(306.87, r.AngleDegrees, 0.01);
        Check(r.Label.Contains("500 px") && r.Label.Contains("Δx 300") && r.Label.Contains("Δy 400") && r.Label.Contains("306.9°"), "Label: " + r.Label);
        Check(r.ClipboardText.StartsWith("500 px", StringComparison.Ordinal), "Clipboard text: " + r.ClipboardText);

        RulerReading h = OverlayRuler.Measure(0, 0, 100, 30, true);
        Equal((100, 0), (h.EndX, h.EndY)); Close(0, h.AngleDegrees); Check(h.Snapped, "Shift marks the reading as snapped.");
        RulerReading v = OverlayRuler.Measure(0, 0, 20, -90, true);
        Equal((0, -90), (v.EndX, v.EndY)); Close(90, v.AngleDegrees); Close(90, v.Distance);
        RulerReading tie = OverlayRuler.Measure(5, 5, 55, 55, true);
        Equal((55, 5), (tie.EndX, tie.EndY));
        Close(180, OverlayRuler.Measure(0, 0, -40, 0, false).AngleDegrees);
        Close(270, OverlayRuler.Measure(0, 0, 0, 25, false).AngleDegrees);
        Close(0, OverlayRuler.Measure(3, 3, 3, 3, false).Distance);
        Check(!OverlayRuler.Measure(0, 0, 100, 30, false).Snapped, "No Shift, no snapping.");

        Equal("320 × 200 px", OverlayRuler.SelectionSize(new Rect(0, 0, 320.4, 200)));
        Equal("0 × 0 px", OverlayRuler.SelectionSize(Rect.Empty));
        string old = CultureInfo.CurrentCulture.Name;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try { Check(OverlayRuler.Measure(0, 0, 1, 1, false).Label.StartsWith("1.41 px", StringComparison.Ordinal), "Invariant decimals under Turkish culture."); }
        finally { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(old.Length == 0 ? "en-US" : old); }
    }

    public static void Shortcuts()
    {
        Equal<OverlayMode?>(OverlayMode.ColorPicker, OverlayShortcuts.ModeForKey(Key.I, ModifierKeys.None));
        Equal<OverlayMode?>(OverlayMode.Ruler, OverlayShortcuts.ModeForKey(Key.M, ModifierKeys.None));
        Equal<OverlayMode?>(OverlayMode.Qr, OverlayShortcuts.ModeForKey(Key.Q, ModifierKeys.Shift));
        Equal<OverlayMode?>(OverlayMode.Ocr, OverlayShortcuts.ModeForKey(Key.O, ModifierKeys.None));
        Equal<OverlayMode?>(null, OverlayShortcuts.ModeForKey(Key.I, ModifierKeys.Control));
        Equal<OverlayMode?>(null, OverlayShortcuts.ModeForKey(Key.O, ModifierKeys.Alt));
        foreach (Key key in new[] { Key.V, Key.A, Key.R, Key.E, Key.P, Key.T, Key.N, Key.H, Key.B, Key.C, Key.S, Key.Z, Key.Y, Key.Delete, Key.Escape })
            Equal<OverlayMode?>(null, OverlayShortcuts.ModeForKey(key, ModifierKeys.None));
        Equal(OverlayMode.Capture, OverlayShortcuts.Toggle(OverlayMode.Ruler, OverlayMode.Ruler));
        Equal(OverlayMode.Qr, OverlayShortcuts.Toggle(OverlayMode.Ruler, OverlayMode.Qr));
    }

    private static byte[] QrModules(string text, int size)
    {
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "UTF-8", [EncodeHintType.MARGIN] = 4 };
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size, hints);
        var bytes = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte value = matrix[x, y] ? (byte)0 : (byte)255;
                int i = (y * size + x) * 4;
                bytes[i] = bytes[i + 1] = bytes[i + 2] = value;
                bytes[i + 3] = 255;
            }
        return bytes;
    }

    private static BitmapSource Screen(int width, int height, params (string Text, int X, int Y, int Size)[] codes)
    {
        var screen = new byte[width * height * 4];
        var random = new Random(11);
        for (int i = 0; i < screen.Length; i += 4)
        {
            byte v = (byte)(190 + random.Next(0, 50));
            screen[i] = v; screen[i + 1] = (byte)(v - 10); screen[i + 2] = (byte)(v - 20); screen[i + 3] = 255;
        }
        foreach (var code in codes)
        {
            byte[] modules = QrModules(code.Text, code.Size);
            for (int y = 0; y < code.Size; y++)
                Buffer.BlockCopy(modules, y * code.Size * 4, screen, ((code.Y + y) * width + code.X) * 4, code.Size * 4);
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, screen, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static void Qr()
    {
        const string link = "https://broidshot.example/ekran?a=1";
        const string plain = "BroidShot QR iki — çğışöü";
        var frame = new FrozenFrame(Screen(900, 700, (link, 80, 90, 210), (plain, 560, 400, 230)), new PixelRect(-1280, -200, 900, 700));

        IReadOnlyList<QrFinding> first = OverlayQr.Decode(frame, new Rect(40.5, 50.2, 300, 300));
        Equal(1, first.Count);
        Equal(link, first[0].Text);
        Equal(link, first[0].Link?.AbsoluteUri);
        Rect b = first[0].Bounds;
        Check(b.Contains(new Point(185, 195)) && b.Left >= 40 && b.Top >= 50 && b.Right <= 341 && b.Bottom <= 351, "Bounds sit on the code, in snapshot pixels: " + b);
        Check(b.Left > 95 && b.Left < 125 && b.Width > 135 && b.Width < 175, "Bounds hug the symbol (29 modules × 5 px, quiet zone excluded): " + b);

        IReadOnlyList<QrFinding> all = OverlayQr.Decode(frame, null);
        Equal(2, all.Count);
        Check(all.Any(f => f.Text == link) && all.Any(f => f.Text == plain && f.Link is null), "Both codes, the plain text is not a link.");
        QrFinding second = all.Single(f => f.Text == plain);
        Check(second.Bounds.Contains(new Point(675, 515)), "Second code located: " + second.Bounds);

        Equal(0, OverlayQr.Decode(frame, new Rect(320, 20, 200, 200)).Count);

        var redaction = new Annotation { Tool = AnnotationTool.Redact, Start = new Point(70, 80), End = new Point(300, 310) };
        Equal(0, OverlayQr.Decode(frame, new Rect(40, 50, 300, 300), new[] { redaction }).Count);
        Equal(1, OverlayQr.Decode(frame, null, new[] { redaction }).Count);

        Check(OverlayQr.TryGetWebLink("http://example.com/a b") is null, "Whitespace is not a link.");
        foreach (string unsafeText in new[] { "javascript:alert(1)", "file:///C:/Windows/notepad.exe", "ftp://example.com", "ms-settings:privacy", "www.example.com", "", null! })
            Check(OverlayQr.TryGetWebLink(unsafeText) is null, "Not offered as a link: " + unsafeText);
        Check(OverlayQr.TryGetWebLink(" https://example.com/x ")?.Host == "example.com", "Trimmed https link.");
    }

    private sealed class FakeRecognizer : IOverlayTextRecognizer
    {
        public bool IsAvailable { get; set; } = true;
        public string LanguageLabel => "Sahte";
        public BitmapSource? Received;
        public CancellationToken Token;
        public string Result = "  Merhaba Dünya  \r\nİkinci satır   \n\n";
        public Task<string> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken)
        {
            Received = image;
            Token = cancellationToken;
            return Task.FromResult(Result);
        }
    }

    private static byte[] Pixel(BitmapSource bitmap, int x, int y)
    {
        var converted = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var value = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), value, 4, 0);
        return value;
    }

    public static void OcrWiring()
    {
        var frame = new FrozenFrame(Pattern(200, 120, PixelFormats.Bgr32), new PixelRect(100, 50, 200, 120));
        var fake = new FakeRecognizer();
        using var cts = new CancellationTokenSource();
        string text = OverlayOcr.RecognizeAsync(fake, frame, new Rect(10.4, 20.2, 50, 30), null, cts.Token).GetAwaiter().GetResult();
        Equal("Merhaba Dünya" + Environment.NewLine + "İkinci satır", text);
        Check(fake.Token == cts.Token, "Cancellation token reaches the recogniser.");
        BitmapSource received = fake.Received ?? throw new InvalidOperationException("Recogniser not called.");
        Equal(2, OverlayOcr.UpscaleFactor(51, 31));
        Equal((102, 62), (received.PixelWidth, received.PixelHeight)); // enclosing 51×31 crop, 2× for small regions
        byte[] p = Pixel(received, 21, 21); // ≈ source pixel (20, 30)
        Check(Math.Abs(p[0] - 20) <= 3 && Math.Abs(p[1] - 30) <= 3, $"Upscaled crop keeps the frozen pixels: {p[0]},{p[1]}");

        BitmapSource raw = frame.ReadableCrop(new Rect(10, 20, 50, 30));
        Equal((50, 30), (raw.PixelWidth, raw.PixelHeight));
        byte[] exact = Pixel(raw, 7, 4);
        Check(exact[0] == 17 && exact[1] == 24 && exact[2] == (byte)((17 * 7 + 24 * 3) % 256), "Crop is exact physical pixels.");

        var redaction = new Annotation { Tool = AnnotationTool.Redact, Start = new Point(20, 25), End = new Point(45, 45) };
        OverlayOcr.RecognizeAsync(fake, frame, new Rect(10, 20, 50, 30), new[] { redaction }).GetAwaiter().GetResult();
        byte[] hidden = Pixel(fake.Received!, 2 * 22, 2 * 12); // source (32, 32): inside the redaction
        Check(hidden[0] < 12 && hidden[1] < 12 && hidden[2] < 12, "Redacted area is black before recognition.");

        Equal(1, OverlayOcr.UpscaleFactor(1920, 1080));
        Check(OverlayOcr.Prepare(raw).PixelWidth == 100, "Prepare doubles small crops.");

        fake.IsAvailable = false;
        try
        {
            OverlayOcr.RecognizeAsync(fake, frame, new Rect(0, 0, 10, 10), null).GetAwaiter().GetResult();
            throw new InvalidOperationException("Unavailable recogniser must fail.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("OCR")) { }
        try
        {
            frame.ReadableCrop(Rect.Empty);
            throw new InvalidOperationException("Empty selection must fail.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("alan")) { }
    }

    public static void RealOcr()
    {
        var recognizer = new OcrServiceRecognizer();
        if (!recognizer.IsAvailable) { Console.WriteLine("  SKIP: tessdata not next to the test binary."); return; }
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 40, 40)), null, new Rect(0, 0, 900, 400));
            context.DrawRectangle(Brushes.White, null, new Rect(200, 120, 520, 90));
            var text = new FormattedText("BROIDSHOT 2026 İstanbul", CultureInfo.GetCultureInfo("tr-TR"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 34, Brushes.Black, 1);
            context.DrawText(text, new Point(220, 140));
        }
        var bitmap = new RenderTargetBitmap(900, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var frame = new FrozenFrame(bitmap, new PixelRect(-900, 0, 900, 400));
        string result = OverlayOcr.RecognizeAsync(recognizer, frame, new Rect(200, 120, 520, 90), null).GetAwaiter().GetResult();
        Check(result.Contains("BROIDSHOT", StringComparison.OrdinalIgnoreCase) && result.Contains("2026"), "Real OCR text: " + result);
        Console.WriteLine($"  OCR ({recognizer.LanguageLabel}): {result.Replace(Environment.NewLine, " ")}");
    }

    public static void Glyphs()
    {
        var fonts = new List<GlyphTypeface>();
        foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            var typeface = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs) && glyphs.FamilyNames.Values.Any(v => v.Equals(name, StringComparison.OrdinalIgnoreCase))) fonts.Add(glyphs);
        }
        if (fonts.Count == 0) { Console.WriteLine("  SKIP: neither icon font is installed."); return; }
        foreach (string glyph in OverlayGlyphs.All)
            Check(fonts.Any(f => f.CharacterToGlyphMap.ContainsKey(glyph[0])), $"Glyph U+{(int)glyph[0]:X4} missing from the icon fonts.");
    }

    public static void WindowKeys()
    {
        if (Application.Current == null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var snapshot = new CaptureSnapshot(Pattern(320, 200, PixelFormats.Bgr32), new PixelRect(-320, 0, 320, 200),
            new[] { new MonitorInfo("A", new PixelRect(-320, 0, 160, 200), false), new MonitorInfo("B", new PixelRect(-160, 0, 160, 200), true) });
        var overlay = new CaptureOverlayWindow(snapshot);
        bool closed = false;
        overlay.Closed += (_, _) => closed = true;
        using var source = new HwndSource(new HwndSourceParameters("BroidShot overlay key test") { Width = 1, Height = 1 });
        void Press(Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            overlay.RaiseEvent(args);
        }
        Equal(OverlayMode.Capture, overlay.Mode);
        Press(Key.I); Equal(OverlayMode.ColorPicker, overlay.Mode);
        Press(Key.Right); Equal(OverlayMode.ColorPicker, overlay.Mode);
        Press(Key.I); Equal(OverlayMode.Capture, overlay.Mode);
        Press(Key.M); Equal(OverlayMode.Ruler, overlay.Mode);
        Press(Key.A); Equal(OverlayMode.Capture, overlay.Mode); // annotation keys return to the capture mode
        Press(Key.O); Equal(OverlayMode.Ocr, overlay.Mode);     // no selection: only a hint toast
        Press(Key.Escape); Equal(OverlayMode.Capture, overlay.Mode);
        Check(!closed, "Esc inside a tool mode returns to capture instead of closing.");
        Press(Key.Escape);
        Check(closed, "Esc in the capture mode closes the overlay as before.");
    }

    private static void Close(double expected, double actual, double tolerance = 1e-9)
    {
        if (Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
}
