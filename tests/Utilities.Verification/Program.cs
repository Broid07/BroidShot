using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BroidShot.Core;
using BroidShot.Modules.Utilities;

// Verification for BroidShot.Modules.Utilities. Temp folders and fakes only: the real clipboard is never touched
// and no overlay window is shown unless --screen-verify is passed.
internal static class Program
{
    private static int assertions;
    private static string root = "";

    [STAThread]
    private static int Main(string[] args)
    {
        root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(DataPaths.DataDirectoryVariable, Path.Combine(root, "profile"));
        // Same inherited font defaults as the app (must run before the first Window exists).
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };   // registers the pack:// scheme
        ThemeResources.ApplyDefaults();

        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("Colour formats: HEX, RGB, HSL, C# Color and parsing", Sync(ColorFormatTests)),
            ("Colour history keeps 50 unique entries, newest first", Sync(ColorHistoryTests)),
            ("Ruler math: distance, Shift snapping, DPI scale", Sync(RulerTests)),
            ("Snippet placeholders with injected clock and clipboard", Sync(SnippetExpansionTests)),
            ("Snippet store: CRUD, validation, search, persistence", Sync(SnippetStoreTests)),
            ("QR generate -> decode round trip on raw bytes", Sync(QrRoundTripTests)),
            ("QR decode from a screen-like crop, PNG file and BitmapSource", Sync(QrImageSourceTests)),
            ("QR decode of several codes on one screen with positions (capture overlay)", Sync(QrMultiTests)),
            ("Colour picks from the capture overlay join the persisted history", Sync(ColorHistoryExternalPickTests)),
            ("Text tools: JSON", Sync(JsonTests)),
            ("Text tools: Base64 and URL", Sync(EncodingTests)),
            ("Text tools: Turkish casing", Sync(CaseTests)),
            ("Text tools: lines, count, hashes, UUID, timestamps", Sync(LineAndMiscTests)),
            ("Batch rename: preview, numbering, regex, case, extension", Sync(RenamePreviewTests)),
            ("Batch rename: conflicts and invalid names are flagged and block apply", Sync(RenameConflictTests)),
            ("Batch rename: apply, swap, undo from a persisted journal", Sync(RenameApplyUndoTests)),
            ("Organize by type: preview, apply, undo", Sync(OrganizeTests)),
            ("Zip create and extract", Sync(ZipRoundTripTests)),
            ("Unzip rejects zip-slip entries and extracts nothing", Sync(ZipSlipTests)),
            ("Modules: ids, category, commands, hotkey, views", Sync(ModuleContractTests)),
            ("Page icon glyphs exist in the icon font", Sync(IconGlyphTests)),
        };
        int renderIndex = Array.IndexOf(args, "--render-views");
        if (renderIndex >= 0 && renderIndex + 1 < args.Length) tests.Add(("Render module pages to PNG (flag)", Sync(() => RenderViews(args[renderIndex + 1]))));
        if (args.Contains("--screen-verify")) tests.Add(("Overlays open on the real screen and close (flag)", Sync(ScreenOverlayTests)));

        int failures = 0;
        foreach (var test in tests)
        {
            try { test.Run().GetAwaiter().GetResult(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
        }
        Console.WriteLine($"Utilities verification: {tests.Count - failures}/{tests.Count} passed, {assertions} assertions; no real clipboard or network used.");
        try { Directory.Delete(root, true); } catch { }
        return failures == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (beklenen: {expected}, gelen: {actual})");

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException(message);
    }

    private static string NewFolder(string name)
    {
        string path = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Touch(string folder, string name, string content = "x")
    {
        string path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- colour -------------------------------------------------------------------------------------------------

    private static void ColorFormatTests()
    {
        var c = new Rgb(18, 52, 86);
        Equal("#123456", ColorFormats.ToHex(c), "HEX is upper-case #RRGGBB");
        Equal("rgb(18, 52, 86)", ColorFormats.ToRgbText(c), "RGB text");
        Equal("hsl(210, 65%, 20%)", ColorFormats.ToHslText(c), "HSL text");
        Equal("Color.FromRgb(18, 52, 86)", ColorFormats.ToCSharpText(c), "C# Color text");
        Equal("#FFAA00", ColorFormats.Format(new Rgb(255, 170, 0), ColorFormat.Hex), "Format dispatches to HEX");
        Equal("hsl(0, 100%, 50%)", ColorFormats.ToHslText(new Rgb(255, 0, 0)), "Red in HSL");
        Equal("hsl(120, 100%, 50%)", ColorFormats.ToHslText(new Rgb(0, 255, 0)), "Green in HSL");
        Equal("hsl(240, 100%, 50%)", ColorFormats.ToHslText(new Rgb(0, 0, 255)), "Blue in HSL");
        Equal("hsl(0, 0%, 100%)", ColorFormats.ToHslText(new Rgb(255, 255, 255)), "White in HSL");
        Equal("hsl(0, 0%, 0%)", ColorFormats.ToHslText(new Rgb(0, 0, 0)), "Black in HSL");
        Equal("hsl(0, 0%, 50%)", ColorFormats.ToHslText(new Rgb(128, 128, 128)), "Grey in HSL");
        Equal("hsl(60, 100%, 50%)", ColorFormats.ToHslText(new Rgb(255, 255, 0)), "Yellow in HSL");

        foreach (var sample in new[] { new Rgb(12, 200, 99), new Rgb(250, 3, 77), new Rgb(1, 1, 2), new Rgb(200, 200, 10) })
        {
            Rgb back = ColorFormats.FromHsl(ColorFormats.ToHsl(sample));
            Equal(sample, back, "HSL round trip " + ColorFormats.ToHex(sample));
        }
        Assert(ColorFormats.TryParseHex("#a1B2c3", out Rgb p) && p == new Rgb(0xA1, 0xB2, 0xC3), "Parses mixed-case hex");
        Assert(ColorFormats.TryParseHex("fa0", out p) && p == new Rgb(0xFF, 0xAA, 0x00), "Parses short hex");
        Assert(!ColorFormats.TryParseHex("#12345", out _) && !ColorFormats.TryParseHex("zzzzzz", out _) && !ColorFormats.TryParseHex(null, out _), "Rejects bad hex");

        // Output never depends on the user's culture.
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Equal("hsl(210, 65%, 20%)", ColorFormats.ToHslText(c), "HSL text is culture independent");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void ColorHistoryTests()
    {
        List<string> history = new();
        for (int i = 0; i < 60; i++) history = ColorHistory.Add(history, new Rgb((byte)i, 0, 0));
        Equal(50, history.Count, "History is capped at 50");
        Equal("#3B0000", history[0], "Newest first");
        history = ColorHistory.Add(history, new Rgb(10, 0, 0));
        Equal("#0A0000", history[0], "Re-picked colour moves to the front");
        Equal(50, history.Count, "Re-picked colour is not duplicated");
        Equal(1, history.Count(h => h == "#0A0000"), "Exactly one copy");
    }

    // ---- ruler --------------------------------------------------------------------------------------------------

    private static void RulerTests()
    {
        RulerMeasurement m = RulerMath.Measure(10, 10, 310, 210);
        Equal(300, m.Width, "Width");
        Equal(200, m.Height, "Height");
        Equal(360.56, m.Diagonal, "Diagonal rounded to 2 decimals");
        Equal("300 × 200 px, çapraz 360.56 px", m.ToClipboardText(), "Clipboard text");

        m = RulerMath.Measure(310, 210, 10, 10);
        Equal(300, m.Width, "Direction does not matter (width)");
        Equal(200, m.Height, "Direction does not matter (height)");

        m = RulerMath.Measure(0, 0, 100, 30, shift: true);
        Equal(100, m.Width, "Shift keeps the longer horizontal axis");
        Equal(0, m.Height, "Shift locks the vertical axis");
        Equal(100.0, m.Diagonal, "Snapped diagonal equals width");
        m = RulerMath.Measure(0, 0, 30, -100, shift: true);
        Equal(0, m.Width, "Shift snaps to vertical");
        Equal(100, m.Height, "Vertical distance");
        Equal((100, 5), RulerMath.Snap(5, 5, 100, 40, true), "Snap returns the end point with the cross axis locked");
        Equal((100, 40), RulerMath.Snap(5, 5, 100, 40, false), "No shift, no snap");

        m = RulerMath.Measure(0, 0, 300, 450, dpiScale: 1.5);
        Equal(300, m.Width, "Physical pixels are reported as measured");
        Equal(200.0, m.WidthDip, "Width in device-independent units at 150 %");
        Equal(300.0, m.HeightDip, "Height in dp at 150 %");
        Assert(m.ToLabel().Contains("%150") && m.ToLabel().Contains("200 × 300 dp"), "Label mentions the scale and the dp size: " + m.ToLabel());
        Assert(!RulerMath.Measure(0, 0, 10, 10).ToLabel().Contains("dp"), "No dp text at 100 %");
        Equal(300, RulerMath.ToPhysical(200, 1.5), "dp to px at 150 %");
        Equal(250, RulerMath.ToPhysical(100, 2.5), "dp to px at 250 %");
        Equal(240.0, RulerMath.ToDip(300, 1.25), "px to dp at 125 %");
        Equal(1.0, RulerMath.Measure(0, 0, 1, 0, dpiScale: 0).DpiScale, "Invalid scale falls back to 1");
    }

    // ---- snippets -----------------------------------------------------------------------------------------------

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text;
        public int Reads;
        public BitmapSource? Image;
        public string? GetText() { Reads++; return Text; }
        public void SetText(string text) => Text = text;
        public BitmapSource? GetImage() => Image;
        public void SetImage(BitmapSource image) => Image = image;
    }

    private static void SnippetExpansionTests()
    {
        var now = new DateTime(2024, 3, 5, 9, 7, 45);
        var clipboard = new FakeClipboard { Text = "panodaki metin" };
        var expander = new SnippetExpander(() => now, clipboard.GetText);
        Equal("05.03.2024 09:07 panodaki metin", expander.Expand("{tarih} {saat} {pano}"), "Default placeholders");
        Equal("2024-03-05 | 09.07.45", expander.Expand("{tarih:yyyy-MM-dd} | {saat:HH.mm.ss}"), "Custom formats");
        Equal("05.03.2024 / 09:07", expander.Expand("{TARIH}") + " / " + expander.Expand("{Saat}"), "Placeholders are case-insensitive");
        Equal("{bilinmeyen} 05.03.2024", expander.Expand("{bilinmeyen} {tarih}"), "Unknown placeholders stay as they are");
        Equal("Merhaba,\r\n05.03.2024", expander.Expand("Merhaba,\r\n{tarih}"), "Line breaks survive");
        Equal("", expander.Expand(""), "Empty content");

        clipboard.Text = "{tarih} içerir";
        Equal("{tarih} içerir 05.03.2024", expander.Expand("{pano} {tarih}"), "Clipboard text is inserted literally (single pass)");
        clipboard.Text = null;
        Equal("[]", expander.Expand("[{pano}]"), "No clipboard text expands to nothing");
        clipboard.Reads = 0;
        expander.Expand("{pano}{pano}");
        Equal(1, clipboard.Reads, "Clipboard is read once per expansion");
        clipboard.Reads = 0;
        expander.Expand("{tarih} {saat}");
        Equal(0, clipboard.Reads, "Clipboard is not read when {pano} is absent");
        string odd = expander.Expand("{saat:%}");
        Assert(odd.Length > 0, "A strange format does not throw");
    }

    private static void SnippetStoreTests()
    {
        string path = Path.Combine(NewFolder("snip"), "snippets.json");
        var store = new SnippetStore(path);
        Equal(0, store.All.Count, "Starts empty");
        Assert(store.Save(new Snippet { Name = "İmza", Trigger = "imza", Folder = "Mail", Content = "Saygılarımla,\nBroid" }, out string? error), "Add: " + error);
        Assert(store.Save(new Snippet { Name = "Şablon bir", Trigger = "sb", Content = "{tarih}" }, out error), "Add second: " + error);
        Assert(!store.Save(new Snippet { Name = "Kopya", Trigger = "IMZA", Content = "x" }, out error) && error is not null, "Duplicate trigger rejected (case-insensitive)");
        Assert(!store.Save(new Snippet { Name = " ", Content = "x" }, out error), "Empty name rejected");
        Assert(!store.Save(new Snippet { Name = "A", Content = "" }, out error), "Empty content rejected");
        Assert(!store.Save(new Snippet { Name = "A", Trigger = "iki kelime", Content = "x" }, out error), "Trigger with spaces rejected");
        Equal(2, store.All.Count, "Rejected snippets were not stored");

        Snippet first = store.All.First(s => s.Name == "İmza");
        first.Content = "Yeni içerik";
        first.Trigger = "imza"; // same trigger on the same snippet is fine
        Assert(store.Save(first, out error), "Update keeps its own trigger: " + error);
        Equal("Yeni içerik", store.Get(first.Id)!.Content, "Update stored");
        Equal(2, store.All.Count, "Update does not duplicate");

        Equal("Şablon bir", store.Search("sablon").First().Name, "Search ignores Turkish characters (sablon -> Şablon)");
        Equal("İmza", store.Search("imza").First().Name, "Search by trigger / Turkish İ");
        Equal(1, store.Search("", "Mail").Count, "Folder filter");
        Equal(2, store.Search(null).Count, "Empty query lists everything");
        Equal(0, store.Search("yokboyle").Count, "No match");

        var reloaded = new SnippetStore(path);
        Equal(2, reloaded.All.Count, "Snippets persist as JSON");
        Equal("Mail", reloaded.Folders.Single(), "Folders listed");
        Assert(reloaded.Delete(first.Id) && !reloaded.Delete(first.Id), "Delete once");
        Equal(1, new SnippetStore(path).All.Count, "Delete persisted");

        File.WriteAllText(path, "{ bozuk json");
        var damaged = new SnippetStore(path);
        Assert(damaged.All.Count == 0 && damaged.LoadFailed, "Damaged file yields an empty list instead of throwing");
        Assert(File.Exists(path + ".bozuk"), "Damaged file is kept aside");
    }

    // ---- QR -----------------------------------------------------------------------------------------------------

    private static void QrRoundTripTests()
    {
        string text = "https://example.com/a?b=1&c=ü#x";
        foreach (QrErrorLevel level in Enum.GetValues<QrErrorLevel>())
        {
            RawImage image = QrService.Generate(text, 300, level);
            Assert(image.Width >= 300 && image.Width == image.Height, $"Square image of at least the requested size ({level}): {image.Width}");
            Equal(text, QrService.Decode(image), $"Round trip at level {level}");
        }
        foreach (string sample in new[] { "Merhaba dünya çğıöşü İĞÜŞÖÇ", "A", new string('Z', 1200), "WIFI:T:WPA;S:ev ağı;P:şifre123;;", "😀 emoji" })
        {
            RawImage image = QrService.Generate(sample, 512, QrErrorLevel.Medium);
            Equal(sample, QrService.Decode(image), "Round trip: " + (sample.Length > 20 ? sample[..20] + "…" : sample));
        }
        Equal(1024, QrService.Generate("x", 1024, QrErrorLevel.Low).Width, "Requested size honoured");
        Equal(QrService.MinSize, QrService.Generate("x", 1, QrErrorLevel.Low).Width, "Size clamped to the minimum");
        Throws<ArgumentException>(() => QrService.Generate("", 256, QrErrorLevel.Low), "Empty text rejected");
        Throws<InvalidOperationException>(() => QrService.Generate(new string('x', 5000), 512, QrErrorLevel.High), "Over-long text reports a friendly error");

        // Inverted (white on black) codes and blank images.
        RawImage code = QrService.Generate("ters", 300, QrErrorLevel.Medium);
        byte[] inverted = (byte[])code.Bgra.Clone();
        for (int i = 0; i < inverted.Length; i += 4) { inverted[i] ^= 255; inverted[i + 1] ^= 255; inverted[i + 2] ^= 255; }
        Equal("ters", QrService.Decode(inverted, code.Width, code.Height), "Inverted code is read");
        byte[] blank = Enumerable.Repeat((byte)255, 200 * 200 * 4).ToArray();
        Assert(QrService.Decode(blank, 200, 200) is null, "A blank image has no QR");
        Assert(QrService.Decode(Array.Empty<byte>(), 0, 0) is null, "Empty buffer is handled");
    }

    private static void QrImageSourceTests()
    {
        string text = "https://broidshot.example/ekran";
        RawImage code = QrService.Generate(text, 220, QrErrorLevel.Quartile);

        // A code sitting somewhere inside a bigger "screenshot" with noise around it.
        int width = 1280, height = 900;
        byte[] screen = new byte[width * height * 4];
        var random = new Random(7);
        for (int i = 0; i < screen.Length; i += 4)
        {
            byte v = (byte)(200 + random.Next(0, 40));
            screen[i] = screen[i + 1] = screen[i + 2] = v; screen[i + 3] = 255;
        }
        int offsetX = 777, offsetY = 333;
        for (int y = 0; y < code.Height; y++)
            Buffer.BlockCopy(code.Bgra, y * code.Width * 4, screen, ((offsetY + y) * width + offsetX) * 4, code.Width * 4);
        Equal(text, QrService.Decode(screen, width, height), "QR inside a large screen-like image");

        var snapshot = new ScreenSnapshot(new PixelRect(-1920, 0, width, height), screen);
        byte[]? crop = snapshot.Crop(new PixelRect(-1920 + offsetX - 20, offsetY - 20, code.Width + 40, code.Height + 40), out int cw, out int ch);
        Assert(crop is not null && cw == code.Width + 40 && ch == code.Height + 40, "Crop works with a negative monitor origin");
        Equal(text, QrService.Decode(crop!, cw, ch), "QR read from a cropped region");
        Assert(snapshot.Crop(new PixelRect(5000, 5000, 10, 10), out _, out _) is null, "Crop outside the snapshot is empty");
        Equal(new Rgb(screen[2], screen[1], screen[0]), snapshot.GetPixel(-1920, 0)!.Value, "GetPixel reads absolute coordinates");
        Assert(snapshot.GetPixel(0, 0) is null && snapshot.GetPixel(-2000, 0) is null, "GetPixel outside is null");

        string png = Path.Combine(NewFolder("qr"), "kod.png");
        QrService.SavePng(code, png);
        Assert(File.Exists(png) && new FileInfo(png).Length > 100, "PNG saved");
        BitmapSource loaded = QrService.LoadImage(png);
        Equal(text, QrService.Decode(loaded), "QR read back from the PNG file");
        Equal(text, QrService.Decode(QrService.ToBitmap(code)), "QR read from a BitmapSource (clipboard path)");
        Throws<Exception>(() => QrService.LoadImage(Path.Combine(Path.GetDirectoryName(png)!, "yok.png")), "Missing file throws");
    }


    private static void QrMultiTests()
    {
        RawImage a = QrService.Generate("https://broidshot.example/bir", 200, QrErrorLevel.Medium);
        RawImage b = QrService.Generate("İkinci kod · çğış", 240, QrErrorLevel.Quartile);
        int width = 1000, height = 640;
        byte[] screen = new byte[width * height * 4];
        for (int i = 0; i < screen.Length; i += 4) { screen[i] = 230; screen[i + 1] = 226; screen[i + 2] = 220; screen[i + 3] = 255; }
        void Place(RawImage code, int ox, int oy)
        {
            for (int y = 0; y < code.Height; y++)
                Buffer.BlockCopy(code.Bgra, y * code.Width * 4, screen, ((oy + y) * width + ox) * 4, code.Width * 4);
        }
        Place(a, 60, 80);
        Place(b, 640, 300);
        IReadOnlyList<QrHit> hits = QrService.DecodeAll(screen, width, height);
        Equal(2, hits.Count, "Both QR codes on one screen are found");
        QrHit first = hits.Single(h => h.Text == "https://broidshot.example/bir");
        QrHit second = hits.Single(h => h.Text == "İkinci kod · çğış");
        Assert(first.Points.Count >= 3 && first.Points.All(p => p.X > 60 && p.X < 260 && p.Y > 80 && p.Y < 280), "Points of the first code are inside it");
        Assert(second.Points.All(p => p.X > 640 && p.X < 880 && p.Y > 300 && p.Y < 540), "Points of the second code are inside it");
        Assert(first.ModuleSize > 1 && second.ModuleSize > 1, "Module size estimated");
        Assert(QrService.Decode(screen, width, height) is string one && hits.Any(h => h.Text == one), "Single Decode still returns one of them");
        Equal(0, QrService.DecodeAll(new byte[100 * 100 * 4], 100, 100).Count, "Blank image has no codes");
        Equal(0, QrService.DecodeAll(Array.Empty<byte>(), 0, 0).Count, "Empty buffer handled");
    }

    private static void ColorHistoryExternalPickTests()
    {
        Assert(!ColorPickerModule.RecordExternalPick(new Rgb(1, 2, 3)), "No running module: external pick reports false");
        string profile = NewFolder("external-pick");
        var module = new ColorPickerModule(new FakeClipboard());
        module.Initialize(new FakeHost("colorpicker", profile));
        int changed = 0;
        module.Changed += () => changed++;
        Assert(ColorPickerModule.RecordExternalPick(new Rgb(0xD9, 0x40, 0x46)), "Running module accepts the pick");
        Assert(ColorPickerModule.RecordExternalPick(new Rgb(0x11, 0x22, 0x33)), "Second pick");
        Assert(ColorPickerModule.RecordExternalPick(new Rgb(0xD9, 0x40, 0x46)), "Repeated pick moves to the front");
        Equal("#D94046,#112233", string.Join(",", module.Settings.History), "Newest first, no duplicates");
        Equal(3, changed, "Changed raised for the open page");
        var reloaded = new ColorPickerModule(new FakeClipboard());
        reloaded.Initialize(new FakeHost("colorpicker", profile));
        Equal("#D94046,#112233", string.Join(",", reloaded.Settings.History), "External picks are persisted");
        reloaded.Shutdown();
        module.Shutdown();
        Assert(!ColorPickerModule.RecordExternalPick(new Rgb(1, 2, 3)), "After shutdown no module receives picks");
    }
    // ---- text tools ---------------------------------------------------------------------------------------------

    private static void JsonTests()
    {
        string input = "{\"a\":1,\"b\":[1,2,{\"c\":null}],\"t\":\"çğıöşü <tag>\"}";
        TextResult formatted = TextOperations.JsonFormat(input);
        Assert(!formatted.IsError && formatted.Output!.Contains("\n") && formatted.Output.Contains("  \"a\": 1"), "Formatted JSON is indented: " + formatted.Output);
        Assert(formatted.Output!.Contains("çğıöşü <tag>"), "Turkish characters and angle brackets are not escaped");
        TextResult minified = TextOperations.JsonMinify(formatted.Output!);
        Equal(input, minified.Output, "Minify restores the compact form");
        Equal("Geçerli JSON.", TextOperations.JsonValidate(input).Output, "Validate success");
        Equal("[1,2]", TextOperations.JsonMinify("  [ 1 ,\n 2 ] ").Output, "Minify arrays");
        Equal("12345678901234567890.123456789", TextOperations.JsonMinify("12345678901234567890.123456789").Output, "Numbers keep their exact text");

        TextResult bad = TextOperations.JsonValidate("{\n  \"a\": 1,\n  \"b\": \n}");
        Assert(bad.IsError && bad.Error!.Contains("satır 4"), "Error names the line: " + bad.Error);
        bad = TextOperations.JsonFormat("{\"a\": 1,}");
        Assert(bad.IsError && bad.Error!.Contains("satır 1"), "Trailing comma is rejected with a line: " + bad.Error);
        Assert(TextOperations.JsonFormat("").IsError && TextOperations.JsonFormat("   ").IsError, "Empty input is an error");
        Assert(TextOperations.JsonFormat("{'a':1}").IsError, "Single quotes are not JSON");
        Assert(TextOperations.JsonValidate("{\"a\":1} // yorum").IsError, "Comments are not JSON");
    }

    private static void EncodingTests()
    {
        string text = "Merhaba Dünya çğıöşü";
        string encoded = TextOperations.Base64Encode(text).Output!;
        Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), encoded, "Base64 of UTF-8");
        Equal(text, TextOperations.Base64Decode(encoded).Output, "Base64 round trip");
        Equal("Merhaba", TextOperations.Base64Decode("TWVy\r\naGFiYQ==").Output, "Whitespace ignored");
        Equal("Merhaba", TextOperations.Base64Decode("TWVyaGFiYQ").Output, "Missing padding accepted");
        Equal("??>", TextOperations.Base64Decode("Pz8-").Output, "URL-safe alphabet accepted");
        Assert(TextOperations.Base64Decode("***").IsError, "Invalid characters rejected");
        Assert(TextOperations.Base64Decode("A").IsError, "Impossible length rejected");
        Assert(TextOperations.Base64Decode("").IsError, "Empty rejected");
        Assert(TextOperations.Base64Decode(Convert.ToBase64String(new byte[] { 0xFF, 0xFE, 0xFD, 0x80 })).IsError, "Binary data is not shown as text");

        Equal("a%20b%26c%3D%C3%A7", TextOperations.UrlEncode("a b&c=ç").Output, "URL encode");
        Equal("a b&c=ç", TextOperations.UrlDecode("a%20b%26c%3D%C3%A7").Output, "URL decode");
        Equal("a b", TextOperations.UrlDecode("a+b").Output, "Plus decodes to a space");
        string rough = "50% + ş/ğ?x=1&y=\"z\"";
        Equal(rough, TextOperations.UrlDecode(TextOperations.UrlEncode(rough).Output!).Output, "URL round trip");
    }

    private static void CaseTests()
    {
        Equal("İSTANBUL IĞDIR İZMİR", TextOperations.Upper("istanbul ığdır izmir").Output, "Upper: i->İ, ı->I");
        Equal("ısparta izmir ığdır", TextOperations.Lower("ISPARTA İZMİR IĞDIR").Output, "Lower: I->ı, İ->i");
        Equal("İstanbul Iğdır Ilık Ali'nin Şeker", TextOperations.Title("istanbul ığdır ILIK ali'nin şEKER").Output, "Title case");
        Equal("Çay, Kahve; Süt-Şeker 2 Adet", TextOperations.Title("çay, kahve; süt-şeker 2 adet").Output, "Title case with punctuation");
        Equal("", TextOperations.Upper("").Output, "Empty stays empty");
        // Result must not depend on the thread culture.
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Equal("İ", TextOperations.Upper("i").Output, "Upper ignores the thread culture");
            Equal("ı", TextOperations.Lower("I").Output, "Lower ignores the thread culture");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void LineAndMiscTests()
    {
        Equal("a\r\nb\r\nc", TextOperations.TrimLines("  a  \r\n b\r\nc  ").Output, "Trim each line");
        Equal("a\nb", TextOperations.RemoveEmptyLines("a\n\n  \nb\n").Output, "Empty lines removed");
        Equal("a\nb\nA\nc", TextOperations.DedupeLines("a\nb\na\nA\nb\nc").Output, "Dedupe keeps the first of each exact line");
        Equal("ali\nçay\nışık\niğne\nÖmer\nşeker\nzeytin",
            TextOperations.SortLines("zeytin\nçay\nışık\nali\niğne\nÖmer\nşeker").Output, "Turkish alphabetical order");
        Equal("zeytin\nşeker\nÖmer\niğne\nışık\nçay\nali",
            TextOperations.SortLines("zeytin\nçay\nışık\nali\niğne\nÖmer\nşeker", descending: true).Output, "Descending order");
        Equal("a\r\nb", TextOperations.SortLines("b\r\na").Output, "CRLF input keeps CRLF");

        TextStats stats = TextOperations.Count("Merhaba dünya\nİki satır");
        Equal(4, stats.Words, "Words");
        Equal(23, stats.Characters, "Characters");
        Equal(20, stats.CharactersWithoutSpaces, "Characters without spaces");
        Equal(2, stats.Lines, "Lines");
        Equal(26, stats.Bytes, "UTF-8 bytes");
        Equal(0, TextOperations.Count("").Lines, "Empty text has no lines");
        Equal(2, TextOperations.Count("a\r\nb").Lines, "CRLF counts as one break");

        Equal("900150983cd24fb0d6963f7d28e17f72", TextOperations.Hash("MD5", "abc"), "MD5");
        Equal("a9993e364706816aba3e25717850c26c9cd0d89d", TextOperations.Hash("SHA1", "abc"), "SHA1");
        Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", TextOperations.Hash("SHA256", "abc"), "SHA256");
        Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", TextOperations.Hash("SHA256", ""), "SHA256 of empty text");
        Throws<ArgumentException>(() => TextOperations.Hash("CRC", "x"), "Unknown algorithm");

        string one = TextOperations.Uuids("").Output!;
        Assert(Guid.TryParse(one, out _), "One UUID by default");
        string[] many = TextOperations.Uuids("5").Output!.Split(Environment.NewLine);
        Equal(5, many.Length, "Count taken from the input");
        Equal(5, many.Distinct().Count(), "UUIDs are unique");
        Equal(1, TextOperations.Uuids("500").Output!.Split(Environment.NewLine).Length, "Absurd counts fall back to one");

        TimeZoneInfo plus3 = TimeZoneInfo.CreateCustomTimeZone("t3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
        TextResult ts = TextOperations.TimestampToDate("0", plus3);
        Assert(ts.Output!.Contains("UTC: 1970-01-01 00:00:00") && ts.Output.Contains("Yerel: 1970-01-01 03:00:00 (UTC+03:00)"), "Epoch: " + ts.Output);
        Assert(TextOperations.TimestampToDate("1700000000", plus3).Output!.Contains("UTC: 2023-11-14 22:13:20"), "Seconds");
        Assert(TextOperations.TimestampToDate("1700000000000", plus3).Output!.Contains("UTC: 2023-11-14 22:13:20"), "Milliseconds");
        Assert(TextOperations.TimestampToDate("1700000000000", plus3).Output!.Contains("milisaniye"), "Unit detected");
        Assert(TextOperations.TimestampToDate("abc").IsError, "Not a number");
        Assert(TextOperations.TimestampToDate("99999999999999999999").IsError, "Too large");
        Assert(TextOperations.DateToTimestamp("2023-11-14T22:13:20Z", plus3).Output!.StartsWith("Saniye: 1700000000"), "ISO with Z");
        Assert(TextOperations.DateToTimestamp("2023-11-15 01:13:20", plus3).Output!.StartsWith("Saniye: 1700000000"), "Wall clock in the given zone");
        Assert(TextOperations.DateToTimestamp("14.11.2023 22:13", TimeZoneInfo.Utc).Output!.StartsWith("Saniye: 1699999980"), "Turkish date format");
        Assert(TextOperations.DateToTimestamp("2023-11-15T01:13:20+03:00", TimeZoneInfo.Utc).Output!.Contains("Milisaniye: 1700000000000"), "Explicit offset wins");
        Assert(TextOperations.DateToTimestamp("dün akşam").IsError, "Unparseable date");

        foreach (TextOperation op in TextOperations.All)
            Assert(op.Id.Length > 0 && op.Title.Length > 0 && op.Group.Length > 0, "Operation metadata: " + op.Id);
        Equal(TextOperations.All.Count, TextOperations.All.Select(o => o.Id).Distinct().Count(), "Operation ids are unique");
    }

    // ---- batch rename -------------------------------------------------------------------------------------------

    private static RenameOptions Options(Action<RenameOptions> configure)
    {
        var options = new RenameOptions();
        configure(options);
        return options;
    }

    private static List<RenameEntry> Preview(IEnumerable<string> files, Action<RenameOptions> configure) =>
        RenameEngine.Preview(files.ToList(), Options(configure));

    private static void RenamePreviewTests()
    {
        string folder = NewFolder("rename");
        string[] files = { Touch(folder, "tatil 1.jpg"), Touch(folder, "tatil 2.JPG"), Touch(folder, "Notlar.txt") };

        List<RenameEntry> e = Preview(files, o => { o.Find = "tatil"; o.Replace = "Yaz"; });
        Equal("Yaz 1.jpg", e[0].NewName, "Find/replace keeps the extension");
        Equal("Yaz 2.JPG", e[1].NewName, "Case-insensitive find, extension case kept");
        Equal(RenameStatus.Unchanged, e[2].Status, "Untouched file is Unchanged");
        Equal(RenameStatus.Ready, e[0].Status, "Changed file is Ready");

        e = Preview(files, o => { o.Prefix = "ön_"; o.Suffix = "_son"; });
        Equal("ön_tatil 1_son.jpg", e[0].NewName, "Prefix and suffix go around the base name");

        e = Preview(files, o => { o.Pattern = "Foto_{n:000}"; o.NumberStart = 7; o.NumberStep = 5; });
        Equal("Foto_007.jpg", e[0].NewName, "Numbering with {n:000}");
        Equal("Foto_012.JPG", e[1].NewName, "Step applies");
        Equal("Foto_017.txt", e[2].NewName, "Numbering counts through the list");

        e = Preview(files, o => { o.Pattern = "{name}-{n}"; });
        Equal("tatil 1-1.jpg", e[0].NewName, "{name} and plain {n}");

        e = Preview(files, o => { o.UseRegex = true; o.Find = @"(\w+) (\d)"; o.Replace = "$2-$1"; });
        Equal("1-tatil.jpg", e[0].NewName, "Regex with groups");
        Throws<RenameException>(() => Preview(files, o => { o.UseRegex = true; o.Find = "(["; }), "Broken regex is reported");

        e = Preview(files, o => o.Case = RenameCase.Upper);
        Equal("TATİL 1.jpg", e[0].NewName, "Upper case with Turkish i (extension untouched)");
        e = Preview(new[] { Touch(folder, "IŞIK Kılıcı.txt") }, o => o.Case = RenameCase.Lower);
        Equal("ışık kılıcı.txt", e[0].NewName, "Lower case with Turkish I");
        e = Preview(new[] { Touch(folder, "ali veli ışık.txt") }, o => o.Case = RenameCase.Title);
        Equal("Ali Veli Işık.txt", e[0].NewName, "Title case");

        e = Preview(files, o => o.NewExtension = "png");
        Assert(e.All(x => x.NewName.EndsWith(".png")), "Extension replaced (without dot)");
        e = Preview(files, o => o.NewExtension = ".bak");
        Equal("Notlar.bak", e[2].NewName, "Extension replaced (with dot)");

        e = Preview(new[] { Touch(folder, ".gitignore") }, o => o.Prefix = "x");
        Equal("x.gitignore", e[0].NewName, "Dot files are treated as names");

        foreach (string f in files) Assert(File.Exists(f), "Preview never touches the files");
    }

    private static void RenameConflictTests()
    {
        string folder = NewFolder("conflict");
        string a = Touch(folder, "a.txt"), b = Touch(folder, "b.txt"), c = Touch(folder, "c.txt");
        Touch(folder, "mevcut.txt");

        List<RenameEntry> e = Preview(new[] { a, b }, o => o.Pattern = "ayni");
        Assert(e.All(x => x.Status == RenameStatus.Conflict), "Two files with the same new name conflict");

        e = Preview(new[] { a }, o => o.Pattern = "mevcut");
        Equal(RenameStatus.Conflict, e[0].Status, "Existing file on disk is a conflict");
        Assert(e[0].Message!.Contains("zaten var"), "Message explains it: " + e[0].Message);

        e = Preview(new[] { a, c }, o => { o.Find = "a"; o.Replace = "c"; });
        Equal(RenameStatus.Conflict, e[0].Status, "Renaming onto an unchanged batch member conflicts");

        e = Preview(new[] { a, b }, o => { o.Pattern = "x?y"; });
        Assert(e.All(x => x.Status == RenameStatus.Invalid), "Invalid characters are flagged");
        e = Preview(new[] { a }, o => { o.Pattern = "CON"; });
        Equal(RenameStatus.Invalid, e[0].Status, "Reserved device names are flagged");
        e = Preview(new[] { a }, o => { o.Find = "a"; o.Replace = ""; o.NewExtension = ""; });
        Equal(RenameStatus.Ready, e[0].Status, "Name that becomes just the extension is still allowed as .txt");
        e = Preview(new[] { a }, o => { o.Pattern = "nokta."; o.NewExtension = "."; });
        Equal(RenameStatus.Invalid, e[0].Status, "Trailing dot is flagged");

        // Case-only rename of the same file is not a conflict.
        e = Preview(new[] { a }, o => o.Case = RenameCase.Upper);
        Equal(RenameStatus.Ready, e[0].Status, "Case-only rename is allowed");

        // Chains (a -> b while b -> c) are not conflicts.
        var journal = new RenameJournal(Path.Combine(NewFolder("jc"), "j.json"));
        var chain = new List<RenameEntry>
        {
            new(a, "b.txt") { Status = RenameStatus.Ready }, new(b, "c.txt") { Status = RenameStatus.Ready }, new(c, "d.txt") { Status = RenameStatus.Ready }
        };
        Assert(RenameEngine.Apply(new[] { new RenameEntry(a, "mevcut.txt") { Status = RenameStatus.Conflict } }, journal).Error is not null, "Apply refuses conflicts");
        Assert(File.Exists(a), "Nothing changed after a refused apply");
        Assert(RenameEngine.Apply(chain, journal).Error is null, "Chain applies");
        Assert(!File.Exists(a) && File.Exists(Path.Combine(folder, "b.txt")) && File.Exists(Path.Combine(folder, "c.txt")) && File.Exists(Path.Combine(folder, "d.txt")), "Chain result on disk");

        // The disk can change between preview and apply.
        string x = Touch(folder, "x1.txt");
        e = Preview(new[] { x }, o => o.Prefix = "n_");
        File.Delete(x);
        BatchApplyResult stale = RenameEngine.Apply(e, journal);
        Assert(stale.Error is not null && stale.Applied == 0, "Apply notices a vanished file");
        string y = Touch(folder, "y1.txt");
        e = Preview(new[] { y }, o => o.Prefix = "n_");
        Touch(folder, "n_y1.txt", "baska");
        stale = RenameEngine.Apply(e, journal);
        Assert(stale.Error is not null && File.ReadAllText(Path.Combine(folder, "n_y1.txt")) == "baska", "Apply never overwrites a file that appeared meanwhile");
    }

    private static void RenameApplyUndoTests()
    {
        string folder = NewFolder("apply");
        string journalPath = Path.Combine(NewFolder("journal"), "rename-journal.json");
        var journal = new RenameJournal(journalPath);
        string p1 = Touch(folder, "IMG_1.jpg", "bir"), p2 = Touch(folder, "IMG_2.jpg", "iki"), p3 = Touch(folder, "IMG_3.jpg", "uc");

        List<RenameEntry> entries = Preview(new[] { p1, p2, p3 }, o => { o.Pattern = "Tatil_{n:00}"; o.Case = RenameCase.None; });
        BatchApplyResult result = RenameEngine.Apply(entries, journal);
        Assert(result.Error is null && result.Applied == 3, "Applied three renames: " + result.Error);
        Equal("bir", File.ReadAllText(Path.Combine(folder, "Tatil_01.jpg")), "File 1 content moved with the rename");
        Equal("uc", File.ReadAllText(Path.Combine(folder, "Tatil_03.jpg")), "File 3 renamed");
        Assert(!File.Exists(p1) && !File.Exists(p2) && !File.Exists(p3), "Old names are gone");
        Assert(File.Exists(journalPath), "Journal written");
        Assert(new RenameJournal(journalPath).Last is { } last && last.Moves.Count == 3 && last.Kind == BatchKind.Rename, "Journal survives a restart");

        UndoResult? undo = new RenameJournal(journalPath).UndoLast();
        Assert(undo is not null && undo.Restored == 3 && undo.Skipped.Count == 0, "Undo restores all three");
        Equal("iki", File.ReadAllText(p2), "Original name and content back");
        Assert(!File.Exists(Path.Combine(folder, "Tatil_01.jpg")), "New names removed by undo");
        Assert(journal.UndoLast() is null && journal.Last is null, "Nothing left to undo afterwards");

        // Swap two names (needs temporary names) and undo it.
        var swap = new List<RenameEntry>
        {
            new(p1, "IMG_2.jpg") { Status = RenameStatus.Ready }, new(p2, "IMG_1.jpg") { Status = RenameStatus.Ready }
        };
        Assert(RenameEngine.Apply(swap, journal).Error is null, "Swap applies");
        Equal("iki", File.ReadAllText(p1), "IMG_1 now holds the old IMG_2 content");
        Equal("bir", File.ReadAllText(p2), "IMG_2 now holds the old IMG_1 content");
        Assert(Directory.GetFiles(folder, ".*.broidtmp").Length == 0, "No temporary files left behind");
        Assert(journal.UndoLast() is { Restored: 2 }, "Swap undone");
        Equal("bir", File.ReadAllText(p1), "Swap undone: content 1");
        Equal("iki", File.ReadAllText(p2), "Swap undone: content 2");

        // Case-only rename and undo.
        var caseOnly = Preview(new[] { p3 }, o => o.Case = RenameCase.Lower);
        Assert(RenameEngine.Apply(caseOnly, journal).Error is null, "Case-only apply");
        Assert(Directory.GetFiles(folder).Select(Path.GetFileName).Contains("ımg_3.jpg"), "File now has the Turkish lower-case name (I -> ı)");
        Assert(journal.UndoLast() is { Restored: 1 }, "Case-only undo");
        Assert(Directory.GetFiles(folder).Select(Path.GetFileName).Contains("IMG_3.jpg"), "Original casing back");

        // Undo skips what it cannot restore safely (never overwrites, never deletes).
        var first = Preview(new[] { p1 }, o => o.Prefix = "z_");
        RenameEngine.Apply(first, journal);
        Touch(folder, "IMG_1.jpg", "yeni dosya");
        UndoResult? partial = journal.UndoLast();
        Assert(partial is { Restored: 0 } && partial.Skipped.Count == 1, "Undo skips when the old name is taken");
        Equal("yeni dosya", File.ReadAllText(p1), "The newer file was not overwritten");
        Assert(File.Exists(Path.Combine(folder, "z_IMG_1.jpg")), "The renamed file was not deleted");

        // The journal keeps only the last batches.
        for (int i = 0; i < RenameJournal.MaxBatches + 5; i++)
            journal.Append(new JournalBatch { Kind = BatchKind.Rename, Root = folder });
        Equal(RenameJournal.MaxBatches, journal.Batches.Count, "Journal is bounded");
    }

    // ---- organize -----------------------------------------------------------------------------------------------

    private static void OrganizeTests()
    {
        string folder = NewFolder("organize");
        string journalPath = Path.Combine(NewFolder("jo"), "j.json");
        var journal = new RenameJournal(journalPath);
        var names = new[] { "foto.jpg", "Resim.PNG", "klip.mp4", "rapor.pdf", "yedek.zip", "sarki.mp3", "garip.xyz", "uzantisiz", "not.txt" };
        foreach (string n in names) Touch(folder, n, n);
        Touch(folder, "Görseller/foto.jpg", "eski");           // pre-existing folder with a clashing name
        Touch(folder, "altklasor/dokunma.jpg", "alt");          // sub-folders are left alone
        string hidden = Touch(folder, "gizli.jpg", "g");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        List<OrganizeEntry> preview = OrganizeEngine.Preview(folder);
        Equal(names.Length, preview.Count, "Hidden files and sub-folders are skipped");
        Equal(FileCategory.Images, preview.Single(x => x.FileName == "Resim.PNG").Category, "Extensions are case-insensitive");
        Equal(FileCategory.Videos, preview.Single(x => x.FileName == "klip.mp4").Category, "Video");
        Equal(FileCategory.Documents, preview.Single(x => x.FileName == "not.txt").Category, "Document");
        Equal(FileCategory.Archives, preview.Single(x => x.FileName == "yedek.zip").Category, "Archive");
        Equal(FileCategory.Music, preview.Single(x => x.FileName == "sarki.mp3").Category, "Music");
        Equal(FileCategory.Other, preview.Single(x => x.FileName == "garip.xyz").Category, "Unknown type");
        Equal(FileCategory.Other, preview.Single(x => x.FileName == "uzantisiz").Category, "No extension");
        Equal("Diğer", preview.Single(x => x.FileName == "uzantisiz").TargetFolderName, "Folder names are Turkish");
        OrganizeEntry clash = preview.Single(x => x.FileName == "foto.jpg");
        Assert(clash.Renamed && Path.GetFileName(clash.TargetPath) == "foto (2).jpg", "Clashing name gets a suffix: " + clash.TargetPath);
        Assert(preview.All(x => Path.GetDirectoryName(Path.GetDirectoryName(x.TargetPath)) == folder), "Every target is inside the chosen folder");
        Assert(File.Exists(Path.Combine(folder, "foto.jpg")) && !Directory.Exists(Path.Combine(folder, "Videolar")), "Preview changes nothing");

        BatchApplyResult applied = OrganizeEngine.Apply(preview, folder, journal);
        Assert(applied.Error is null && applied.Applied == names.Length, "Applied: " + applied.Error);
        Equal("klip.mp4", Path.GetFileName(Directory.GetFiles(Path.Combine(folder, "Videolar")).Single()), "Video moved");
        Equal("eski", File.ReadAllText(Path.Combine(folder, "Görseller", "foto.jpg")), "Existing file was not overwritten");
        Equal("foto.jpg", Path.GetFileName(Directory.GetFiles(Path.Combine(folder, "Görseller"), "foto.jpg").Single()), "Existing file kept its name");
        Equal("foto.jpg", File.ReadAllText(Path.Combine(folder, "Görseller", "foto (2).jpg")), "Clashing file stored under the suffixed name");
        Assert(File.Exists(hidden) && File.Exists(Path.Combine(folder, "altklasor", "dokunma.jpg")), "Skipped items untouched");
        Assert(Directory.GetFiles(folder).Length == 1, "Only the hidden file is left at the top level");

        UndoResult? undo = journal.UndoLast();
        Assert(undo is { Restored: 9 } && undo.Skipped.Count == 0, "Undo restores all nine");
        foreach (string n in names) Equal(n, File.ReadAllText(Path.Combine(folder, n)), "Restored " + n);
        Assert(!Directory.Exists(Path.Combine(folder, "Videolar")) && !Directory.Exists(Path.Combine(folder, "Diğer")), "Folders created by the batch are removed");
        Assert(Directory.Exists(Path.Combine(folder, "Görseller")) && File.Exists(Path.Combine(folder, "Görseller", "foto.jpg")), "Pre-existing folder and its file stay");
        Assert(!File.Exists(Path.Combine(folder, "Görseller", "foto (2).jpg")), "Suffixed file went back to its original name");

        Throws<DirectoryNotFoundException>(() => OrganizeEngine.Preview(Path.Combine(folder, "yok")), "Missing folder");

        // A trailing separator on the folder does not break the "stay inside" check.
        string folder2 = NewFolder("organize2");
        Touch(folder2, "a.jpg");
        var p2 = OrganizeEngine.Preview(folder2 + Path.DirectorySeparatorChar);
        Assert(OrganizeEngine.Apply(p2, folder2 + Path.DirectorySeparatorChar, journal).Error is null, "Trailing separator accepted");
    }

    // ---- zip ----------------------------------------------------------------------------------------------------

    private static void ZipRoundTripTests()
    {
        string source = NewFolder("zipsrc");
        string f1 = Touch(source, "a.txt", "alfa"), f2 = Touch(source, "ç ö.txt", "türkçe");
        string dir = Path.Combine(source, "klasör");
        Touch(dir, "ic/b.txt", "beta");
        string zip = Path.Combine(NewFolder("zipout"), "arsiv.zip");

        var reports = new List<double>();
        int count = ArchiveService.CreateZip(new[] { f1, f2, dir }, zip, new SyncProgress(reports.Add));
        Equal(3, count, "Three files zipped (folder content included)");
        Assert(reports.Count == 3 && reports[^1] == 1.0, "Progress reported");
        using (ZipArchive archive = ZipFile.OpenRead(zip))
        {
            var entries = archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert(entries.Contains("a.txt") && entries.Contains("ç ö.txt") && entries.Contains("klasör/ic/b.txt"), "Entry names: " + string.Join(", ", entries));
        }
        Throws<IOException>(() => ArchiveService.CreateZip(new[] { f1 }, zip), "An existing zip is never overwritten");
        Equal(Path.Combine(Path.GetDirectoryName(zip)!, "arsiv (2).zip"), ArchiveService.UniquePath(zip), "UniquePath suggests a free name");
        Assert(Directory.GetFiles(Path.GetDirectoryName(zip)!, "*.tmp").Length == 0, "No temporary files left");
        Throws<FileNotFoundException>(() => ArchiveService.CreateZip(new[] { Path.Combine(source, "yok.txt") }, Path.Combine(source, "x.zip")), "Missing source");

        // Two files with the same name from different folders do not collide inside the archive.
        string other = NewFolder("zipother");
        string dup = Touch(other, "a.txt", "baska");
        string zip2 = Path.Combine(NewFolder("zipout2"), "iki.zip");
        ArchiveService.CreateZip(new[] { f1, dup }, zip2);
        using (ZipArchive archive = ZipFile.OpenRead(zip2)) Equal(2, archive.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Duplicate names get a suffix");

        string dest = Path.Combine(NewFolder("unzip"), "cikti");
        ExtractResult result = ArchiveService.Extract(zip, dest);
        Equal(3, result.Files, "Three files extracted");
        Equal("alfa", File.ReadAllText(Path.Combine(dest, "a.txt")), "Content a");
        Equal("türkçe", File.ReadAllText(Path.Combine(dest, "ç ö.txt")), "Content Turkish name");
        Equal("beta", File.ReadAllText(Path.Combine(dest, "klasör", "ic", "b.txt")), "Content nested");

        // Existing files are not overwritten.
        File.WriteAllText(Path.Combine(dest, "a.txt"), "değiştirilmiş");
        ExtractResult again = ArchiveService.Extract(zip, dest);
        Equal("değiştirilmiş", File.ReadAllText(Path.Combine(dest, "a.txt")), "Existing file kept");
        Equal("alfa", File.ReadAllText(Path.Combine(dest, "a (2).txt")), "New copy stored with a suffix");
        Equal(3, again.Files, "Count of the second extraction");
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _report;
        public SyncProgress(Action<double> report) { _report = report; }
        public void Report(double value) => _report(value);
    }

    private static string MakeZip(string folder, params (string Name, string Content)[] entries)
    {
        string path = Path.Combine(folder, "kotu-" + Guid.NewGuid().ToString("N")[..5] + ".zip");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }

    private static void ZipSlipTests()
    {
        string bench = NewFolder("slip");
        string dest = Path.Combine(bench, "hedef");
        var attacks = new[] { "../evil.txt", "..\\evil.txt", "ic/../../evil.txt", "ic\\..\\..\\evil.txt", "/mutlak.txt", "\\\\sunucu\\paylasim\\x.txt", "C:\\Windows\\Temp\\x.txt", "C:/x.txt", "dosya.txt:gizli", "a/./../../b.txt" };
        foreach (string attack in attacks)
        {
            string zip = MakeZip(bench, ("iyi.txt", "iyi"), (attack, "kötü"));
            Throws<ZipSlipException>(() => ArchiveService.Extract(zip, dest), "Rejected: " + attack);
            Assert(!Directory.Exists(dest) || !Directory.EnumerateFileSystemEntries(dest).Any(), "Nothing was extracted before rejection: " + attack);
        }
        Assert(!File.Exists(Path.Combine(bench, "evil.txt")) && !File.Exists(Path.Combine(Path.GetDirectoryName(bench)!, "evil.txt")), "No file escaped the destination");

        // Directory entries are validated too.
        Throws<ZipSlipException>(() => ArchiveService.Extract(MakeZip(bench, ("../kardes/", "")), dest), "Directory entry outside the destination");
        // Sibling folder with the same prefix must not pass the prefix test.
        Throws<ZipSlipException>(() => ArchiveService.Extract(MakeZip(bench, ("../hedef-kardes/x.txt", "k")), dest), "Prefix look-alike folder");

        // Legit nested names, dots in names and dot segments inside the folder are fine.
        string good = MakeZip(bench, ("a/b/c.txt", "1"), ("ana..dosya.txt", "2"), ("./d.txt", "3"), ("klasör/", ""));
        ExtractResult ok = ArchiveService.Extract(good, dest);
        Equal(3, ok.Files, "Legitimate entries extract");
        Assert(File.Exists(Path.Combine(dest, "a", "b", "c.txt")) && File.Exists(Path.Combine(dest, "ana..dosya.txt")) && File.Exists(Path.Combine(dest, "d.txt")) && Directory.Exists(Path.Combine(dest, "klasör")), "Layout on disk");

        Equal(Path.Combine(dest, "x", "y.txt"), ArchiveService.ResolveEntry(dest, "x/y.txt", out bool isDir), "ResolveEntry maps inside the root");
        Assert(!isDir, "Not a directory entry");
        Throws<ZipSlipException>(() => ArchiveService.ResolveEntry(dest, "..", out _), "Bare ..");
    }

    // ---- module contract ----------------------------------------------------------------------------------------

    private sealed class FakeHost : IToolHost
    {
        public FakeHost(string id, string dataRoot)
        {
            ModuleId = id;
            DataRoot = dataRoot;
            string folder = Path.Combine(dataRoot, "modules", id);
            DataFolder = Path.Combine(folder, "data");
            Directory.CreateDirectory(DataFolder);
            Settings = new ModuleSettings(Path.Combine(folder, "settings.json"));
            Dispatcher = Dispatcher.CurrentDispatcher;
            Messages = new MessageBus(null);
            Jobs = new JobTracker { FinishedJobLinger = TimeSpan.Zero };
        }

        public string ModuleId { get; }
        public string DataRoot { get; }
        public string DataFolder { get; }
        public ModuleSettings Settings { get; }
        public IMessageBus Messages { get; }
        public IJobTracker Jobs { get; }
        public IExternalToolManager ExternalTools => throw new NotSupportedException("Kullanılmaz.");
        public Dispatcher Dispatcher { get; }
        public List<ToastOptions> Toasts { get; } = new();
        public string? NavigatedTo;
        public void Navigate(string moduleId) => NavigatedTo = moduleId;
        public void ShowToast(ToastOptions toast) => Toasts.Add(toast);
        public HotkeyRegistration RegisterHotkey(HotkeyGesture gesture, Action callback) => HotkeyRegistration.Failed(gesture, "test");
        public void Log(string message, Exception? exception = null) { }
    }

    private static void ModuleContractTests()
    {
        string profile = DataPaths.Root;
        var clipboard = new FakeClipboard();
        var modules = new List<UtilityModuleBase>
        {
            new ColorPickerModule(clipboard), new RulerModule(clipboard), new SnippetsModule(clipboard),
            new QrModule(clipboard), new TextToolsModule(clipboard), new BatchFilesModule()
        };
        string[] ids = { "colorpicker", "ruler", "snippets", "qr", "texttools", "batchfiles" };
        Equal(string.Join(",", ids), string.Join(",", modules.Select(m => m.Id)), "Module ids");
        Assert(modules.All(m => DataPaths.IsValidId(m.Id)), "Ids are valid folder names");
        Assert(modules.All(m => m.Category == ToolCategories.Utilities), "Shared category");
        Assert(modules.All(m => m.Title.Length > 0 && m.Description.Length > 0 && m.Icon.Length > 0 && m.Keywords.Count > 0), "Metadata present");

        var hosts = new Dictionary<string, FakeHost>();
        foreach (UtilityModuleBase m in modules)
        {
            var host = new FakeHost(m.Id, profile);
            hosts[m.Id] = host;
            m.Initialize(host);
        }

        var color = modules[0];
        ToolHotkey hotkey = color.Hotkeys.Single();
        Equal("Ctrl+Shift+C", hotkey.DefaultGesture.ToString(), "Colour picker default hotkey");

        var titles = modules.SelectMany(m => m.Commands.Select(c => (Module: m, Command: c))).ToList();
        foreach (string expected in new[]
                 {
                     "Ekrandan renk al", "Cetvel", "QR oluştur", "Ekrandaki QR'ı oku", "JSON biçimlendir", "Toplu yeniden adlandır", "Klasörü düzenle"
                 })
            Assert(titles.Any(t => t.Command.Title == expected), "Palette command exists: " + expected);
        Assert(titles.All(t => t.Command.Execute is not null || t.Command.NavigateToModuleId is not null), "Every command does something");
        Equal(titles.Count, titles.Select(t => t.Command.Id ?? t.Command.Title).Distinct().Count(), "Command ids are unique");

        // Snippets appear as "Snippet: <name>" commands, and the list follows the store at run time.
        var snippets = (SnippetsModule)modules[2];
        Assert(snippets.Commands.Any(c => c.Title.StartsWith("Snippet: ")), "Seeded example snippet is a palette command");
        Assert(snippets.Store.Save(new Snippet { Name = "Selamlama", Trigger = "selam", Content = "Merhaba {tarih}" }, out _), "Snippet saved");
        ToolCommand cmd = snippets.Commands.Single(c => c.Title == "Snippet: Selamlama");
        Assert(cmd.Description!.Contains("Merhaba"), "Command description previews the content");
        Assert(cmd.Keywords.Contains("selam"), "Trigger is a palette keyword");
        Assert(snippets.Store.Delete(snippets.Store.All.Single(s => s.Name == "Selamlama").Id) && snippets.Commands.All(c => c.Title != "Snippet: Selamlama"), "Deleted snippets leave the palette");

        // Text tool palette commands work on the clipboard (fake).
        var tools = (TextToolsModule)modules[4];
        clipboard.Text = "{\"a\":1}";
        tools.Commands.Single(c => c.Title == "JSON biçimlendir").Execute!(CancellationToken.None).GetAwaiter().GetResult();
        Assert(clipboard.Text!.Contains("\n") && clipboard.Text.Contains("\"a\": 1"), "JSON command formatted the clipboard text");
        clipboard.Text = "istanbul";
        tools.Commands.Single(c => c.Title == "Büyük harfe çevir").Execute!(CancellationToken.None).GetAwaiter().GetResult();
        Equal("İSTANBUL", clipboard.Text, "Upper-case command is Turkish aware");
        clipboard.Text = "{bozuk";
        tools.Commands.Single(c => c.Title == "JSON biçimlendir").Execute!(CancellationToken.None).GetAwaiter().GetResult();
        Equal("{bozuk", clipboard.Text, "A failing command leaves the clipboard alone");
        Assert(hosts["texttools"].Toasts.Last().Kind == ToastKind.Error, "...and reports an error toast");

        // QR palette command pre-fills the generator from the clipboard.
        var qr = (QrModule)modules[3];
        clipboard.Text = "https://ornek.test";
        qr.Commands.Single(c => c.Title == "QR oluştur").Execute!(CancellationToken.None).GetAwaiter().GetResult();
        Equal("https://ornek.test", qr.PendingInput, "QR command stores the clipboard text for the page");

        // Views build with the shared theme (a standalone Application stands in for the shell).
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Resources.MergedDictionaries.Any()) app.Resources.MergedDictionaries.Add(ThemeResources.Load());
        foreach (UtilityModuleBase m in modules)
        {
            FrameworkElement view = m.CreateView();
            view.Measure(new Size(900, 700));
            Assert(view.DesiredSize.Width > 0 && view.DesiredSize.Height > 0, "View lays out: " + m.Id);
        }

        // Settings persist.
        ((ColorPickerModule)color).SetDefaultFormat(ColorFormat.Hsl);
        var reloaded = new ColorPickerModule(clipboard);
        reloaded.Initialize(new FakeHost("colorpicker", profile));
        Equal(ColorFormat.Hsl, reloaded.Settings.DefaultFormat, "Default colour format persists");
        ((ColorPickerModule)color).CopyColor(new Rgb(1, 2, 3), ColorFormat.CSharp);
        Equal("Color.FromRgb(1, 2, 3)", clipboard.Text, "CopyColor uses the requested format");

        foreach (UtilityModuleBase m in modules) m.Shutdown();
    }

    // ---- optional: real screen (flag) ---------------------------------------------------------------------------

    private static void ScreenOverlayTests()
    {
        ScreenSnapshot snapshot = ScreenSnapshot.Capture();
        Assert(snapshot.Bounds.Width > 0 && snapshot.Bounds.Height > 0 && snapshot.Bgra.Length >= snapshot.Bounds.Width * snapshot.Bounds.Height * 4, "Desktop captured");
        Assert(snapshot.GetPixel(snapshot.Bounds.X, snapshot.Bounds.Y) is not null, "Top-left pixel readable");
        Console.WriteLine($"  desktop {snapshot.Bounds}");

        var overlays = new Func<Window>[]
        {
            () => new ColorPickerOverlay(snapshot),
            () => new RulerOverlay(snapshot),
            () => new RegionSelectOverlay(snapshot, "deneme")
        };
        foreach (var make in overlays)
        {
            Window window = make();
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, _) => { timer.Stop(); Console.WriteLine($"  before close: {window.Left},{window.Top} {window.Width}x{window.Height} actual {window.ActualWidth}x{window.ActualHeight}"); window.Close(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            Assert(closed, "Overlay closes: " + window.GetType().Name);
            Console.WriteLine($"  {window.GetType().Name}: Left={window.Left} Top={window.Top} Width={window.Width} Height={window.Height} Actual={window.ActualWidth}x{window.ActualHeight}");
        }
    }

    // ---- icon glyphs -----------------------------------------------------------------------------------------------

    private static System.Windows.Media.GlyphTypeface? LoadIconFont()
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string file in new[] { "SegoeIcons.ttf", "segmdl2.ttf" })
        {
            string path = Path.Combine(fonts, file);
            if (File.Exists(path)) return new System.Windows.Media.GlyphTypeface(new Uri(path));
        }
        return null;
    }

    /// <summary>Every icon-font glyph on a page (IconText blocks, IconButton content, ThemeAssist.Icon) as "U+XXXX (where)".</summary>
    private static IEnumerable<(int Code, string Where)> IconGlyphs(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            if (!seen.Add(node)) continue;
            string? icon = ThemeAssist.GetIcon(node);
            if (!string.IsNullOrEmpty(icon))
                foreach (char ch in icon) yield return (ch, node.GetType().Name + " ThemeAssist.Icon");
            bool iconFont(System.Windows.Media.FontFamily f) => f.Source.Contains("Icons", StringComparison.Ordinal) || f.Source.Contains("MDL2", StringComparison.Ordinal);
            if (node is TextBlock text && iconFont(text.FontFamily))
                foreach (char ch in text.Text) yield return (ch, "TextBlock " + text.Text);
            if (node is ContentControl { Content: string s } control && iconFont(control.FontFamily))
                foreach (char ch in s) yield return (ch, control.GetType().Name + " content");
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) stack.Push(d);
            if (node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++) stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
    }

    private static void IconGlyphTests()
    {
        System.Windows.Media.GlyphTypeface? font = LoadIconFont();
        if (font is null) { Console.WriteLine("  icon font not installed; glyph check skipped"); return; }
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Resources.MergedDictionaries.Any()) app.Resources.MergedDictionaries.Add(ThemeResources.Load());
        var clipboard = new FakeClipboard();
        foreach (UtilityModuleBase m in new UtilityModuleBase[]
                 {
                     new ColorPickerModule(clipboard), new RulerModule(clipboard), new SnippetsModule(clipboard),
                     new QrModule(clipboard), new TextToolsModule(clipboard), new BatchFilesModule()
                 })
        {
            m.Initialize(new FakeHost(m.Id, Path.Combine(root, "glyphs")));
            Assert(m.Icon.All(c => font.CharacterToGlyphMap.ContainsKey(c)), "Module icon exists in the icon font: " + m.Id);
            FrameworkElement view = m.CreateView();
            view.Measure(new Size(1100, 760));
            view.Arrange(new Rect(0, 0, 1100, 760));
            int count = 0;
            foreach ((int code, string where) in IconGlyphs(view))
            {
                if (char.IsWhiteSpace((char)code)) continue;
                Assert(font.CharacterToGlyphMap.ContainsKey(code), $"{m.Id}: U+{code:X4} missing from the icon font ({where})");
                count++;
            }
            Assert(count > 0, "Page uses icon glyphs: " + m.Id);
            m.Shutdown();
        }
    }

    // ---- optional: render pages to PNG for a visual check (flag) -----------------------------------------------

    private const int PageWidth = 1100, PageHeight = 760;

    /// <summary>Runs the dispatcher until the task finishes (async UI code in a console host).</summary>
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Wraps a page like the shell does: page padding, and a ScrollViewer when the page is a plain stack.</summary>
    private static FrameworkElement ShellPage(FrameworkElement view, Application app)
    {
        var padded = new Border { Child = view, Padding = (Thickness)app.Resources["PagePadding"] };
        object? content = view is ContentControl control ? control.Content : view;
        if (content is not (StackPanel or WrapPanel)) return padded;
        return new ScrollViewer { Content = padded, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static void SavePng(Window window, string path)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Window HostWindow(Application app, object content, int width = PageWidth, int height = PageHeight)
    {
        var window = new Window
        {
            Width = width, Height = height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Left = -6000, Top = 0, UseLayoutRounding = true, SnapsToDevicePixels = true,
            Background = (System.Windows.Media.Brush)app.Resources["WindowBackgroundBrush"],
            Foreground = (System.Windows.Media.Brush)app.Resources["TextBrush"],
            FontFamily = (System.Windows.Media.FontFamily)app.Resources["UiFont"], FontSize = 14,
            Content = content
        };
        System.Windows.Media.TextOptions.SetTextFormattingMode(window, System.Windows.Media.TextFormattingMode.Display);
        System.Windows.Media.TextOptions.SetTextRenderingMode(window, System.Windows.Media.TextRenderingMode.ClearType);
        return window;
    }

    private static void RenderPage(Application app, string outputFolder, string name, FrameworkElement view, Action<FrameworkElement>? afterShow = null)
    {
        Window window = HostWindow(app, ShellPage(view, app));
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        afterShow?.Invoke(view);
        SavePng(window, Path.Combine(outputFolder, name + ".png"));
        window.Close();
        Console.WriteLine("  " + Path.Combine(outputFolder, name + ".png"));
    }

    private static void RenderViews(string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Resources.MergedDictionaries.Any()) app.Resources.MergedDictionaries.Add(ThemeResources.Load());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var clipboard = new FakeClipboard { Text = "https://ornek.test" };
        string data = Path.Combine(root, "render");
        T Init<T>(T module) where T : UtilityModuleBase { module.Initialize(new FakeHost(module.Id + "-" + Guid.NewGuid().ToString("N")[..4], data)); return module; }

        // Colour picker: a realistic history (first = current colour), and the empty first-run state.
        var color = Init(new ColorPickerModule(clipboard));
        color.Settings.History = new List<string>
        {
            "#E5484D", "#3E63DD", "#30A46C", "#F5D90A", "#8E4EC6", "#12A594", "#F76B15", "#1C2024", "#FFFFFF", "#D6409F",
            "#0090FF", "#46A758", "#FFC53D", "#6E56CF", "#E54666", "#AD7F58", "#8D8D86", "#123456", "#FFAA00", "#2A2A31"
        };
        RenderPage(app, outputFolder, "colorpicker", color.CreateView());
        RenderPage(app, outputFolder, "colorpicker-empty", Init(new ColorPickerModule(clipboard)).CreateView());

        // Ruler with recent measurements.
        var ruler = Init(new RulerModule(clipboard));
        ruler.Settings.Recent = new List<string>
        {
            "1280 × 720 px, çapraz 1468.6 px", "300 × 200 px, çapraz 360.56 px", "48 × 48 px, çapraz 67.88 px", "1 × 640 px, çapraz 640 px", "212 × 0 px, çapraz 212 px"
        };
        RenderPage(app, outputFolder, "ruler", ruler.CreateView());
        RenderPage(app, outputFolder, "ruler-empty", Init(new RulerModule(clipboard)).CreateView());

        // Snippets: several saved, one open in the editor.
        var snippets = Init(new SnippetsModule(clipboard));
        foreach (Snippet s in new[]
                 {
                     new Snippet { Name = "E-posta imzası", Trigger = "imza", Folder = "İş", Content = "Saygılarımla,\nAyşe Yılmaz\nÜrün Tasarımı · BroidShot" },
                     new Snippet { Name = "Toplantı notu başlığı", Trigger = "toplanti", Folder = "İş", Content = "# Toplantı — {tarih}\nKatılımcılar:\nKararlar:" },
                     new Snippet { Name = "Adres", Trigger = "adres", Folder = "Kişisel", Content = "Moda Cad. No: 12 D: 4, Kadıköy / İstanbul" },
                     new Snippet { Name = "Hata raporu şablonu", Trigger = "", Folder = "Destek", Content = "Adımlar:\n1.\n2.\nBeklenen:\nGerçekleşen:" },
                     new Snippet { Name = "Teşekkür", Trigger = "tsk", Folder = "", Content = "Çok teşekkürler, elinize sağlık!" },
                 })
            snippets.Store.Save(s, out _);
        var snippetView = (SnippetsView)snippets.CreateView();
        RenderPage(app, outputFolder, "snippets", snippetView, v => { var view = (SnippetsView)v; view.List.SelectedIndex = 1; });

        // QR: a decoded result on "Oku", and the generator on "Oluştur".
        var qr = Init(new QrModule(clipboard));
        var qrView = (QrView)qr.CreateView();
        RenderPage(app, outputFolder, "qr-read", qrView, _ =>
        {
            RawImage code = QrService.Generate("https://broidshot.app/indir?kaynak=qr", 256, QrErrorLevel.Medium);
            Pump(qr.DecodeAsync(code, "Ekran alanı", copy: false));
        });
        var qr2 = Init(new QrModule(clipboard));
        qr2.PendingInput = "https://broidshot.app/indir?kaynak=qr";
        RenderPage(app, outputFolder, "qr-create", qr2.CreateView());

        // Text tools: JSON formatted.
        var text = Init(new TextToolsModule(clipboard));
        var textView = (TextToolsView)text.CreateView();
        textView.InputBox.Text = "{\"ad\":\"BroidShot\",\"sürüm\":\"0.3\",\"araçlar\":[\"renk seçici\",\"cetvel\",\"QR\"],\"yerel\":true}";
        RenderPage(app, outputFolder, "texttools", textView, v =>
        {
            Button format = ((TextToolsView)v).OperationPanel.Children.OfType<Button>().First();
            format.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, format));
        });

        // Batch files: rename preview with one conflict, organize preview, zip list.
        string photos = NewFolder("tatil");
        var files = new List<string>();
        foreach (string n in new[] { "IMG_20240712_093015.jpg", "IMG_20240712_101233.jpg", "IMG_20240713_174502.jpg", "IMG_20240714_081120.jpg", "DSC00421.JPG", "ekran görüntüsü.png", "notlar.txt" })
            files.Add(Touch(photos, n));
        Touch(photos, "Tatil_003.jpg");
        var batch = Init(new BatchFilesModule());
        var batchView = (BatchFilesView)batch.CreateView();
        RenderPage(app, outputFolder, "batchfiles-empty", Init(new BatchFilesModule()).CreateView());
        batchView.AddRenameFiles(files);
        batchView.PatternBox.Text = "Tatil_{n:000}";
        RenderPage(app, outputFolder, "batchfiles-rename", batchView, _ => Wait(400));

        string downloads = NewFolder("indirilenler");
        foreach (string n in new[] { "fatura_ekim.pdf", "sunum.pptx", "tatil.mp4", "logo.svg", "kurulum.zip", "şarkı.mp3", "rapor.docx", "veri.csv", "ekran.png" })
            Touch(downloads, n);
        var organize = Init(new BatchFilesModule());
        organize.PendingTab = BatchTab.Organize;
        var organizeView = (BatchFilesView)organize.CreateView();
        RenderPage(app, outputFolder, "batchfiles-organize", organizeView, v => Pump(((BatchFilesView)v).LoadOrganizeFolderAsync(downloads)));

        var zip = Init(new BatchFilesModule());
        zip.PendingTab = BatchTab.Zip;
        var zipView = (BatchFilesView)zip.CreateView();
        zipView.AddZipItems(new[] { files[0], files[1], files[4], downloads });
        RenderPage(app, outputFolder, "batchfiles-zip", zipView);

        RenderOverlays(app, outputFolder);
    }

    /// <summary>A fake desktop: window-like panels on a wallpaper gradient, so overlays can be judged without the real screen.</summary>
    private static ScreenSnapshot FakeDesktop(int width, int height)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var wallpaper = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(40, 62, 98), System.Windows.Media.Color.FromRgb(120, 70, 90), 35);
            dc.DrawRectangle(wallpaper, null, new Rect(0, 0, width, height));
            dc.DrawRoundedRectangle(System.Windows.Media.Brushes.WhiteSmoke, null, new Rect(90, 90, 620, 420), 8, 8);
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 230, 236)), null, new Rect(90, 90, 620, 36));
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 99, 221)), null, new Rect(130, 160, 220, 120));
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 72, 77)), null, new Rect(370, 160, 300, 40));
            for (int i = 0; i < 6; i++)
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 180, 188)), null, new Rect(370, 220 + i * 22, 260 - i * 25, 10));
            dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 28, 32)), null, new Rect(760, 300, 290, 380), 8, 8);
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 164, 108)), null, new Rect(790, 340, 120, 120));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] bgra = new byte[width * height * 4];
        bitmap.CopyPixels(bgra, width * 4, 0);
        return new ScreenSnapshot(new PixelRect(0, 0, width, height), bgra);
    }

    /// <summary>Renders the overlays' content off-screen (the windows themselves are never shown, so nothing covers the real desktop).</summary>
    private static void RenderOverlays(Application app, string outputFolder)
    {
        ScreenSnapshot desktop = FakeDesktop(PageWidth, PageHeight);
        var ruler = new RulerOverlay(desktop);
        ruler.ShowMeasurement(240, 420, 640, 220, 1.0);
        var picker = new ColorPickerOverlay(desktop);
        picker.ShowLoupeAt(250, 200, 1.0);
        var region = new RegionSelectOverlay(desktop, "QR kodunu içine alacak şekilde sürükle");
        region.ShowSelection(new Rect(770, 320, 160, 160), 1.0);
        foreach ((string name, Window overlay) in new (string, Window)[] { ("overlay-ruler", ruler), ("overlay-colorpicker", picker), ("overlay-region", region) })
        {
            object content = overlay.Content;
            overlay.Content = null;
            Window window = HostWindow(app, content);
            window.Show();
            SavePng(window, Path.Combine(outputFolder, name + ".png"));
            window.Close();
            overlay.Close();
            Console.WriteLine("  " + Path.Combine(outputFolder, name + ".png"));
        }
    }
}
