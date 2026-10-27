using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BroidShot.Core;
using BroidShot.Modules.Convert;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

// Verification for the convert module. Everything happens in a temp folder; no network, no real user data, no clipboard.
internal static partial class Program
{
    private static int assertions;
    private static string root = "";
    private static string? renderDir;

    [STAThread]
    private static int Main(string[] args)
    {
        // --render <folder>: also save PNGs of the page states and the password window (visual check).
        if (args.Length == 2 && args[0] == "--render") renderDir = args[1];
        // Like the app: an Application (registers pack://) and the theme font defaults before the first window.
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ThemeResources.ApplyDefaults();
        root = Path.Combine(Path.GetTempPath(), "BroidShotConvertVerify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(DataPaths.DataDirectoryVariable, Path.Combine(root, "profile"));
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Page range parser: valid ranges, defaults, groups", Sync(PageRanges)),
            ("Page range parser: invalid input is rejected with a message", Sync(PageRangeErrors)),
            ("Image resize and page-fit arithmetic keeps aspect ratio", Sync(ImageMathChecks)),
            ("Name templates, sanitising and collision-free names", Sync(Naming)),
            ("Atomic write never overwrites, even under parallel writers", AtomicWrites),
            ("Operations offered for each file-type mix", Sync(ValidOperations)),
            ("PDF -> PNG: page count and pixel size per DPI, page ranges, name template", Sync(PdfToPng)),
            ("PDF -> JPG: quality changes size, white background", Sync(PdfToJpg)),
            ("Password-protected PDF: prompt, wrong password retry, skip, no prompt", Sync(PasswordPdf)),
            ("Per-file errors are isolated and cancellation leaves no temp files", Sync(ErrorsAndCancel)),
            ("Images -> PDF: page count, order, A4 fit and original size, bad image skipped", Sync(ImagesToPdf)),
            ("PDF merge and split: page counts and order", Sync(MergeAndSplit)),
            ("PDF extract, delete and rotate pages", Sync(EditPages)),
            ("Image convert: formats, resize by percent and max size, transparency", Sync(ConvertFormats)),
            ("Image convert: metadata kept or stripped, EXIF orientation baked in", Sync(MetadataAndOrientation)),
            ("Resampling is exact for flat colours and averages fine patterns", Sync(Resampling)),
            ("PNG compression levels keep the pixels", Sync(PngCompressionLevels)),
            ("ICO output has every requested size and centres non-square sources", Sync(IconOutput)),
            ("Unreadable or codec-less images give a clear message", Sync(Unreadable)),
            ("Repeated conversions never overwrite earlier outputs", Sync(NoOverwrite)),
            ("Module: commands, shared FilesToConvertMessage and page smoke test", Sync(ModuleSmoke)),
            ("Output folder action: spaces, commas, Turkish names and missing output files", Sync(OutputFolderAction)),
            ("Page: empty state, operation tiles, segmented options, per-file status, icons", Sync(PageStates.Run)),
            ("Password window: themed, dark, wrong-password notice", Sync(PageStates.PasswordPrompt)),
        };
        int failures = 0;
        foreach (var test in tests)
        {
            try { test.Run().GetAwaiter().GetResult(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
        }
        try { Directory.Delete(root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Console.WriteLine($"Convert verification: {tests.Length - failures}/{tests.Length} passed, {assertions} assertions.");
        return failures == 0 ? 0 : 1;
    }

    private static void OutputFolderAction()
    {
        string folder = Path.Combine(root, "İndirilenler, dönüştürülen belgeler");
        Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, "BroidShot_2026-10-27_20-31-54-783 (2).pdf");
        File.WriteAllText(output, "test");
        var info = ConvertView.OutputFolderStartInfo(output);
        Assert(info is not null && info.FileName == folder && info.UseShellExecute, "The shell opens the exact output folder, including spaces and commas.");
        Assert(info!.ArgumentList.Count == 0 && info.Arguments.Length == 0, "The folder path is not parsed as an Explorer command or switch.");
        File.Delete(output);
        Assert(ConvertView.OutputFolderStartInfo(output)?.FileName == folder, "The folder still opens after the output file has been moved or deleted.");
        Assert(ConvertView.OutputFolderStartInfo(Path.Combine(root, "missing-folder", "file.pdf")) is null, "A missing folder does not open an unrelated Explorer location.");
    }

    private static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private static string NewFolder(string name)
    {
        string path = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(path);
        return path;
    }

    // ---------- helpers: PDFs ----------

    private static readonly (double W, double H) A4 = (595.276, 841.89);

    private static string MakePdf(string path, params (double W, double H)[] pages)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        for (int i = 0; i < pages.Length; i++)
        {
            PdfSharp.Pdf.PdfPage page = document.AddPage();
            page.Width = XUnit.FromPoint(pages[i].W);
            page.Height = XUnit.FromPoint(pages[i].H);
            using XGraphics graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(XBrushes.Crimson, 20, 20, 120, 70);
            graphics.DrawString($"Sayfa {i + 1}", new XFont("Arial", 22), XBrushes.Black, 30, 130);
        }
        document.Save(path);
        return path;
    }

    private static string MakeEncryptedPdf(string path, string userPassword, PdfDefaultEncryption encryption = PdfDefaultEncryption.V2With128Bits, int pages = 2)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            PdfSharp.Pdf.PdfPage page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            using XGraphics graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(XBrushes.Navy, 30, 30, 100, 60);
        }
        document.SecuritySettings.UserPassword = userPassword;
        document.SecuritySettings.OwnerPassword = userPassword + "-owner";
        document.SecurityHandler.SetEncryption(encryption);
        document.Save(path);
        return path;
    }

    private static List<(double W, double H, int Rotate)> ReadPdf(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var pages = new List<(double, double, int)>();
        foreach (PdfSharp.Pdf.PdfPage page in document.Pages) pages.Add((page.Width.Point, page.Height.Point, page.Rotate));
        return pages;
    }

    private static (int W, int H) Dimensions(string path)
    {
        using var stream = File.OpenRead(path);
        BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static bool Near(double a, double b, double tolerance = 1.01) => Math.Abs(a - b) <= tolerance;

    private static string[] Files(string folder, string pattern) =>
        Directory.GetFiles(folder, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

    private static BatchOutcome Run(ConvertOperation operation, ConvertOptions options, PdfPasswordPrompt? prompt, CancellationToken token, params string[] paths) =>
        Run(operation, options, prompt, token, null, paths);

    private static BatchOutcome Run(ConvertOperation operation, ConvertOptions options, PdfPasswordPrompt? prompt, CancellationToken token, Action<double?, string>? report, params string[] paths)
    {
        var items = paths.Select(p => new ConvertItem(p)).ToList();
        var engine = new ConvertEngine(prompt) { Report = report };
        return engine.RunAsync(operation, items, options, token).GetAwaiter().GetResult();
    }

    private static BatchOutcome Run(ConvertOperation operation, ConvertOptions options, params string[] paths) =>
        Run(operation, options, null, CancellationToken.None, paths);

    // ---------- helpers: images ----------

    private static string MakeImage(string path, int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)> pixel, BitmapEncoder? encoder = null, BitmapMetadata? metadata = null)
    {
        byte[] data = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                int i = (y * width + x) * 4;
                // BitmapSource.Create with Bgra32 expects straight alpha.
                data[i] = b; data[i + 1] = g; data[i + 2] = r; data[i + 3] = a;
            }
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4);
        encoder ??= path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static (byte B, byte G, byte R, byte A) Gradient(int x, int y) => ((byte)(x * 255 / 100 % 256), (byte)(y * 255 / 100 % 256), 90, 255);

