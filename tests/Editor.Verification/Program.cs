using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BroidShot.Capture;
using BroidShot.Editor;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Mixed-monitor desktop coordinates become local crop coordinates", Geometry),
            ("Fractional and out-of-bounds crop uses enclosing physical pixels", Crop),
            ("Undo and redo preserve independent annotation snapshots", UndoRedo),
            ("Export crops exactly and permanently paints opaque redaction", Redaction),
            ("Shared annotation renderer produces identical preview and export pixels", PreviewParity),
            ("Turkish multiline text renders without truncating document state", TurkishText),
            ("Overlay: surface point -> frozen pixel colour at 150 % DPI with a negative virtual-screen origin", OverlayTests.PixelMapping),
            ("Overlay: ruler distance, dx/dy, angle and Shift snapping; live selection size", OverlayTests.Ruler),
            ("Overlay: mode shortcuts never steal annotation keys", OverlayTests.Shortcuts),
            ("Overlay: QR codes decoded from a region / the whole frozen bitmap, redaction respected, safe links", OverlayTests.Qr),
            ("Overlay: OCR wiring crops the selection, applies redaction, upscales and normalises (fake recogniser)", OverlayTests.OcrWiring),
            ("Overlay: real offline OCR of a frozen region when tessdata is present", OverlayTests.RealOcr),
            ("Overlay: icon glyphs exist in Segoe Fluent Icons or Segoe MDL2 Assets", OverlayTests.Glyphs),
            ("Overlay window: tool-mode keys, Esc returns to capture first, then closes", OverlayTests.WindowKeys)
        };
        int failures = 0;
        foreach (var test in tests)
        {
            try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
        }
        Console.WriteLine($"Editor verification: {tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Geometry()
    {
        var desktop = new PixelRect(-1920, -1080, 4480, 2520);
        Equal(new Rect(0, 0, 1920, 1080), CaptureGeometry.ToSnapshotSelection(desktop, new PixelRect(-1920, -1080, 1920, 1080)));
        Equal(new Rect(1920, 1080, 2560, 1440), CaptureGeometry.ToSnapshotSelection(desktop, new PixelRect(0, 0, 2560, 1440)));
        Equal(new Rect(0, 0, 120, 80), CaptureGeometry.ToSnapshotSelection(desktop, new PixelRect(-2000, -1100, 200, 100)));
        Check(CaptureGeometry.ToSnapshotSelection(desktop, new PixelRect(8000, 8000, 100, 100)).IsEmpty, "Outside monitor must have empty intersection.");
        Check(CaptureGeometry.ToSnapshotSelection(desktop, new PixelRect(0, 0, -1, 30)).IsEmpty, "Invalid size must not construct a negative rectangle.");
    }

    private static void Crop()
    {
        Equal(new Rect(0, 0, 16, 9), AnnotationRenderer.PixelSelection(new Rect(-5.2, -4.3, 20.6, 12.8), 100, 100));
        Equal(new Rect(10, 20, 6, 6), AnnotationRenderer.PixelSelection(new Rect(10.1, 20.1, 5.1, 5.1), 100, 100));
        Equal(new Rect(99, 99, 1, 1), AnnotationRenderer.PixelSelection(new Rect(99.9, 99.9, 20, 20), 100, 100));
        Check(AnnotationRenderer.PixelSelection(Rect.Empty, 100, 100).IsEmpty, "Empty crop must remain empty.");
    }

    private static void UndoRedo()
    {
        var document = new AnnotationDocument { Selection = new Rect(0, 0, 64, 48) };
        document.Checkpoint();
        var item = new Annotation { Tool = AnnotationTool.Text, Start = new Point(2, 3), End = new Point(20, 12), Text = "İlk metin" };
        document.Items.Add(item);
        document.Checkpoint();
        item.Text = "Değişen yazı";
        item.Move(new Vector(10, 5));
        document.Undo();
        Equal("İlk metin", document.Items.Single().Text);
        Equal(new Point(2, 3), document.Items.Single().Start);
        item.Text = "Eski referansın değişmesi geçmişi etkilememeli";
        Equal("İlk metin", document.Items.Single().Text);
        document.Undo();
        Equal(0, document.Items.Count);
        document.Redo();
        Equal("İlk metin", document.Items.Single().Text);
        document.Redo();
        Equal("Değişen yazı", document.Items.Single().Text);
        Equal(new Point(12, 8), document.Items.Single().Start);
        document.Undo();
        document.Checkpoint();
        document.Items.Single().Text = "Yeni dal";
        Check(!document.CanRedo, "Editing after undo must discard the previous redo branch.");
    }

    private static BitmapSource Background(int width = 64, int height = 48)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                bytes[offset] = (byte)(x + 20);
                bytes[offset + 1] = (byte)(y + 30);
                bytes[offset + 2] = 130;
                bytes[offset + 3] = 255;
            }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        source.Freeze();
        return source;
    }

    private static void Redaction()
    {
        var document = new AnnotationDocument { Selection = new Rect(5, 6, 20, 15) };
        document.Items.Add(new Annotation { Tool = AnnotationTool.Redact, Start = new Point(10, 10), End = new Point(18, 16) });
        var output = AnnotationRenderer.Export(Background(), document);
        Equal(20, output.PixelWidth);
        Equal(15, output.PixelHeight);
        Check(output.IsFrozen, "Export passed to history must be frozen.");
        var pixel = Pixel(output, 6, 5);
        Equal((byte)0, pixel[0]); Equal((byte)0, pixel[1]); Equal((byte)0, pixel[2]); Equal((byte)255, pixel[3]);
        var outside = Pixel(output, 0, 0);
        Equal((byte)25, outside[0]); Equal((byte)36, outside[1]); Equal((byte)130, outside[2]); Equal((byte)255, outside[3]);
        var decoder = new PngBitmapEncoder();
        decoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = new System.IO.MemoryStream();
        decoder.Save(stream);
        stream.Position = 0;
        var reloaded = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var rgba = new FormatConvertedBitmap(reloaded, PixelFormats.Bgra32, null, 0);
        var persisted = Pixel(rgba, 6, 5);
        Check(persisted.SequenceEqual(new byte[] { 0, 0, 0, 255 }), "Redaction must survive PNG encoding as opaque black pixels.");
    }

    private static void PreviewParity()
    {
        var source = Background();
        var document = new AnnotationDocument { Selection = new Rect(5, 6, 40, 30) };
        document.Items.Add(new Annotation { Tool = AnnotationTool.Arrow, Start = new Point(8, 12), End = new Point(40, 28), Color = Colors.Red, Thickness = 3 });
        document.Items.Add(new Annotation { Tool = AnnotationTool.Highlight, Start = new Point(0, 16), End = new Point(30, 21), Color = Colors.Gold });
        document.Items.Add(new Annotation { Tool = AnnotationTool.Redact, Start = new Point(31, 6), End = new Point(55, 18) });
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            context.PushClip(new RectangleGeometry(document.Selection));
            AnnotationRenderer.Draw(context, document.Items);
            context.Pop();
        }
        var preview = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        preview.Render(visual);
        var croppedPreview = new CroppedBitmap(preview, new Int32Rect(5, 6, 40, 30));
        var export = AnnotationRenderer.Export(source, document);
        var previewBytes = new byte[40 * 30 * 4];
        var exportBytes = new byte[40 * 30 * 4];
        croppedPreview.CopyPixels(previewBytes, 40 * 4, 0);
        export.CopyPixels(exportBytes, 40 * 4, 0);
        var differences = Enumerable.Range(0, previewBytes.Length).Where(i => previewBytes[i] != exportBytes[i]).ToArray();
        Check(differences.Length == 0, $"Selected preview and exported image must agree byte for byte. {differences.Length} differing channels; first: {string.Join(", ", differences.Take(8).Select(i => $"{i}: {previewBytes[i]}/{exportBytes[i]}"))}");
    }

    private static void TurkishText()
    {
        var document = new AnnotationDocument { Selection = new Rect(0, 0, 64, 48) };
        document.Items.Add(new Annotation { Tool = AnnotationTool.Text, Start = new Point(2, 2), End = new Point(60, 40), Text = "İıŞş\nĞğÇç", Color = Colors.White, Thickness = 2 });
        var source = Background();
        var before = new byte[64 * 48 * 4];
        var after = new byte[before.Length];
        source.CopyPixels(before, 64 * 4, 0);
        AnnotationRenderer.Export(source, document).CopyPixels(after, 64 * 4, 0);
        Check(!before.SequenceEqual(after), "Text renderer must paint visible characters.");
        Equal("İıŞş\nĞğÇç", document.Items.Single().Text);
    }

    private static byte[] Pixel(BitmapSource bitmap, int x, int y)
    {
        var value = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), value, 4, 0);
        return value;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
}
