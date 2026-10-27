using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BroidShot.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int result = 1;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) =>
        {
            try { await RunAsync(args.Contains("--clipboard-verify", StringComparer.OrdinalIgnoreCase)); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { application.Shutdown(); }
        };
        application.Run();
        return result;
    }

    private static async Task RunAsync(bool verifyClipboard)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        BitmapSource image = CreateTextImage();
        string png = Path.Combine(root, "export.png");
        string jpeg = Path.Combine(root, "export.jpg");
        ImageExporter.Save(image, png);
        ImageExporter.Save(image, jpeg);
        Require(File.ReadAllBytes(png).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG header");
        Require(File.ReadAllBytes(jpeg).Take(2).SequenceEqual(new byte[] { 255, 216 }), "JPEG header");
        Console.WriteLine("PASS: PNG and JPEG export");

        var history = new HistoryStore(Path.Combine(root, "history"));
        HistoryItem item = await history.AddAsync(image);
        var reopened = new HistoryStore(history.RootDirectory);
        var entries = await reopened.LoadAsync();
        Require(entries.Count == 1 && entries[0].Id == item.Id, "history restart");
        Require(reopened.LoadImage(entries[0]).PixelWidth == image.PixelWidth, "history image");
        await reopened.UpdateOcrAsync(item, "İstanbul BroidShot");
        Require((await reopened.LoadAsync())[0].OcrText == "İstanbul BroidShot", "OCR metadata update");
        Console.WriteLine("PASS: history persistence, frozen image, OCR metadata");

        string metadata = Path.Combine(history.RootDirectory, item.Id + ".json");
        File.WriteAllText(metadata, JsonSerializer.Serialize(item with { ImagePath = png }));
        Require((await reopened.LoadAsync())[0].ImagePath == item.ImagePath, "persisted path sanitization");
        bool rejected = false;
        try { await reopened.DeleteAsync(item with { ImagePath = png }); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected && File.Exists(png), "outside deletion rejected");
        File.WriteAllText(Path.Combine(history.RootDirectory, Guid.NewGuid().ToString("N") + ".json"), "broken json");
        Require((await reopened.LoadAsync()).Count == 1, "malformed metadata isolated");
        Console.WriteLine("PASS: forged paths rejected and malformed metadata isolated");

        reopened.Retention = TimeSpan.Zero;
        Require((await reopened.LoadAsync()).Count == 0 && !File.Exists(item.ImagePath), "retention removes image and metadata");
        Console.WriteLine("PASS: retention");

        var tinyHistory = new HistoryStore(Path.Combine(root, "tiny-history")) { MaxStorageBytes = 10 };
        bool capacityRejected = false;
        try { await tinyHistory.AddAsync(image); }
        catch (IOException) { capacityRejected = true; }
        Require(capacityRejected && (await tinyHistory.LoadAsync()).Count == 0, "oversized image must not return a missing history item");
        Console.WriteLine("PASS: oversized history image fails cleanly");

        var ocr = new OcrService();
        Require(ocr.IsAvailable, "OCR models");
        string text = await ocr.RecognizeAsync(image);
        Require(text.Contains("BROIDSHOT", StringComparison.OrdinalIgnoreCase) && text.Contains("12345"), "offline OCR output: " + text);
        Console.WriteLine("PASS: real offline Turkish + English OCR: " + text.Replace('\n', ' '));
        if (verifyClipboard)
        {
            Console.WriteLine("Clipboard verification requested: replaces the current system clipboard with the synthetic test image.");
            await ClipboardService.CopyImageAsync(image);
            BitmapSource? bitmap = Clipboard.GetImage();
            Require(bitmap is not null, "bitmap clipboard format");
            Require(bitmap!.PixelWidth == image.PixelWidth && bitmap.PixelHeight == image.PixelHeight, "bitmap clipboard dimensions");
            Require(Pixels(image).SequenceEqual(Pixels(bitmap)), "bitmap clipboard pixel roundtrip");
            using Stream pngStream = Clipboard.GetData("PNG") as Stream ?? throw new InvalidOperationException("Verification failed: PNG clipboard format");
            if (pngStream.CanSeek) pngStream.Position = 0;
            BitmapSource pngImage = BitmapDecoder.Create(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Require(pngImage.PixelWidth == image.PixelWidth && pngImage.PixelHeight == image.PixelHeight, "PNG clipboard dimensions");
            Require(Pixels(image).SequenceEqual(Pixels(pngImage)), "PNG clipboard pixel roundtrip");
            Console.WriteLine("PASS: clipboard bitmap + PNG formats, exact dimensions and exact pixel readback");
        }
        Console.WriteLine("Verification output: " + root);
    }

    private static BitmapSource CreateTextImage()
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 800, 180));
            var text = new FormattedText("BROIDSHOT TEST 12345\nTürkçe İstanbul ekran alıntısı", CultureInfo.GetCultureInfo("tr-TR"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 36, Brushes.Black, 1);
            context.DrawText(text, new Point(25, 25));
        }
        var bitmap = new RenderTargetBitmap(800, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Verification failed: " + name);
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = checked(image.PixelWidth * 4);
        var pixels = new byte[checked(stride * image.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