    private static (byte B, byte G, byte R, byte A) ReadPixel(string path, int x, int y)
    {
        using var stream = File.OpenRead(path);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        byte[] px = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2], px[3]);
    }

    // ---------- parser / math / naming ----------

    private static void PageRanges()
    {
        PageSelection s = PageRangeParser.Parse("1-3,5", 10);
        Assert(s.Success && s.Pages.SequenceEqual(new[] { 1, 2, 3, 5 }), "\"1-3,5\" selects 1,2,3,5.");
        Assert(s.Groups.Count == 2 && s.Groups[0].SequenceEqual(new[] { 1, 2, 3 }) && s.Groups[1].SequenceEqual(new[] { 5 }), "Each comma item forms a group.");
        Assert(PageRangeParser.Parse("", 4).Pages.SequenceEqual(new[] { 1, 2, 3, 4 }), "Empty text selects every page.");
        Assert(PageRangeParser.Parse("   ", 3).Pages.Count == 3 && PageRangeParser.Parse(null, 3).Pages.Count == 3, "Blank and null select every page.");
        Assert(PageRangeParser.Parse("8-", 10).Pages.SequenceEqual(new[] { 8, 9, 10 }), "\"8-\" runs to the last page.");
        Assert(PageRangeParser.Parse("3, 1 ,3", 5).Pages.SequenceEqual(new[] { 3, 1 }), "Order of appearance is kept, duplicates dropped.");
        Assert(PageRangeParser.Parse("2;4", 5).Pages.SequenceEqual(new[] { 2, 4 }), "Semicolons separate too.");
        Assert(PageRangeParser.Parse("2–4", 5).Pages.SequenceEqual(new[] { 2, 3, 4 }), "En dash works as a range dash.");
        Assert(PageRangeParser.Parse("1-3,", 5).Success, "A trailing comma is ignored.");
        Assert(PageRangeParser.Parse("10", 10).Pages.SequenceEqual(new[] { 10 }), "The last page is selectable.");
        Assert(PageRangeParser.Format(new[] { 1, 2, 3, 5, 7, 8 }) == "1-3,5,7-8", "Format compacts runs.");
        Assert(PageRangeParser.ValidateSyntax("1-3,5") is null && PageRangeParser.ValidateSyntax("") is null, "Valid syntax passes without a page count.");
    }

    private static void PageRangeErrors()
    {
        foreach (string bad in new[] { "abc", "3-1", "0", "1-0", "-3", "1--2", "1-2-3", "x-5", "1,a", ",,,", "1.5", "2 3" })
        {
            PageSelection s = PageRangeParser.Parse(bad, 10);
            Assert(!s.Success && !string.IsNullOrWhiteSpace(s.Error) && s.Pages.Count == 0, $"\"{bad}\" is rejected with a message (got: {s.Error ?? "success"}).");
        }
        PageSelection tooHigh = PageRangeParser.Parse("1-3,11", 10);
        Assert(!tooHigh.Success && tooHigh.Error!.Contains("11") && tooHigh.Error.Contains("10"), "Out-of-range page names the page and the page count.");
        Assert(!PageRangeParser.Parse("5-12", 10).Success, "A range running past the end is rejected.");
        Assert(!PageRangeParser.Parse("1", 0).Success, "A document without pages is rejected.");
        Assert(PageRangeParser.ValidateSyntax("1-") is null && PageRangeParser.ValidateSyntax("q") is not null, "Syntax validation flags garbage.");
        Assert(PageRangeParser.ValidateSyntax(" , ") is not null, "Only separators is an error.");
    }

    private static void ImageMathChecks()
    {
        Assert(ImageMath.ScalePercent(1000, 500, 50) == (500, 250), "50% halves both sides.");
        Assert(ImageMath.ScalePercent(1001, 501, 50) == (501, 251), "Percent rounds half away from zero.");
        Assert(ImageMath.ScalePercent(10, 10, 1) == (1, 1) && ImageMath.ScalePercent(3, 3, 1).Width >= 1, "A side is never smaller than 1.");
        Assert(ImageMath.ScalePercent(100, 50, 200) == (200, 100), "Percent above 100 enlarges.");
        Assert(ImageMath.FitWithin(4000, 3000, 1920, 1080) == (1440, 1080), "4:3 inside 1920x1080 is limited by height.");
        Assert(ImageMath.FitWithin(3000, 4000, 1920, 1080) == (810, 1080), "Portrait inside 1920x1080 is limited by height.");
        Assert(ImageMath.FitWithin(1000, 2000, 500, 0) == (500, 1000), "Zero means no limit on that side.");
        Assert(ImageMath.FitWithin(1000, 2000, 0, 500) == (250, 500), "Zero width limit leaves only the height limit.");
        Assert(ImageMath.FitWithin(800, 600, 1920, 1080) == (800, 600), "Small images are not enlarged by default.");
        Assert(ImageMath.FitWithin(800, 600, 1600, 1600, allowUpscale: true) == (1600, 1200), "Upscaling works when allowed.");
        Assert(ImageMath.FitWithin(640, 480, 0, 0) == (640, 480), "No limits leaves the size alone.");
        var rng = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            int w = rng.Next(1, 9000), h = rng.Next(1, 9000), mw = rng.Next(1, 3000), mh = rng.Next(1, 3000);
            var (rw, rh) = ImageMath.FitWithin(w, h, mw, mh);
            Assert(rw >= 1 && rh >= 1 && rw <= Math.Max(w, 1) && rh <= Math.Max(h, 1) && rw <= mw && rh <= mh, $"Fit never exceeds the limits ({w}x{h} in {mw}x{mh} -> {rw}x{rh}).");
            double expected = w / (double)h, actual = rw / (double)rh;
            if (rw > 1 && rh > 1) Assert(Math.Abs(expected - actual) / expected <= 1.0 / Math.Min(rw, rh), $"Aspect ratio kept within rounding ({w}x{h} -> {rw}x{rh}).");
        }
        var landscape = ImageMath.FitToA4(2000, 1000);
        Assert(landscape.PageWidth > landscape.PageHeight && Near(landscape.Width, landscape.PageWidth, 0.01) && Near(landscape.Height, landscape.PageWidth / 2, 0.01), "Wide images get a landscape A4 page, fitted to its width.");
        var portrait = ImageMath.FitToA4(1000, 3000);
        Assert(portrait.PageHeight > portrait.PageWidth && Near(portrait.Height, portrait.PageHeight, 0.01) && portrait.X > 0, "Tall images are fitted to the height and centred.");
        Assert(ImageMath.PagePixels(595.276 * 96 / 72, 841.89 * 96 / 72, 150) == (1240, 1754), "A4 at 150 DPI is 1240 x 1754.");
        var original = ImageMath.OriginalPageSize(960, 480, 96, 96);
        Assert(Near(original.Width, 720, 0.01) && Near(original.Height, 360, 0.01), "Original size maps pixels at 96 DPI to points.");
        Assert(ImageMath.OriginalPageSize(100, 100, 0, 0).Width > 0, "Unknown DPI falls back to 96.");
    }

    private static void Naming()
    {
        Assert(FileNaming.ExpandTemplate("{ad}_s{sayfa}", "rapor", "3", 12) == "rapor_s3", "Default template: name_sPage.");
        Assert(FileNaming.ExpandTemplate("{ad}_s{sayfa:3}", "rapor", "3", 12) == "rapor_s003", "{sayfa:3} zero-pads.");
        Assert(FileNaming.ExpandTemplate("{AD} {Sayfa}/{toplam}", "x", "2", 9) == "x 2/9".Replace('/', '_'), "Tokens are case-insensitive and the result is sanitised.");
        Assert(FileNaming.ExpandTemplate("", "belge", "1", 2) == "belge_s1", "Empty template falls back to the default.");
        Assert(FileNaming.ExpandTemplate("{ad}_{aralik}", "a", "1", 1) == "a_{aralik}", "Unknown tokens stay as typed.");
        Assert(FileNaming.ExpandTemplate("{ad}_s{sayfa}", "1-3", "1-3", 5) == "1-3_s1-3", "Range labels work as page labels.");
        Assert(FileNaming.Sanitize("a:b*c?.png") == "a_b_c_.png", "Invalid characters become underscores.");
        Assert(FileNaming.Sanitize("CON") == "_CON" && FileNaming.Sanitize("nul.txt") == "_nul.txt", "Reserved device names are avoided.");
        Assert(FileNaming.Sanitize("  ...  ") == "dosya" && FileNaming.Sanitize(null) == "dosya", "Empty names fall back.");
        Assert(FileNaming.Sanitize("Çığ öşü.png") == "Çığ öşü.png", "Turkish letters are kept.");

        string folder = NewFolder("naming");
        string target = Path.Combine(folder, "a.png");
        Assert(FileNaming.GetUniquePath(target) == target, "A free name is returned unchanged.");
        File.WriteAllText(target, "1");
        Assert(FileNaming.GetUniquePath(target) == Path.Combine(folder, "a (2).png"), "A taken name gets \" (2)\".");
        File.WriteAllText(Path.Combine(folder, "a (2).png"), "2");
        Assert(FileNaming.GetUniquePath(target) == Path.Combine(folder, "a (3).png"), "Then \" (3)\".");
        Directory.CreateDirectory(Path.Combine(folder, "klasor"));
        Assert(FileNaming.GetUniquePath(Path.Combine(folder, "klasor")) == Path.Combine(folder, "klasor (2)"), "A folder with the same name also counts as taken.");
    }

    private static async Task AtomicWrites()
    {
        string folder = NewFolder("atomic");
        string target = Path.Combine(folder, "out.bin");
        File.WriteAllBytes(target, new byte[] { 9, 9, 9 });
        string written = await FileNaming.WriteAtomicAsync(target, new byte[] { 1, 2, 3 }, CancellationToken.None);
        Assert(written == Path.Combine(folder, "out (2).bin"), "Existing files are not overwritten.");
        Assert(File.ReadAllBytes(target).SequenceEqual(new byte[] { 9, 9, 9 }), "The existing file keeps its content.");
        Assert(Directory.GetFiles(folder, "*.tmp").Length == 0 && Directory.GetFiles(folder, ".*").Length == 0, "No temp file is left.");

        string racing = Path.Combine(folder, "race.bin");
        string[] results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            FileNaming.WriteAtomicAsync(racing, new byte[] { (byte)i, (byte)i }, CancellationToken.None))));
        Assert(results.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 24, "Parallel writers all get distinct names.");
        Assert(results.All(r => File.ReadAllBytes(r).Length == 2) && results.Select(r => File.ReadAllBytes(r)[0]).Distinct().Count() == 24, "No content was lost or mixed.");
        Assert(Directory.GetFiles(folder, ".*").Length == 0, "No temp files after racing writers.");

        string failing = Path.Combine(folder, "fail.bin");
        try
        {
            await FileNaming.WriteAtomicAsync(failing, (stream, token) => throw new InvalidOperationException("kasıtlı hata"), CancellationToken.None);
            Assert(false, "The failure must propagate.");
        }
        catch (InvalidOperationException) { assertions++; }
        Assert(!File.Exists(failing) && Directory.GetFiles(folder, ".*").Length == 0, "A failed write leaves neither the target nor a temp file.");
    }

    private static void ValidOperations()
    {
        var pdf = FileTypes.ValidOperations(new[] { FileKind.Pdf, FileKind.Pdf });
        Assert(pdf.Contains(ConvertOperation.PdfToImages) && pdf.Contains(ConvertOperation.MergePdf) && pdf.Contains(ConvertOperation.SplitPdf)
            && pdf.Contains(ConvertOperation.ExtractPages) && pdf.Contains(ConvertOperation.DeletePages) && pdf.Contains(ConvertOperation.RotatePages), "PDFs offer the PDF operations.");
        Assert(!pdf.Contains(ConvertOperation.ImagesToPdf) && !pdf.Contains(ConvertOperation.ConvertImages), "PDFs do not offer image operations.");
        var images = FileTypes.ValidOperations(new[] { FileKind.Image });
        Assert(images.SequenceEqual(new[] { ConvertOperation.ImagesToPdf, ConvertOperation.ConvertImages }), "Images offer images->PDF and format conversion only.");
        Assert(FileTypes.ValidOperations(new[] { FileKind.Image, FileKind.Pdf }).Count == 0, "A mixed list has no valid operation.");
        Assert(FileTypes.ValidOperations(Array.Empty<FileKind>()).Count == Enum.GetValues<ConvertOperation>().Length, "An empty list shows every operation.");
        Assert(FileTypes.GetKind("a.PDF") == FileKind.Pdf && FileTypes.GetKind("b.JpEg") == FileKind.Image && FileTypes.GetKind("c.webp") == FileKind.Image && FileTypes.GetKind("d.docx") is null, "File kinds follow the extension.");
    }

    // ---------- PDF -> images ----------

    private static void PdfToPng()
    {
        string folder = NewFolder("pdf2png");
        string pdf = MakePdf(Path.Combine(folder, "rapor.pdf"), A4, A4, (288, 144));
        foreach (int dpi in new[] { 72, 150, 300 })
        {
            string output = Path.Combine(folder, "out" + dpi);
            var options = new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = output, Dpi = dpi, RasterFormat = RasterFormat.Png };
            BatchOutcome outcome = Run(ConvertOperation.PdfToImages, options, pdf);
            Assert(outcome.Failed == 0 && outcome.Files[0].Error is null, $"{dpi} DPI converts without errors ({outcome.Files[0].Error}).");
            string[] files = Files(output, "*.png");
            Assert(files.Length == 3, $"{dpi} DPI writes one PNG per page (got {files.Length}).");
            Assert(files.Select(Path.GetFileName).SequenceEqual(new[] { "rapor_s1.png", "rapor_s2.png", "rapor_s3.png" }), "Files follow the {ad}_s{sayfa} template.");
            var (w1, h1) = Dimensions(files[0]);
            var (w3, h3) = Dimensions(files[2]);
            Assert(Near(w1, A4.W * dpi / 72) && Near(h1, A4.H * dpi / 72), $"A4 page at {dpi} DPI is {A4.W * dpi / 72:0}x{A4.H * dpi / 72:0} px (got {w1}x{h1}).");
            Assert(Near(w3, 288.0 * dpi / 72) && Near(h3, 144.0 * dpi / 72), $"Landscape page at {dpi} DPI is {288.0 * dpi / 72:0}x{144.0 * dpi / 72:0} px (got {w3}x{h3}).");
        }

        // Rendered content: white paper, a crimson rectangle at (20,20)pt, black text further down.
        string check = Path.Combine(folder, "out150", "rapor_s1.png");
        var paper = ReadPixel(check, 1100, 1600);
        Assert(paper.R > 250 && paper.G > 250 && paper.B > 250 && paper.A == 255, "The page background is opaque white.");
        var rectangle = ReadPixel(check, (int)(60 * 150 / 72.0), (int)(55 * 150 / 72.0));
        Assert(rectangle.R > 180 && rectangle.G < 80 && rectangle.B < 90, $"The crimson rectangle is rendered (got R{rectangle.R} G{rectangle.G} B{rectangle.B}).");

        // Page range and template.
        string ranged = Path.Combine(folder, "ranged");
        var rangeOptions = new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = ranged, Dpi = 72, PageRange = "1,3", NameTemplate = "{ad}-{sayfa:3}-{toplam}" };
        BatchOutcome rangeOutcome = Run(ConvertOperation.PdfToImages, rangeOptions, pdf);
        Assert(Files(ranged, "*.png").Select(Path.GetFileName).SequenceEqual(new[] { "rapor-001-3.png", "rapor-003-3.png" }), "A page range renders only those pages with the custom template.");
        Assert(rangeOutcome.Outputs.Count() == 2, "The outcome lists both outputs.");

        // Default location: next to the source.
        var nextTo = new ConvertOptions { Dpi = 72, PageRange = "2" };
        Run(ConvertOperation.PdfToImages, nextTo, pdf);
        Assert(File.Exists(Path.Combine(folder, "rapor_s2.png")), "By default outputs go next to the source file.");

        // Invalid range for this document is a per-file error and writes nothing.
        string none = Path.Combine(folder, "none");
        BatchOutcome bad = Run(ConvertOperation.PdfToImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = none, PageRange = "2-9" }, pdf);
        Assert(bad.Failed == 1 && bad.Files[0].Error!.Contains("9") && (!Directory.Exists(none) || Files(none, "*").Length == 0), "A range beyond the page count fails that file and writes nothing.");

        // Absurd DPI is refused instead of exhausting memory.
        BatchOutcome huge = Run(ConvertOperation.PdfToImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = none, Dpi = 1200 }, MakePdf(Path.Combine(folder, "poster.pdf"), (4000, 4000)));
        Assert(huge.Failed == 1 && huge.Files[0].Error!.Contains("DPI"), "A page too large for the DPI is refused with a hint.");
    }

    private static void PdfToJpg()
    {
        string folder = NewFolder("pdf2jpg");
        string pdf = MakePdf(Path.Combine(folder, "foto.pdf"), A4);
        string low = Path.Combine(folder, "low"), high = Path.Combine(folder, "high");
        Run(ConvertOperation.PdfToImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = low, RasterFormat = RasterFormat.Jpg, JpgQuality = 10, Dpi = 150 }, pdf);
        Run(ConvertOperation.PdfToImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = high, RasterFormat = RasterFormat.Jpg, JpgQuality = 100, Dpi = 150 }, pdf);
        string lowFile = Files(low, "*.jpg").Single(), highFile = Files(high, "*.jpg").Single();
        byte[] header = File.ReadAllBytes(highFile).Take(3).ToArray();
        Assert(header.SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF }), "The output is a real JPEG.");
        Assert(new FileInfo(lowFile).Length < new FileInfo(highFile).Length, "A lower quality gives a smaller file.");
        var (w, h) = Dimensions(highFile);
        Assert(Near(w, A4.W * 150 / 72) && Near(h, A4.H * 150 / 72), "JPEG pixel size matches the DPI.");
        var paper = ReadPixel(highFile, 1100, 1600);
        Assert(paper.R > 245 && paper.G > 245 && paper.B > 245, "The JPEG background is white, not black.");
    }

    private static void PasswordPdf()
    {
        string folder = NewFolder("password");
        string pdf = MakeEncryptedPdf(Path.Combine(folder, "gizli.pdf"), "gizli123");

        // No way to ask: the file fails with a clear message.
        BatchOutcome noPrompt = Run(ConvertOperation.PdfToImages, new ConvertOptions { Dpi = 72 }, pdf);
        Assert(noPrompt.Failed == 1 && noPrompt.Files[0].Error!.Contains("parola", StringComparison.OrdinalIgnoreCase), $"Without a prompt the error mentions the password ({noPrompt.Files[0].Error}).");
        Assert(Files(folder, "*.png").Length == 0, "Nothing is written for a protected PDF.");

        // Wrong password first, then the right one: attempts are numbered.
        var attempts = new List<(string Name, int Attempt)>();
        var answers = new Queue<string?>(new string?[] { "yanlis", "gizli123" });
        PdfPasswordPrompt prompt = (name, attempt) => { attempts.Add((name, attempt)); return answers.Dequeue(); };
        var item = new ConvertItem(pdf);
        var engine = new ConvertEngine(prompt);
        BatchOutcome ok = engine.RunAsync(ConvertOperation.PdfToImages, new[] { item }, new ConvertOptions { Dpi = 72 }, CancellationToken.None).GetAwaiter().GetResult();
        Assert(ok.Failed == 0 && Files(folder, "gizli_s*.png").Length == 2, $"The right password renders both pages ({ok.Files[0].Error}).");
        Assert(attempts.Count == 2 && attempts[0].Attempt == 0 && attempts[1].Attempt == 1 && attempts[0].Name == "gizli.pdf", "The prompt is asked again after a wrong password, with the attempt number.");
        Assert(item.Password == "gizli123", "The working password is remembered on the list item.");

        // A remembered password does not ask again (e.g. second operation on the same list).
        int asked = 0;
        var remember = new ConvertEngine((n, a) => { asked++; return null; });
        BatchOutcome again = remember.RunAsync(ConvertOperation.PdfToImages, new[] { item }, new ConvertOptions { Dpi = 72, Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "again") }, CancellationToken.None).GetAwaiter().GetResult();
        Assert(again.Failed == 0 && asked == 0, "A remembered password is reused without asking.");

        // The user skips the file.
        BatchOutcome skipped = Run(ConvertOperation.PdfToImages, new ConvertOptions { Dpi = 72 }, (n, a) => null, CancellationToken.None, pdf);
        Assert(skipped.Failed == 1 && skipped.Files[0].Error!.Contains("parola", StringComparison.OrdinalIgnoreCase), "Cancelling the prompt skips the file with a message.");

        // A prompt that keeps answering with a wrong password gives up instead of asking forever (both PDF engines).
        foreach (ConvertOperation operation in new[] { ConvertOperation.PdfToImages, ConvertOperation.SplitPdf })
        {
            int wrongAsked = 0;
            BatchOutcome stubborn = Run(operation, new ConvertOptions { Dpi = 72, Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "stubborn") }, (n, a) => { wrongAsked++; return "yanlis"; }, CancellationToken.None, pdf);
            Assert(stubborn.Failed == 1 && wrongAsked is >= 1 and <= PdfRendering.MaxPasswordAttempts && stubborn.Files[0].Error!.Contains("parola", StringComparison.OrdinalIgnoreCase),
                $"{operation}: an endlessly wrong password stops after a few attempts ({wrongAsked}, {stubborn.Files[0].Error}).");
        }

        // PDFsharp-based operations ask too (split) and the probe flags protected files.
        BatchOutcome split = Run(ConvertOperation.SplitPdf, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "split") }, (n, a) => "gizli123", CancellationToken.None, pdf);
        Assert(split.Failed == 0 && Files(Path.Combine(folder, "split"), "*.pdf").Length == 2, $"Splitting a protected PDF works after entering the password ({split.Files[0].Error}).");
        foreach (PdfDefaultEncryption encryption in new[] { PdfDefaultEncryption.V4UsingAES, PdfDefaultEncryption.V4UsingRC4 })
        {
            string variant = MakeEncryptedPdf(Path.Combine(folder, "var-" + encryption + ".pdf"), "sifre-şğ", encryption);
            BatchOutcome viaWinRt = Run(ConvertOperation.PdfToImages, new ConvertOptions { Dpi = 72, Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "v-" + encryption) }, (n, a) => "sifre-şğ", CancellationToken.None, variant);
            Assert(viaWinRt.Failed == 0, $"{encryption}: Windows.Data.Pdf opens it with the password ({viaWinRt.Files[0].Error}).");
            BatchOutcome viaSharp = Run(ConvertOperation.ExtractPages, new ConvertOptions { PageRange = "1", Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "w-" + encryption) }, (n, a) => "sifre-şğ", CancellationToken.None, variant);
            Assert(viaSharp.Failed == 0, $"{encryption}: PDFsharp opens it with the password ({viaSharp.Files[0].Error}).");
        }
        var probe = PdfEditing.Probe(pdf);
        Assert(probe.NeedsPassword && probe.Pages == 0, "The list probe reports a protected PDF instead of failing.");
        Assert(PdfEditing.Probe(MakePdf(Path.Combine(folder, "acik.pdf"), A4, A4)).Pages == 2, "The list probe reads the page count of normal PDFs.");
    }

    private static void ErrorsAndCancel()
    {
        string folder = NewFolder("errors");
        string good1 = MakePdf(Path.Combine(folder, "bir.pdf"), A4);
        string broken = Path.Combine(folder, "bozuk.pdf");
        File.WriteAllText(broken, "bu bir PDF degil");
        string good2 = MakePdf(Path.Combine(folder, "iki.pdf"), A4, A4);
        BatchOutcome outcome = Run(ConvertOperation.PdfToImages, new ConvertOptions { Dpi = 72 }, good1, broken, good2);
        Assert(outcome.Files.Count == 3 && outcome.Succeeded == 2 && outcome.Failed == 1, "One bad file does not stop the others.");
        Assert(outcome.Files[1].Error is { Length: > 0 }, "The bad file carries an error message.");
        Assert(File.Exists(Path.Combine(folder, "bir_s1.png")) && File.Exists(Path.Combine(folder, "iki_s2.png")), "Files before and after the bad one were converted.");

        // Page edit operations isolate errors as well.
        BatchOutcome split = Run(ConvertOperation.SplitPdf, new ConvertOptions(), good1, broken);
        Assert(split.Succeeded == 1 && split.Failed == 1, "Split isolates errors per file.");

        // Pre-cancelled token.
        using var already = new CancellationTokenSource();
        already.Cancel();
        BatchOutcome pre = Run(ConvertOperation.PdfToImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "pre") }, null, already.Token, good1);
        Assert(pre.WasCancelled && !pre.Outputs.Any(), "A cancelled batch reports cancellation and writes nothing.");

        // Cancel in the middle of a multi-page PDF.
        string many = MakePdf(Path.Combine(folder, "cok.pdf"), A4, A4, A4, A4, A4, A4, A4, A4);
        string outFolder = Path.Combine(folder, "mid");
        using var cts = new CancellationTokenSource();
        BatchOutcome mid = Run(ConvertOperation.PdfToImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = outFolder, Dpi = 150 }, null, cts.Token,
            (fraction, status) => { if (status.Contains("sayfa 3/")) cts.Cancel(); }, many);
        int written = Directory.Exists(outFolder) ? Files(outFolder, "*.png").Length : 0;
        Assert(mid.WasCancelled && written >= 2 && written < 8, $"Cancelling mid-way stops early ({written} of 8 pages written).");
        Assert(!Directory.Exists(outFolder) || Directory.GetFiles(outFolder, ".*").Length == 0, "No temp files remain after cancellation.");
    }

    // ---------- images -> PDF, merge, split, page edits ----------

    private static void ImagesToPdf()
    {
        string folder = NewFolder("img2pdf");
        string wide = MakeImage(Path.Combine(folder, "genis.png"), 400, 200, Gradient);
        string tall = MakeImage(Path.Combine(folder, "uzun.jpg"), 150, 300, Gradient);
        string alpha = MakeImage(Path.Combine(folder, "seffaf.png"), 120, 120, (x, y) => (10, 200, 30, (byte)(x < 60 ? 0 : 255)));
        string output = Path.Combine(folder, "cikti");

        BatchOutcome a4 = Run(ConvertOperation.ImagesToPdf, new ConvertOptions { PageFit = PageFit.A4, Location = OutputLocation.CustomFolder, OutputFolder = output, OutputName = "kitap" }, wide, tall, alpha);
        string pdf = Path.Combine(output, "kitap.pdf");
        Assert(a4.Failed == 0 && File.Exists(pdf), $"Three images make one PDF ({string.Join(";", a4.Files.Select(f => f.Error))}).");
        List<(double W, double H, int Rotate)> pages = ReadPdf(pdf);
        Assert(pages.Count == 3, $"Page count equals image count (got {pages.Count}).");
        Assert(pages[0].W > pages[0].H && Near(pages[0].W, 841.89, 0.1), "Page 1 (wide image) is landscape A4.");
        Assert(pages[1].H > pages[1].W && Near(pages[1].H, 841.89, 0.1), "Page 2 (tall image) is portrait A4.");
        Assert(pages[2].H > pages[2].W, "Page 3 (square image) is portrait A4.");

        // Order follows the list order.
        string reversedOut = Path.Combine(folder, "ters");
        Run(ConvertOperation.ImagesToPdf, new ConvertOptions { PageFit = PageFit.A4, Location = OutputLocation.CustomFolder, OutputFolder = reversedOut }, tall, wide);
        List<(double W, double H, int Rotate)> reversed = ReadPdf(Files(reversedOut, "*.pdf").Single());
        Assert(reversed.Count == 2 && reversed[0].H > reversed[0].W && reversed[1].W > reversed[1].H, "Page order is the list order.");
        Assert(Path.GetFileName(Files(reversedOut, "*.pdf").Single()) == "uzun_birlesik.pdf", "The default name comes from the first image.");

        // Original size: 96 DPI image -> points.
        string originalOut = Path.Combine(folder, "orijinal");
        Run(ConvertOperation.ImagesToPdf, new ConvertOptions { PageFit = PageFit.Original, Location = OutputLocation.CustomFolder, OutputFolder = originalOut }, wide);
        var original = ReadPdf(Files(originalOut, "*.pdf").Single());
        Assert(original.Count == 1 && Near(original[0].W, 300, 0.2) && Near(original[0].H, 150, 0.2), $"Original size keeps pixels at 96 DPI (got {original[0].W:0.0}x{original[0].H:0.0} pt).");
        Assert(Path.GetFileName(Files(originalOut, "*.pdf").Single()) == "genis.pdf", "A single image keeps its own name.");

        // The PDF renders back to an image (embedded images are intact).
        string renderOut = Path.Combine(folder, "render");
        Run(ConvertOperation.PdfToImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = renderOut, Dpi = 72, PageRange = "2" }, pdf);
        var render = Files(renderOut, "*.png").Single();
        var center = ReadPixel(render, 297, 421);
        Assert(center.R == 90 || Math.Abs(center.R - 90) < 12, $"The embedded image is visible in the rendered PDF (R={center.R}).");

        // A broken image is skipped and reported; the rest still make a PDF.
        string broken = Path.Combine(folder, "bozuk.png");
        File.WriteAllText(broken, "png degil");
        string partialOut = Path.Combine(folder, "kismi");
        BatchOutcome partial = Run(ConvertOperation.ImagesToPdf, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = partialOut }, wide, broken, tall);
        Assert(partial.Files[1].Error is { Length: > 0 } && partial.Files[0].Error is null && partial.Files[2].Error is null, "Only the broken image is flagged.");
        Assert(ReadPdf(Files(partialOut, "*.pdf").Single()).Count == 2, "The remaining images still produce a PDF.");
        Assert(partial.Files.Last().Warnings.Count == 1, "The result warns that an image was left out.");
    }

    private static void MergeAndSplit()
    {
        string folder = NewFolder("mergesplit");
        string a = MakePdf(Path.Combine(folder, "a.pdf"), (200, 300), (210, 310), (220, 320));
        string b = MakePdf(Path.Combine(folder, "b.pdf"), (400, 500), (410, 510));
        string output = Path.Combine(folder, "merge");

        BatchOutcome merged = Run(ConvertOperation.MergePdf, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = output }, a, b);
        Assert(merged.Failed == 0, "Merging two PDFs succeeds.");
        List<(double W, double H, int Rotate)> pages = ReadPdf(Files(output, "*.pdf").Single());
        Assert(pages.Count == 5, $"3 + 2 pages merge into 5 pages (got {pages.Count}).");
        Assert(pages.Select(p => Math.Round(p.W)).SequenceEqual(new[] { 200.0, 210, 220, 400, 410 }), "Pages keep the list order across files.");
        Assert(Path.GetFileName(Files(output, "*.pdf").Single()) == "a_birlesik.pdf", "The merged file is named after the first PDF.");

        string reversedOut = Path.Combine(folder, "merge2");
        Run(ConvertOperation.MergePdf, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = reversedOut, OutputName = "hepsi.pdf" }, b, a);
        Assert(ReadPdf(Path.Combine(reversedOut, "hepsi.pdf")).Select(p => Math.Round(p.W)).SequenceEqual(new[] { 400.0, 410, 200, 210, 220 }), "Reordering the list reorders the merge; the custom name is used.");

        BatchOutcome one = Run(ConvertOperation.MergePdf, new ConvertOptions(), a);
        Assert(one.Failed == 1 && !one.Outputs.Any(), "Merging a single PDF is refused with an error and writes nothing.");

        // A broken input is reported but the rest are merged.
        string broken = Path.Combine(folder, "bozuk.pdf");
        File.WriteAllText(broken, "degil");
        string partialOut = Path.Combine(folder, "merge3");
        BatchOutcome partial = Run(ConvertOperation.MergePdf, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = partialOut }, a, broken, b);
        Assert(partial.Files[1].Error is not null && ReadPdf(Files(partialOut, "*.pdf").Single()).Count == 5, "A broken PDF is skipped with an error; the others merge.");
        Assert(partial.Files.Last().Warnings.Count == 1, "The merge result warns about the skipped file.");

        // Split: each page.
        string eachOut = Path.Combine(folder, "split-each");
        BatchOutcome each = Run(ConvertOperation.SplitPdf, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = eachOut, SplitMode = SplitMode.EachPage }, a);
        string[] parts = Files(eachOut, "*.pdf");
        Assert(each.Failed == 0 && parts.Length == 3, "Splitting 3 pages gives 3 files.");
        Assert(parts.Select(Path.GetFileName).SequenceEqual(new[] { "a_s1.pdf", "a_s2.pdf", "a_s3.pdf" }), "Parts are named {ad}_s{sayfa}.pdf.");
        Assert(parts.All(p => ReadPdf(p).Count == 1) && Math.Round(ReadPdf(parts[1])[0].W) == 210, "Each part holds exactly its page.");

        // Split: by ranges.
        string rangeOut = Path.Combine(folder, "split-range");
        Run(ConvertOperation.SplitPdf, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = rangeOut, SplitMode = SplitMode.Ranges, SplitRanges = "1-2,3" }, a);
        string[] rangeParts = Files(rangeOut, "*.pdf");
        Assert(rangeParts.Select(Path.GetFileName).SequenceEqual(new[] { "a_s1-2.pdf", "a_s3.pdf" }), $"Range parts are named after their pages ({string.Join(",", rangeParts.Select(Path.GetFileName))}).");
        Assert(ReadPdf(rangeParts[0]).Count == 2 && ReadPdf(rangeParts[1]).Count == 1, "Range parts hold 2 and 1 pages.");

        BatchOutcome badRange = Run(ConvertOperation.SplitPdf, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "bad"), SplitMode = SplitMode.Ranges, SplitRanges = "1-9" }, a);
        Assert(badRange.Failed == 1, "A split range beyond the page count is an error.");
        BatchOutcome emptyRange = Run(ConvertOperation.SplitPdf, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "bad2"), SplitMode = SplitMode.Ranges, SplitRanges = "" }, a);
        Assert(emptyRange.Failed == 1, "Splitting by ranges without ranges is an error.");
    }

    private static void EditPages()
    {
        string folder = NewFolder("edit");
        string source = MakePdf(Path.Combine(folder, "kaynak.pdf"), (200, 300), (210, 310), (220, 320), (230, 330));
        string output = Path.Combine(folder, "o");
        ConvertOptions Options(string pages, int degrees = 90) => new() { Location = OutputLocation.CustomFolder, OutputFolder = output, PageRange = pages, RotateDegrees = degrees };

        Run(ConvertOperation.ExtractPages, Options("3,1"), source);
        List<(double W, double H, int Rotate)> extracted = ReadPdf(Path.Combine(output, "kaynak_secili.pdf"));
        Assert(extracted.Select(p => Math.Round(p.W)).SequenceEqual(new[] { 220.0, 200 }), "Extract keeps the requested pages in the requested order.");

        Run(ConvertOperation.DeletePages, Options("2-3"), source);
        Assert(ReadPdf(Path.Combine(output, "kaynak_duzenlendi.pdf")).Select(p => Math.Round(p.W)).SequenceEqual(new[] { 200.0, 230 }), "Delete removes exactly the listed pages.");

        BatchOutcome all = Run(ConvertOperation.DeletePages, Options("1-4"), source);
        Assert(all.Failed == 1 && all.Files[0].Error!.Contains("en az bir sayfa"), "Deleting every page is refused.");

        Run(ConvertOperation.RotatePages, Options("2", 90), source);
        List<(double W, double H, int Rotate)> rotated = ReadPdf(Path.Combine(output, "kaynak_dondurulmus.pdf"));
        Assert(rotated.Count == 4 && rotated.Select(p => p.Rotate).SequenceEqual(new[] { 0, 90, 0, 0 }), $"Rotate turns only the chosen page (got {string.Join(",", rotated.Select(p => p.Rotate))}).");

        string rotateAllOut = Path.Combine(folder, "all");
        Run(ConvertOperation.RotatePages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = rotateAllOut, PageRange = "", RotateDegrees = 270 }, source);
        Assert(ReadPdf(Files(rotateAllOut, "*.pdf").Single()).All(p => p.Rotate == 270), "An empty page range rotates every page.");

        // Rotating an already rotated file adds up.
        string twice = Path.Combine(folder, "twice");
        Run(ConvertOperation.RotatePages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = twice, RotateDegrees = 180 }, Path.Combine(rotateAllOut, Files(rotateAllOut, "*.pdf").Single()));
        Assert(ReadPdf(Files(twice, "*.pdf").Single()).All(p => p.Rotate == 90), "270 + 180 degrees wraps to 90.");

        Assert(Run(ConvertOperation.ExtractPages, Options(""), source).Failed == 1, "Extract needs a page list.");
        Assert(Run(ConvertOperation.ExtractPages, Options("9"), source).Failed == 1, "Extract rejects pages beyond the end.");

        // The rotated page renders with swapped dimensions.
        string renderOut = Path.Combine(folder, "render");
        Run(ConvertOperation.PdfToImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = renderOut, Dpi = 72, PageRange = "2" }, Path.Combine(output, "kaynak_dondurulmus.pdf"));
        var (w, h) = Dimensions(Files(renderOut, "*.png").Single());
        Assert(Near(w, 310, 2) && Near(h, 210, 2), $"A page rotated by 90 degrees renders landscape (got {w}x{h}).");
    }

    // ---------- image conversion ----------

    private static void ConvertFormats()
    {
        string folder = NewFolder("formats");
        string png = MakeImage(Path.Combine(folder, "kaynak.png"), 400, 200, (x, y) => ((byte)(x * 255 / 400), 60, 200, (byte)(y < 100 ? 0 : 255)));
        string output = Path.Combine(folder, "o");
        ConvertOptions Options(ImageTarget target) => new() { Location = OutputLocation.CustomFolder, OutputFolder = output, TargetFormat = target, JpgQuality = 92 };

        foreach (var (target, extension) in new[] { (ImageTarget.Jpg, ".jpg"), (ImageTarget.Bmp, ".bmp"), (ImageTarget.Tiff, ".tif"), (ImageTarget.Gif, ".gif"), (ImageTarget.Png, ".png") })
        {
            BatchOutcome outcome = Run(ConvertOperation.ConvertImages, Options(target), png);
            Assert(outcome.Failed == 0, $"PNG -> {target} succeeds ({outcome.Files[0].Error}).");
            string file = outcome.Files[0].Outputs.Single();
            Assert(file.EndsWith(extension, StringComparison.OrdinalIgnoreCase), $"{target} output has {extension}.");
            Assert(Dimensions(file) == (400, 200), $"{target} keeps the pixel size.");
        }

        string jpg = Directory.GetFiles(output, "kaynak*.jpg").Single();
        var top = ReadPixel(jpg, 200, 10);
        Assert(top.R > 240 && top.G > 240 && top.B > 240, $"Transparent areas become white in JPEG (got R{top.R} G{top.G} B{top.B}).");
        var bottom = ReadPixel(jpg, 380, 180);
        Assert(bottom.R is > 150 and < 230 && bottom.B > 150, "Opaque areas keep their colour in JPEG.");
        var pngOut = Directory.GetFiles(output, "kaynak*.png").Single();
        Assert(ReadPixel(pngOut, 200, 10).A == 0 && ReadPixel(pngOut, 200, 150).A == 255, "PNG output keeps transparency.");

        // Resize by percent and by maximum size.
        string resized = Path.Combine(folder, "r");
        BatchOutcome percent = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = resized, TargetFormat = ImageTarget.Png, ResizeMode = ResizeKind.Percent, ResizePercent = 50 }, png);
        Assert(Dimensions(percent.Files[0].Outputs[0]) == (200, 100), "50 percent gives 200x100.");
        BatchOutcome fit = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = resized, TargetFormat = ImageTarget.Jpg, ResizeMode = ResizeKind.MaxSize, MaxWidth = 100, MaxHeight = 100 }, png);
        Assert(Dimensions(fit.Files[0].Outputs[0]) == (100, 50), "Max 100x100 keeps the 2:1 aspect: 100x50.");
        BatchOutcome small = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = resized, TargetFormat = ImageTarget.Png, ResizeMode = ResizeKind.MaxSize, MaxWidth = 5000, MaxHeight = 5000 }, png);
        Assert(Dimensions(small.Files[0].Outputs[0]) == (400, 200), "Max size never enlarges.");
        BatchOutcome badPercent = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = resized, ResizeMode = ResizeKind.Percent, ResizePercent = 0 }, png);
        Assert(badPercent.Failed == 1, "A zero percent is rejected.");

        // Reading other formats back: BMP -> PNG, TIFF -> JPG.
        string bmp = Directory.GetFiles(output, "*.bmp").Single();
        BatchOutcome fromBmp = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "b"), TargetFormat = ImageTarget.Png }, bmp);
        Assert(fromBmp.Failed == 0, "BMP can be read.");
        string tif = Directory.GetFiles(output, "*.tif").Single();
        Assert(Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "t"), TargetFormat = ImageTarget.Jpg }, tif).Failed == 0, "TIFF can be read.");

        // A multi-frame GIF converts its first frame and says so.
        string gif = Path.Combine(folder, "animasyon.gif");
        var encoder = new GifBitmapEncoder();
        for (int i = 0; i < 2; i++)
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr24, null, Enumerable.Repeat((byte)(i * 200), 8 * 8 * 3).ToArray(), 8 * 3)));
        using (var stream = File.Create(gif)) encoder.Save(stream);
        BatchOutcome animated = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "g"), TargetFormat = ImageTarget.Png }, gif);
        Assert(animated.Failed == 0 && animated.Files[0].Warnings.Any(w => w.Contains("ilk kare")), "A GIF with several frames converts the first frame and warns.");
    }

    private static void MetadataAndOrientation()
    {
        string folder = NewFolder("meta");
        var metadata = new BitmapMetadata("jpg") { CameraModel = "TestKamera 9000", Title = "Gizli başlık" };
        string jpg = MakeImage(Path.Combine(folder, "foto.jpg"), 64, 48, Gradient, new JpegBitmapEncoder { QualityLevel = 95 }, metadata);
        static string? Camera(string path)
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            return (frame.Metadata as BitmapMetadata)?.CameraModel;
        }
        Assert(Camera(jpg) == "TestKamera 9000", "Test setup: the source JPEG carries a camera model.");

        BatchOutcome kept = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "keep"), TargetFormat = ImageTarget.Jpg, StripMetadata = false }, jpg);
        Assert(kept.Failed == 0 && Camera(kept.Files[0].Outputs[0]) == "TestKamera 9000", $"Metadata is kept when not stripping ({kept.Files[0].Error}).");
        BatchOutcome stripped = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "strip"), TargetFormat = ImageTarget.Jpg, StripMetadata = true }, jpg);
        Assert(stripped.Failed == 0 && string.IsNullOrEmpty(Camera(stripped.Files[0].Outputs[0])), "Metadata is removed when stripping.");
        BatchOutcome toPng = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "png"), TargetFormat = ImageTarget.Png, StripMetadata = false }, jpg);
        Assert(toPng.Failed == 0 && File.Exists(toPng.Files[0].Outputs[0]), "Metadata that a target cannot hold never fails the conversion.");

        // Orientation transform on a 2x3 image with distinct pixels.
        byte[] px = new byte[2 * 3 * 4];
        for (int i = 0; i < 6; i++) px[i * 4] = (byte)(i + 1); // pixel index (row-major) in the blue channel
        int At((int W, int H, byte[] P) img, int x, int y) => img.P[(y * img.W + x) * 4];
        var o2 = ImageProcessing.ApplyOrientation(px, 2, 3, 2);
        Assert((o2.Width, o2.Height) == (2, 3) && At(o2, 0, 0) == 2 && At(o2, 1, 0) == 1, "Orientation 2 flips horizontally.");
        var o3 = ImageProcessing.ApplyOrientation(px, 2, 3, 3);
        Assert(At(o3, 0, 0) == 6 && At(o3, 1, 2) == 1, "Orientation 3 rotates 180 degrees.");
        var o4 = ImageProcessing.ApplyOrientation(px, 2, 3, 4);
        Assert(At(o4, 0, 0) == 5 && At(o4, 0, 2) == 1, "Orientation 4 flips vertically.");
        var o5 = ImageProcessing.ApplyOrientation(px, 2, 3, 5);
        Assert((o5.Width, o5.Height) == (3, 2) && At(o5, 0, 0) == 1 && At(o5, 1, 0) == 3 && At(o5, 0, 1) == 2, "Orientation 5 transposes.");
        var o6 = ImageProcessing.ApplyOrientation(px, 2, 3, 6);
        Assert((o6.Width, o6.Height) == (3, 2) && At(o6, 0, 0) == 5 && At(o6, 2, 0) == 1 && At(o6, 0, 1) == 6 && At(o6, 2, 1) == 2, "Orientation 6 rotates 90 degrees clockwise.");
        var o7 = ImageProcessing.ApplyOrientation(px, 2, 3, 7);
        Assert((o7.Width, o7.Height) == (3, 2) && At(o7, 0, 0) == 6 && At(o7, 2, 1) == 1, "Orientation 7 transverses.");
        var o8 = ImageProcessing.ApplyOrientation(px, 2, 3, 8);
        Assert((o8.Width, o8.Height) == (3, 2) && At(o8, 0, 0) == 2 && At(o8, 2, 0) == 6 && At(o8, 0, 1) == 1, "Orientation 8 rotates 90 degrees counter-clockwise.");
        Assert(ReferenceEquals(ImageProcessing.ApplyOrientation(px, 2, 3, 1).Pixels, px), "Orientation 1 returns the pixels untouched.");

        // End to end: a JPEG tagged "rotate 90 CW" (left half red, right half blue) comes out upright: red on top, blue at the bottom.
        var tagged = new BitmapMetadata("jpg");
        bool tagWritten = true;
        try { tagged.SetQuery("/app1/ifd/{ushort=274}", (ushort)6); } catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { tagWritten = false; }
        Assert(tagWritten, "Test setup: the EXIF orientation tag can be written.");
        string rotatedSource = MakeImage(Path.Combine(folder, "yatik.jpg"), 40, 20, (x, y) => x < 20 ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)255, (byte)0, (byte)0, (byte)255),
            new JpegBitmapEncoder { QualityLevel = 100 }, tagged);
        foreach (bool strip in new[] { false, true })
        {
            BatchOutcome upright = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "up" + strip), TargetFormat = ImageTarget.Jpg, StripMetadata = strip, JpgQuality = 100 }, rotatedSource);
            string file = upright.Files[0].Outputs[0];
            Assert(Dimensions(file) == (20, 40), $"The 40x20 image tagged orientation 6 is saved as 20x40 (strip={strip}).");
            var top = ReadPixel(file, 10, 5);
            var bottom = ReadPixel(file, 10, 35);
            Assert(top.R > 200 && top.B < 60 && bottom.B > 200 && bottom.R < 60, $"Rotated pixels are in the right place (strip={strip}).");
            using var stream = File.OpenRead(file);
            var meta = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0].Metadata as BitmapMetadata;
            object? tag = null;
            try { tag = meta?.GetQuery("/app1/ifd/{ushort=274}"); } catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException) { }
            Assert(tag is null || System.Convert.ToInt32(tag) == 1, "The orientation tag does not rotate the image a second time.");
        }
    }

    private static void Resampling()
    {
        byte[] flat = new byte[100 * 100 * 4];
        for (int i = 0; i < flat.Length; i += 4) { flat[i] = 10; flat[i + 1] = 20; flat[i + 2] = 30; flat[i + 3] = 255; }
        foreach (var (w, h) in new[] { (33, 37), (200, 150), (1, 1), (100, 7) })
        {
            byte[] result = ImageProcessing.Resample(flat, 100, 100, w, h);
            bool exact = true;
            for (int i = 0; i < result.Length; i += 4) exact &= result[i] == 10 && result[i + 1] == 20 && result[i + 2] == 30 && result[i + 3] == 255;
            Assert(result.Length == w * h * 4 && exact, $"A flat colour stays exactly the same at {w}x{h}.");
        }
        byte[] checker = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                byte v = (byte)((x + y) % 2 == 0 ? 0 : 255);
                int i = (y * 64 + x) * 4;
                checker[i] = checker[i + 1] = checker[i + 2] = v; checker[i + 3] = 255;
            }
        byte[] gray = ImageProcessing.Resample(checker, 64, 64, 8, 8);
        Assert(Enumerable.Range(0, 64).All(p => Math.Abs(gray[p * 4] - 127.5) <= 6), "A fine checkerboard shrinks to mid grey (area averaging, no aliasing).");
        byte[] ramp = new byte[2 * 2 * 4];
        ramp[0] = ramp[4] = 0; ramp[8] = ramp[12] = 200;
        for (int i = 3; i < ramp.Length; i += 4) ramp[i] = 255;
        byte[] enlarged = ImageProcessing.Resample(ramp, 2, 2, 2, 8);
        bool monotonic = true;
        for (int y = 1; y < 8; y++) monotonic &= enlarged[(y * 2) * 4] >= enlarged[((y - 1) * 2) * 4];
        Assert(monotonic && enlarged[0] < 60 && enlarged[7 * 2 * 4] > 140, "Enlarging a ramp stays smooth and monotonic.");
    }

    private static void PngCompressionLevels()
    {
        string folder = NewFolder("pngc");
        var random = new Random(3);
        string source = MakeImage(Path.Combine(folder, "desen.png"), 300, 300, (x, y) => ((byte)((x / 7 + y / 5) % 256), (byte)(x % 50 * 5), (byte)(random.Next(4) * 60), 255));
        long Size(PngCompression level, out string path)
        {
            BatchOutcome outcome = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, level.ToString()), TargetFormat = ImageTarget.Png, PngCompression = level }, source);
            Assert(outcome.Failed == 0, $"PNG {level} converts ({outcome.Files[0].Error}).");
            path = outcome.Files[0].Outputs[0];
            return new FileInfo(path).Length;
        }
        long fast = Size(PngCompression.Fast, out string fastFile), balanced = Size(PngCompression.Balanced, out string balancedFile), smallest = Size(PngCompression.Smallest, out string smallestFile);
        Assert(smallest <= balanced && balanced <= fast, $"Higher compression is not larger ({fast} / {balanced} / {smallest} bytes).");
        Assert(smallest < fast, "The smallest setting beats the fast one on this image.");
        foreach (string file in new[] { fastFile, balancedFile, smallestFile })
            Assert(ReadPixel(file, 123, 45) == ReadPixel(source, 123, 45) && ReadPixel(file, 299, 299) == ReadPixel(source, 299, 299), "Recompressed PNGs decode to the same pixels.");
        Assert(PngRecompressor.Recompress(new byte[] { 1, 2, 3 }, System.IO.Compression.CompressionLevel.SmallestSize).Length == 3, "Garbage input is returned unchanged.");
    }

    private static void IconOutput()
    {
        string folder = NewFolder("ico");
        string source = MakeImage(Path.Combine(folder, "logo.png"), 400, 200, (x, y) => (0, 0, 255, 255));
        int[] sizes = { 16, 32, 48, 64, 128, 256 };
        BatchOutcome outcome = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "o"), TargetFormat = ImageTarget.Ico, IconSizes = sizes.ToList() }, source);
        Assert(outcome.Failed == 0, $"ICO output succeeds ({outcome.Files[0].Error}).");
        string ico = outcome.Files[0].Outputs.Single();
        byte[] bytes = File.ReadAllBytes(ico);
        Assert(bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0 && bytes[4] == sizes.Length, "ICONDIR header: type 1 with 6 images.");
        using var stream = File.OpenRead(ico);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert(decoder.Frames.Count == sizes.Length, $"The icon holds {sizes.Length} images (got {decoder.Frames.Count}).");
        Assert(decoder.Frames.Select(f => f.PixelWidth).OrderBy(w => w).SequenceEqual(sizes), "Every requested size is present.");
        Assert(decoder.Frames.All(f => f.PixelWidth == f.PixelHeight), "Icon images are square.");
        BitmapFrame frame64 = decoder.Frames.Single(f => f.PixelWidth == 64);
        var converted = new FormatConvertedBitmap(frame64, PixelFormats.Bgra32, null, 0);
        byte[] top = new byte[4], middle = new byte[4];
        converted.CopyPixels(new Int32Rect(32, 2, 1, 1), top, 4, 0);
        converted.CopyPixels(new Int32Rect(32, 32, 1, 1), middle, 4, 0);
        Assert(top[3] == 0, "A 2:1 source is letterboxed: the top edge is transparent.");
        Assert(middle[3] == 255 && middle[2] > 200 && middle[0] < 60, "The picture sits in the middle of the square.");

        // The icon can be read as an image again (largest frame).
        BatchOutcome back = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "back"), TargetFormat = ImageTarget.Png }, ico);
        Assert(back.Failed == 0 && Dimensions(back.Files[0].Outputs[0]) == (256, 256), "Reading an icon picks its largest image.");

        BatchOutcome none = Run(ConvertOperation.ConvertImages, new ConvertOptions { Location = OutputLocation.CustomFolder, OutputFolder = Path.Combine(folder, "none"), TargetFormat = ImageTarget.Ico, IconSizes = new List<int>() }, source);
        Assert(none.Failed == 1, "An icon without sizes is an error.");
    }

    private static void Unreadable()
    {
        string folder = NewFolder("bad");
        string fakeWebp = Path.Combine(folder, "ornek.webp");
        File.WriteAllText(fakeWebp, "RIFFxxxxWEBPbozuk");
        BatchOutcome webp = Run(ConvertOperation.ConvertImages, new ConvertOptions { TargetFormat = ImageTarget.Png }, fakeWebp);
        Assert(webp.Failed == 1 && webp.Files[0].Error!.Contains("WebP"), $"A WebP that cannot be decoded names WebP and the codec ({webp.Files[0].Error}).");
        Assert(!File.Exists(Path.Combine(folder, "ornek.png")), "Nothing is written for an unreadable image.");

        string fakePng = Path.Combine(folder, "bozuk.png");
        File.WriteAllText(fakePng, "png degil");
        BatchOutcome png = Run(ConvertOperation.ConvertImages, new ConvertOptions { TargetFormat = ImageTarget.Jpg }, fakePng);
        Assert(png.Failed == 1 && png.Files[0].Error!.Contains(".png"), "A damaged PNG gives a Turkish message naming the type.");
        Assert(Run(ConvertOperation.ConvertImages, new ConvertOptions(), Path.Combine(folder, "yok.png")).Failed == 1, "A missing file is an error, not a crash.");
        Assert(ImageProcessing.DescribeDecodeFailure(".webp").Contains("Microsoft Store"), "The WebP hint tells where to get the codec.");
        Assert(ImageProcessing.Probe(fakePng) is null, "The list probe returns null for undecodable files.");
        Assert(!new ConvertItem(fakeWebp).Path.Contains(".."), "Items keep full paths.");
        try { _ = new ConvertItem(Path.Combine(folder, "a.docx")); Assert(false, "Unsupported types are rejected."); }
        catch (NotSupportedException) { assertions++; }
    }

    private static void NoOverwrite()
    {
        string folder = NewFolder("nooverwrite");
        string png = MakeImage(Path.Combine(folder, "resim.png"), 50, 50, Gradient);
        string pdf = MakePdf(Path.Combine(folder, "belge.pdf"), A4, A4);
        string Hash(string path) => Convert_Hash(File.ReadAllBytes(path));

        var options = new ConvertOptions { TargetFormat = ImageTarget.Png };
        BatchOutcome first = Run(ConvertOperation.ConvertImages, options, png);
        Assert(Path.GetFileName(first.Files[0].Outputs[0]) == "resim (2).png", "Converting PNG to PNG next to the source does not touch the original (\"resim (2).png\").");
        BatchOutcome second = Run(ConvertOperation.ConvertImages, options, png);
        Assert(Path.GetFileName(second.Files[0].Outputs[0]) == "resim (3).png", "A second run takes the next free number.");
        string originalHash = Hash(png);
        Assert(File.ReadAllBytes(png).Length > 0 && Hash(png) == originalHash, "The source image is unchanged.");

        var pdfOptions = new ConvertOptions { Dpi = 72 };
        Run(ConvertOperation.PdfToImages, pdfOptions, pdf);
        string original = Path.Combine(folder, "belge_s1.png");
        string before = Hash(original);
        BatchOutcome again = Run(ConvertOperation.PdfToImages, pdfOptions, pdf);
        Assert(again.Files[0].Outputs.Select(Path.GetFileName).SequenceEqual(new[] { "belge_s1 (2).png", "belge_s2 (2).png" }), "A repeated PDF export gets new numbered names.");
        Assert(Hash(original) == before, "The first export is untouched.");

        BatchOutcome splitOne = Run(ConvertOperation.SplitPdf, new ConvertOptions(), pdf);
        BatchOutcome splitTwo = Run(ConvertOperation.SplitPdf, new ConvertOptions(), pdf);
        Assert(splitOne.Outputs.Intersect(splitTwo.Outputs, StringComparer.OrdinalIgnoreCase).Count() == 0, "Repeated splits never reuse a path.");
        Assert(Directory.GetFiles(folder, ".*").Length == 0 && Directory.GetFiles(folder, "*.tmp").Length == 0, "No temp files are left in the output folder.");
    }

    private static string Convert_Hash(byte[] data) => System.Convert.ToHexString(SHA256.HashData(data));

    // ---------- module ----------

    private sealed class TestHost : IToolHost
    {
        public TestHost(string moduleId, string dataRoot)
        {
            ModuleId = moduleId;
            DataRoot = dataRoot;
            Settings = new ModuleSettings(Path.Combine(dataRoot, "modules", moduleId, "settings.json"));
            Messages = new MessageBus(dispatcher: null);
            Jobs = new JobTracker { FinishedJobLinger = TimeSpan.Zero };
            ExternalTools = new ExternalToolManager(Path.Combine(dataRoot, "tools"), new ThrowingHandler());
            Dispatcher = Dispatcher.CurrentDispatcher;
        }

        public string ModuleId { get; }
        public string DataRoot { get; }
        public string DataFolder => Directory.CreateDirectory(Path.Combine(DataRoot, "modules", ModuleId, "data")).FullName;
        public ModuleSettings Settings { get; }
        public IMessageBus Messages { get; }
        public IJobTracker Jobs { get; }
        public IExternalToolManager ExternalTools { get; }
        public Dispatcher Dispatcher { get; }
        public List<string> Navigations { get; } = new();
        public List<ToastOptions> Toasts { get; } = new();
        public void Navigate(string moduleId) => Navigations.Add(moduleId);
        public void ShowToast(ToastOptions toast) => Toasts.Add(toast);
        public HotkeyRegistration RegisterHotkey(HotkeyGesture gesture, Action callback) => HotkeyRegistration.Failed(gesture, "test");
        public void Log(string message, Exception? exception = null) { }
    }

    private sealed class ThrowingHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Ağ kullanılmamalı.");
    }

    private static void ModuleSmoke()
    {
        string folder = NewFolder("module");
        string profile = Path.Combine(folder, "profile");
        Application app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemeResources.Load());

        var module = new ConvertModule();
        Assert(module.Id == "convert" && module.Title == "Dönüştür" && DataPaths.IsValidId(module.Id), "Module identity.");
        Assert(module.Category == ToolCategories.Files && module.Icon.Length == 1 && module.Description.Length > 10, "Module description and category.");
        string[] expected = { "PDF'i PNG'ye çevir", "Görselleri PDF yap", "PDF birleştir", "PDF böl", "Görsel formatını değiştir" };
        Assert(module.Commands.Select(c => c.Title).SequenceEqual(expected), "The five palette commands exist with the requested titles.");
        Assert(module.Commands.All(c => c.NavigateToModuleId == "convert" && c.Execute is not null && !string.IsNullOrEmpty(c.Id) && c.Keywords.Count > 0), "Every command navigates to the page and runs an action.");
        Assert(module.Commands.Select(c => c.Id).Distinct().Count() == 5, "Command ids are unique.");
        Assert(SearchMatcher.Score("donustur", module.Title, module.Keywords, module.Description) > 0, "The palette finds the module with Turkish-insensitive search.");

        var host = new TestHost("convert", profile);
        module.Initialize(host);
        FrameworkElement element = module.CreateView();
        Assert(element is UserControl, "CreateView returns the page.");
        var view = (ConvertView)element;
        var fileList = (ItemsControl)view.FindName("FileList");
        var operationBox = (ComboBox)view.FindName("OperationBox");
        Assert(fileList.Items.Count == 0 && operationBox.Items.Count == Enum.GetValues<ConvertOperation>().Length, "An empty page lists every operation.");

        // Shared message: navigates and prefills.
        string pdf1 = MakePdf(Path.Combine(folder, "bir.pdf"), A4, A4);
        string pdf2 = MakePdf(Path.Combine(folder, "iki.pdf"), A4);
        string note = Path.Combine(folder, "not.txt");
        File.WriteAllText(note, "x");
        host.Messages.Publish(new FilesToConvertMessage(new[] { pdf1, pdf2, note, pdf1 }));
        Assert(host.Navigations.SequenceEqual(new[] { "convert" }), "The message navigates to the convert page.");
        Assert(fileList.Items.Count == 2 && fileList.Items.Cast<ConvertItem>().Select(i => i.Name).SequenceEqual(new[] { "bir.pdf", "iki.pdf" }), "Supported files are queued once; unsupported ones are skipped.");
        var operations = operationBox.Items.Cast<ComboBoxItem>().Select(i => (ConvertOperation)i.Tag).ToList();
        Assert(operations.Contains(ConvertOperation.MergePdf) && !operations.Contains(ConvertOperation.ConvertImages), "With PDFs in the list only PDF operations are offered.");
        Assert(((ConvertOperation)((ComboBoxItem)operationBox.SelectedItem).Tag) == ConvertOperation.PdfToImages, "PDFs preselect PDF -> PNG/JPG.");

        // Palette-style preselection (without the file dialog).
        Assert(view.SelectOperation(ConvertOperation.MergePdf) && ((ConvertOperation)((ComboBoxItem)operationBox.SelectedItem).Tag) == ConvertOperation.MergePdf, "A command preselects its operation.");
        Assert(fileList.Items.Count == 2, "Compatible files stay when an operation is preselected.");
        Assert(view.SelectOperation(ConvertOperation.ConvertImages) && fileList.Items.Count == 0, "Preselecting an image operation drops incompatible PDFs.");

        // Images arriving through the message replace a PDF list.
        string image = MakeImage(Path.Combine(folder, "resim.png"), 30, 30, Gradient);
        view.AddFilesFromOutside(new[] { pdf1 });
        host.Messages.Publish(new FilesToConvertMessage(new[] { image }));
        Assert(fileList.Items.Count == 1 && ((ConvertItem)fileList.Items[0]!).Kind == FileKind.Image, "Incoming files of another kind replace the old list.");
        Assert(((ConvertOperation)((ComboBoxItem)operationBox.SelectedItem).Tag) == ConvertOperation.ConvertImages, "A single image selects image conversion.");

        // The page fills the settings file after a run is started with valid options is covered by engine tests; saving works.
        module.Settings.LastOperation = ConvertOperation.MergePdf;
        module.SaveSettings();
        var reloaded = new ModuleSettings(host.Settings.FilePath).Load<ConvertSettings>();
        Assert(reloaded.LastOperation == ConvertOperation.MergePdf && reloaded.Options.IconSizes.Count == 6, "Settings round-trip with defaults for missing values.");

        module.Shutdown();
        host.Messages.Publish(new FilesToConvertMessage(new[] { pdf2 }));
        Assert(fileList.Items.Count == 1, "After Shutdown the module no longer reacts to messages.");

        // A view created after the message was delivered (module page not yet opened) still gets the files.
        var late = new ConvertModule();
        var lateHost = new TestHost("convert", Path.Combine(folder, "profile2"));
        late.Initialize(lateHost);
        lateHost.Messages.Publish(new FilesToConvertMessage(new[] { pdf1 }));
        var lateView = (ConvertView)late.CreateView();
        Assert(((ItemsControl)lateView.FindName("FileList")).Items.Count == 1, "Files sent before the page exists are queued when it is created.");
        late.Shutdown();
    }
}
