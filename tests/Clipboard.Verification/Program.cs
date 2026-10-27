using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BroidShot.Core;
using BroidShot.Modules.Clipboard;
using BroidShot.Modules.Clipboard.Native;
using BroidShot.Modules.Clipboard.Storage;

// Clipboard module checks. Default run never touches the real system clipboard;
// pass --clipboard-verify to add a live round-trip through the Windows clipboard,
// and --hook-verify to briefly install (and remove) a real low-level keyboard hook.
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
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; // registers pack:// (theme URIs)
        ThemeResources.ApplyDefaults(); // like the app: windows created by the module get the theme font
        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("Classifier: link, e-mail, color, code, path, number, text", Sync(Classifier)),
            ("Video links: YouTube, Instagram, TikTok, X, Vimeo, Twitch clips", Sync(VideoLinks)),
            ("Sensitive detection: tokens, keys, passwords, Luhn cards, IBAN (TR) and false positives", Sync(SensitiveDetection)),
            ("Masking never reveals characters, and sensitive items are not searchable by content", Sync(Masking)),
            ("DPAPI CurrentUser round-trip and tamper detection", Sync(Dpapi)),
            ("Store round-trip (text+HTML, files, image) in a temp data dir, encrypted at rest", Sync(StoreRoundTrip)),
            ("Dedupe: identical copy moves to top and keeps id, tags and pin", Sync(Dedupe)),
            ("Retention: max items and max age, pinned items exempt", Sync(Retention)),
            ("Sensitive items expire 1 hour after capture (memory, database, image file, startup cleanup)", Sync(SensitiveExpiry)),
            ("Tags: create, rename, delete, multiple per item, Turkish-aware search and filters", Sync(TagsAndFilters)),
            ("Win+V filter: swallow/open/pass decisions for every key state (no hook installed)", Sync(WinVFilter)),
            ("Win+V takeover glue: Start menu mask, foreground capture, injected events ignored (no hook installed)", Sync(WinVTakeoverGlue)),
            ("Win+V settings: Ctrl+Shift+V migrates, toggle off keeps Windows' Win+V, JSON round-trip", WinVSettings),
            ("Module pipeline: UrlCopiedMessage, sensitive never published, blocklist, monitoring off, logs", ModulePipeline),
            ("Page and quick panel build with the theme; sensitive rows stay masked until revealed", Sync(ViewSmoke)),
            ("Render: page, settings and quick panel PNGs with realistic items; every icon glyph exists", Sync(RenderPages)),
        };
        if (args.Contains("--clipboard-verify"))
            tests.Add(("LIVE: real clipboard capture and own-change suppression", Sync(LiveClipboard)));
        if (args.Contains("--hook-verify"))
            tests.Add(("LIVE: low-level keyboard hook installs on its own thread and unhooks (keys are passed through)", Sync(LiveKeyboardHook)));

        int failures = 0;
        foreach (var test in tests)
        {
            try { test.Run().GetAwaiter().GetResult(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
        }
        Console.WriteLine($"Clipboard verification: {tests.Count - failures}/{tests.Count} passed, {assertions} assertions; "
            + (args.Contains("--clipboard-verify") ? "the real clipboard was used" : "the real clipboard was not touched")
            + (args.Contains("--hook-verify") ? "; a real keyboard hook was installed briefly." : "; no keyboard hook was installed."));
        return failures == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private static string NewFolder(string name)
    {
        string path = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static ClipboardHistory OpenHistory(string folder, TimeProvider time, RetentionPolicy? retention = null)
    {
        var history = new ClipboardHistory(new ClipStore(folder, DpapiContentProtector.Instance), time) { Retention = retention ?? RetentionPolicy.Default };
        history.Load();
        return history;
    }

    private static ClipCapture Text(string text, string? source = "notepad", bool sensitiveFormat = false) =>
        new() { Text = text, SourceApp = source, SensitiveFormat = sensitiveFormat };

    private static ClipCapture Pixels(int width, int height, byte shade, bool sensitiveFormat = false)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = shade; pixels[i + 1] = (byte)(i % 251); pixels[i + 2] = 40; pixels[i + 3] = 255; }
        return new ClipCapture { ImagePixels = pixels, ImageWidth = width, ImageHeight = height, SourceApp = "mspaint", SensitiveFormat = sensitiveFormat };
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // ---------------------------------------------------------------------------------------------

    private static void Classifier()
    {
        var cases = new (string Text, ClipKind Kind)[]
        {
            ("https://example.com/a?b=1", ClipKind.Link),
            ("  http://localhost:8080/api  ", ClipKind.Link),
            ("www.ornek.com.tr", ClipKind.Link),
            ("ftp://files.example.org/x.zip", ClipKind.Link),
            ("ayse.yilmaz@example.com.tr", ClipKind.Email),
            ("mailto:info@example.com", ClipKind.Email),
            ("#fff", ClipKind.Color),
            ("#D94046", ClipKind.Color),
            ("#d9404680", ClipKind.Color),
            ("rgb(255, 0, 0)", ClipKind.Color),
            ("rgba(0,0,0,0.5)", ClipKind.Color),
            ("hsl(120 50% 50%)", ClipKind.Color),
            (@"C:\Users\ayse\Belgeler\rapor.docx", ClipKind.FilePath),
            (@"""C:\Program Files\App\app.exe""", ClipKind.FilePath),
            (@"\\sunucu\paylasim\klasor", ClipKind.FilePath),
            (@"%APPDATA%\BroidShot\settings.json", ClipKind.FilePath),
            ("42", ClipKind.Number),
            ("-3,14", ClipKind.Number),
            ("1.234.567,89", ClipKind.Number),
            ("1e-5", ClipKind.Number),
            ("%15", ClipKind.Text),
            ("0x1F", ClipKind.Number),
            ("public static void Main()\n{\n    Console.WriteLine(1);\n}", ClipKind.Code),
            ("{\"ad\": \"Ayşe\", \"yas\": [1, 2]}", ClipKind.Code),
            ("console.log(value);", ClipKind.Code),
            ("SELECT id, ad FROM kullanicilar WHERE id = 1", ClipKind.Code),
            ("def topla(a, b):\n    return a + b\n", ClipKind.Code),
            ("<div class=\"x\">Merhaba</div>", ClipKind.Code),
            ("Merhaba dünya, nasılsın?", ClipKind.Text),
            ("Bugün hava güzel.\nYarın yağmur bekleniyor.", ClipKind.Text),
            ("#etiket", ClipKind.Text),
            ("12 elma", ClipKind.Text),
            ("Toplantı saat 14:30'da; gündem ekte.", ClipKind.Text),
            ("", ClipKind.Text),
        };
        foreach (var (text, kind) in cases)
        {
            ClipKind actual = ClipClassifier.Classify(text);
            Assert(actual == kind, $"Classify(\"{text}\") = {actual}, expected {kind}.");
        }
        Assert(ClipClassifier.TryGetUrl("www.ornek.com", out string url) && url == "https://www.ornek.com", "www. addresses are normalized to https.");
        Assert(!ClipClassifier.TryGetUrl("javascript:alert(1)", out _) && !ClipClassifier.TryGetUrl("file:///C:/x", out _), "Only web URLs count as links.");
        Assert(!ClipClassifier.TryGetUrl("https://example.com bak", out _), "Text with spaces is not a link.");
    }

    private static void VideoLinks()
    {
        string[] videos =
        {
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://youtu.be/dQw4w9WgXcQ", "https://m.youtube.com/shorts/abc123",
            "https://music.youtube.com/watch?v=abc", "https://www.instagram.com/reel/Cxyz123/", "https://instagram.com/p/Babc/",
            "https://www.tiktok.com/@kullanici/video/7234567890123456789", "https://vm.tiktok.com/ZMabc123/",
            "https://x.com/kullanici/status/1790000000000000000", "https://twitter.com/kullanici/status/123",
            "https://vimeo.com/76979871", "https://player.vimeo.com/video/76979871", "https://clips.twitch.tv/FunnyClipName",
            "https://www.twitch.tv/yayinci/clip/FunnyClipName", "https://www.twitch.tv/videos/123456789",
        };
        foreach (string link in videos)
        {
            Assert(ClipClassifier.Classify(link) == ClipKind.Link, "Video URL is a link: " + link);
            Assert(ClipClassifier.IsVideoUrl(link), "Video URL detected: " + link);
        }
        string[] others =
        {
            "https://www.youtube.com/", "https://x.com/kullanici", "https://www.instagram.com/kullanici/", "https://www.twitch.tv/yayinci",
            "https://example.com/video.mp4", "https://vimeo.com/about", "https://notyoutube.com/watch?v=1", "https://youtube.com.evil.example/watch?v=1",
        };
        foreach (string link in others) Assert(!ClipClassifier.IsVideoUrl(link), "Not a video link: " + link);
    }

    // Deliberately synthetic fixtures: no provider credential or private key is embedded in source.
    private static string SyntheticKey(string prefix, int length) => prefix + new string('A', length);
    private static string SyntheticJwt()
    {
        static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("{\"alg\":\"HS256\",\"typ\":\"JWT\"}") + "." + Encode("{\"fixture\":true}") + "." + new string('x', 32);
    }
    private static string SyntheticPem(string type) => string.Join("\n", "-----" + "BEGIN " + type + " PRIVATE " + "KEY-----", "SYNTHETIC TEST FIXTURE", "-----" + "END " + type + " PRIVATE " + "KEY-----");

    private static void SensitiveDetection()
    {
        var positives = new (string Text, SensitiveReason Reason)[]
        {
            (SyntheticJwt(), SensitiveReason.Jwt),
            ("Authorization: Bearer " + SyntheticJwt(), SensitiveReason.Jwt),
            (SyntheticKey("sk-proj-", 32), SensitiveReason.ApiKey),
            ("OPENAI anahtarım " + SyntheticKey("sk-ant-api03-", 32) + " burada", SensitiveReason.ApiKey),
            (SyntheticKey("ghp_", 36), SensitiveReason.ApiKey),
            (SyntheticKey("github_pat_", 60), SensitiveReason.ApiKey),
            (SyntheticKey("xoxb-", 32), SensitiveReason.ApiKey),
            (SyntheticKey("AKIA", 16), SensitiveReason.ApiKey),
            (SyntheticKey("sk_live_", 32), SensitiveReason.ApiKey),
            (SyntheticPem("RSA"), SensitiveReason.PrivateKey),
            (SyntheticPem("OPENSSH"), SensitiveReason.PrivateKey),
            ("password: hunter22", SensitiveReason.Password),
            ("DB_PASSWORD=S3cr3t!x", SensitiveReason.Password),
            ("şifre=Gizli123", SensitiveReason.Password),
            ("api_key = \"abcd1234efgh\"", SensitiveReason.Password),
            ("4111 1111 1111 1111", SensitiveReason.CreditCard),
            ("Kart: 5555-5555-5555-4444, SKT 12/28", SensitiveReason.CreditCard),
            ("4012888888881881", SensitiveReason.CreditCard),
            ("378282246310005", SensitiveReason.CreditCard),
            ("TR33 0006 1005 1978 6457 8413 26", SensitiveReason.Iban),
            ("TR330006100519786457841326", SensitiveReason.Iban),
            ("IBAN: tr33 0006 1005 1978 6457 8413 26 numaralı hesaba gönder", SensitiveReason.Iban),
            ("DE89 3704 0044 0532 0130 00", SensitiveReason.Iban),
            ("GB82WEST12345698765432", SensitiveReason.Iban),
            ("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", SensitiveReason.Secret),
            ("Zx9kQ2mP7vR4tY8wL1nB5cJ3hF6gD0sA", SensitiveReason.Secret),
            ("dGhpc0lzQVNlY3JldEtleUZvclRlc3Q5ODc2NTQzMjE=", SensitiveReason.Secret),
        };
        foreach (var (text, reason) in positives)
        {
            SensitiveReason actual = SensitiveDetector.Detect(text);
            Assert(actual == reason, $"Detect(case {Array.FindIndex(positives, p => p.Text == text)}) = {actual}, expected {reason}.");
        }
        string[] negatives =
        {
            "Merhaba dünya", "4111 1111 1111 1112", "0000 0000 0000 0000", "1234 5678 9012 3456",
            "TR34 0006 1005 1978 6457 8413 26", "TR33 0006 1005 1978 6457 8413", "DE88 3704 0044 0532 0130 00",
            "+90 532 123 45 67", "e83c5163316f89bfbde7d9ab23ca2e25604af290", "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
            "https://example.com/path/to/some/very/long/page/that/keeps/going", "Toplantı 14:30'da, şifre değil.",
            "Muvaffakiyetsizleştiricileştiriveremeyebileceklerimizdenmişsinizcesine", "1234567890", "Sipariş no 20261027-0001",
            "token yok", "C:\\Users\\ayse\\AppData\\Local\\Temp\\abcdefabcdefabcdefabcdefabcdefab", "skip-this-is-not-a-key",
        };
        foreach (string text in negatives)
            Assert(SensitiveDetector.Detect(text) == SensitiveReason.None, $"Not sensitive (case {Array.IndexOf(negatives, text)}).");

        Assert(SensitiveDetector.LuhnValid("79927398713") && !SensitiveDetector.LuhnValid("79927398710"), "Luhn reference values.");
        Assert(SensitiveDetector.LuhnValid("4111111111111111") && !SensitiveDetector.LuhnValid("4111111111111112"), "Luhn on card numbers.");
        Assert(!SensitiveDetector.LuhnValid("") && !SensitiveDetector.LuhnValid("41x1"), "Luhn rejects empty/non-digit input.");
        Assert(SensitiveDetector.IsValidIban("TR330006100519786457841326"), "Valid TR IBAN.");
        Assert(!SensitiveDetector.IsValidIban("TR3300061005197864578413261"), "TR IBAN must be 26 characters.");
        Assert(!SensitiveDetector.IsValidIban("TR320006100519786457841326"), "TR IBAN with a wrong check digit.");
        Assert(SensitiveDetector.IsValidIban("NL91 ABNA 0417 1643 00") && !SensitiveDetector.IsValidIban("NL91 ABNA 0417 1643 01"), "Mod-97 check.");
    }

    private static void Masking()
    {
        string secret = SyntheticKey("ghp_", 36);
        string mask = SensitiveDetector.Mask(secret);
        Assert(mask.Length is >= 6 and <= 16 && mask.All(c => c == '•'), "Mask is only bullets with a capped length.");
        Assert(SensitiveDetector.Mask("ab").Length == 6 && SensitiveDetector.Mask(new string('x', 500)).Length == 16, "Mask length does not reveal the content length.");

        using ClipboardHistory history = OpenHistory(NewFolder("mask"), new FakeTime());
        CaptureResult result = history.Add(Text(secret));
        ClipEntry entry = result.Entry!;
        Assert(entry.Sensitive && entry.Reason == SensitiveReason.ApiKey, "Token stored as sensitive.");
        Assert(entry.Payload is null && entry.ProtectedPayload is { Length: > 0 }, "Sensitive content is kept encrypted in memory.");
        Assert(!entry.Preview.Contains("ghp", StringComparison.Ordinal) && entry.Preview.All(c => c == '•'), "Preview is masked.");
        Assert(entry.SearchText.Length == 0, "Sensitive content is not indexed for search.");
        Assert(!ClipFilter.Apply(history.Items, secret.Substring(5, 6), ClipFilterKind.All, null, history.Tags).Any(), "Searching a fragment of the secret finds nothing.");
        Assert(ClipFilter.Apply(history.Items, "", ClipFilterKind.Sensitive, null, history.Tags).Count() == 1, "Sensitive filter chip finds it.");
        Assert(history.GetPayload(entry.Id)?.Text == secret, "Explicit reveal/copy decrypts the original.");
    }

    private static void Dpapi()
    {
        byte[] plain = Encoding.UTF8.GetBytes("Gizli pano içeriği: ğüşıöç ÇĞÜŞİÖ");
        byte[] protectedBytes = DpapiContentProtector.Instance.Protect(plain);
        Assert(!protectedBytes.SequenceEqual(plain) && !Contains(protectedBytes, plain[..10]), "DPAPI output does not contain the plaintext.");
        Assert(DpapiContentProtector.Instance.Unprotect(protectedBytes).SequenceEqual(plain), "DPAPI round-trip.");
        Assert(!DpapiContentProtector.Instance.Protect(plain).SequenceEqual(protectedBytes), "DPAPI output is randomized per call.");
        byte[] tampered = (byte[])protectedBytes.Clone();
        tampered[^5] ^= 0x5A;
        bool rejected = false;
        try { DpapiContentProtector.Instance.Unprotect(tampered); }
        catch (CryptographicException) { rejected = true; }
        Assert(rejected, "Tampered DPAPI blob is rejected.");
        bool otherEntropy = false;
        try { ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { otherEntropy = true; }
        Assert(otherEntropy, "Blobs are bound to the module's entropy.");
    }

    private static void StoreRoundTrip()
    {
        string folder = NewFolder("store");
        var time = new FakeTime();
        const string marker = "BENZERSIZ-isaret-7781 çalışma notu";
        string html = "Version:0.9\r\nStartHTML:0\r\n<html><body><!--StartFragment--><b>" + marker + "</b><!--EndFragment--></body></html>";
        long textId, filesId, imageId;
        using (ClipboardHistory history = OpenHistory(folder, time))
        {
            textId = history.Add(new ClipCapture { Text = marker, Html = html, SourceApp = "chrome" }).Entry!.Id;
            time.Advance(TimeSpan.FromSeconds(1));
            filesId = history.Add(new ClipCapture { Files = new[] { @"C:\Belgeler\rapor.pdf", @"C:\Belgeler\tablo.xlsx" }, SourceApp = "explorer" }).Entry!.Id;
            time.Advance(TimeSpan.FromSeconds(1));
            imageId = history.Add(Pixels(4, 3, 200)).Entry!.Id;
            time.Advance(TimeSpan.FromSeconds(1));
            ClipEntry htmlOnly = history.Add(new ClipCapture { Html = "<html><body><!--StartFragment--><p>Yalnız&nbsp;HTML</p><!--EndFragment--></body></html>" }).Entry!;
            Assert(htmlOnly.Payload?.Text == "Yalnız HTML" && htmlOnly.Payload.Html is not null, "HTML-only copies keep a plain-text version alongside.");
        }

        using (ClipboardHistory reopened = OpenHistory(folder, time))
        {
            Assert(reopened.Items.Count == 4, "All items survive a restart.");
            Assert(reopened.Items[0].Kind == ClipKind.Text && reopened.Items[1].Id == imageId, "Order is most recent first.");
            ClipPayload text = reopened.GetPayload(textId)!;
            Assert(text.Text == marker && text.Html == html, "Text and HTML round-trip.");
            Assert(reopened.Find(textId)!.SourceApp == "chrome", "Source app round-trips.");
            ClipEntry files = reopened.Find(filesId)!;
            Assert(files.Kind == ClipKind.Files && files.Payload!.Files!.SequenceEqual(new[] { @"C:\Belgeler\rapor.pdf", @"C:\Belgeler\tablo.xlsx" }), "File lists round-trip.");
            Assert(files.Preview.Contains("rapor.pdf") && files.Preview.Contains("2 dosya"), "File preview lists names.");
            ClipEntry image = reopened.Find(imageId)!;
            Assert(image.Kind == ClipKind.Image && image.Width == 4 && image.Height == 3, "Image size round-trips.");
            byte[] png = reopened.GetImagePng(imageId)!;
            Assert(png.Length > 8 && png[0] == 0x89 && png[1] == (byte)'P', "Image decrypts to PNG.");
            var decoded = ClipboardModule.DecodePng(png, 0);
            Assert(decoded.PixelWidth == 4 && decoded.PixelHeight == 3, "PNG decodes with the original size.");

            string imageFile = Path.Combine(folder, "images", image.ImageFile!);
            byte[] onDisk = File.ReadAllBytes(imageFile);
            Assert(onDisk[0] != 0x89 && !Contains(onDisk, "IHDR"u8.ToArray()), "Image file on disk is encrypted.");
            byte[] db = ReadShared(Path.Combine(folder, "clipboard.db"));
            Assert(!Contains(db, Encoding.UTF8.GetBytes("BENZERSIZ")) && !Contains(db, Encoding.Unicode.GetBytes("BENZERSIZ")), "Database never holds plaintext content.");
            Assert(!Contains(db, Encoding.UTF8.GetBytes("rapor.pdf")), "File paths are encrypted too.");
        }
    }

    private static void Dedupe()
    {
        var time = new FakeTime();
        using ClipboardHistory history = OpenHistory(NewFolder("dedupe"), time);
        ClipEntry a = history.Add(Text("Aynı metin")).Entry!;
        ClipTag tag = history.CreateTag("Önemli");
        history.SetTag(a.Id, tag.Id, true);
        history.SetPinned(a.Id, true);
        time.Advance(TimeSpan.FromMinutes(1));
        history.Add(Text("Başka metin"));
        time.Advance(TimeSpan.FromMinutes(1));
        CaptureResult again = history.Add(Text("Aynı metin", source: "code"));
        Assert(again.Outcome == CaptureOutcome.MovedToTop && again.Entry!.Id == a.Id, "Identical copy reuses the existing item.");
        Assert(history.Items.Count == 2 && history.Items[0].Id == a.Id, "Duplicate moved to the top, no new row.");
        Assert(history.Items[0].LastUsedAt == time.Now && history.Items[0].CreatedAt == a.CreatedAt, "Last-used time updated, created time kept.");
        Assert(history.Items[0].TagIds.Contains(tag.Id) && history.Items[0].Pinned && history.Items[0].SourceApp == "code", "Tags and pin are kept; source updated.");
        CaptureResult consecutive = history.Add(Text("Aynı metin"));
        Assert(consecutive.Outcome == CaptureOutcome.MovedToTop && history.Items.Count == 2, "Consecutive identical copies are deduped.");
        Assert(history.Add(Text("aynı metin")).Outcome == CaptureOutcome.Added, "Different case is different content.");
        history.Add(Pixels(3, 3, 10));
        Assert(history.Add(Pixels(3, 3, 10)).Outcome == CaptureOutcome.MovedToTop, "Identical images are deduped.");
        Assert(history.Add(Pixels(3, 3, 11)).Outcome == CaptureOutcome.Added, "Different images are separate items.");
    }

    private static void Retention()
    {
        string folder = NewFolder("retention");
        var time = new FakeTime();
        using (ClipboardHistory history = OpenHistory(folder, time, new RetentionPolicy(3, TimeSpan.FromDays(30))))
        {
            ClipEntry pinned = history.Add(Text("sabit öğe")).Entry!;
            history.SetPinned(pinned.Id, true);
            for (int i = 1; i <= 5; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                history.Add(Text("öğe " + i));
            }
            string[] texts = history.Items.Select(e => e.Payload!.Text!).ToArray();
            Assert(history.Items.Count == 4 && texts.Contains("sabit öğe") && texts.Contains("öğe 5") && texts.Contains("öğe 3") && !texts.Contains("öğe 2"),
                "Max items keeps the newest unpinned items, plus pinned ones.");

            time.Advance(TimeSpan.FromDays(31));
            history.Add(Text("yeni öğe"));
            texts = history.Items.Select(e => e.Payload!.Text!).ToArray();
            Assert(texts.Length == 2 && texts.Contains("yeni öğe") && texts.Contains("sabit öğe"), "Max age removes old unpinned items, pinned items stay.");

            history.SetPinned(pinned.Id, false);
            history.Retention = new RetentionPolicy(1, TimeSpan.FromDays(30));
            history.ApplyRetention();
            Assert(history.Items.Count == 1 && history.Items[0].Payload!.Text == "yeni öğe", "Unpinning makes an old item subject to retention again.");
            Assert(history.ClearUnpinned() == 1 && history.Items.Count == 0, "Clear removes unpinned items.");
        }

        string folder2 = NewFolder("retention-clear");
        using ClipboardHistory h2 = OpenHistory(folder2, time);
        long keep = h2.Add(Text("kalsın")).Entry!.Id;
        h2.SetPinned(keep, true);
        h2.Add(Text("gitsin 1"));
        h2.Add(Pixels(2, 2, 1));
        string imagesFolder = Path.Combine(folder2, "images");
        Assert(Directory.GetFiles(imagesFolder).Length == 1, "Image file written.");
        Assert(h2.ClearUnpinned() == 2 && h2.Items.Single().Id == keep, "Clear history keeps pinned items.");
        Assert(Directory.GetFiles(imagesFolder).Length == 0, "Clearing deletes image files.");
    }

    private static void SensitiveExpiry()
    {
        string folder = NewFolder("expiry");
        var time = new FakeTime();
        const string secret = "password: Kirmizi-Kaplan-42";
        byte[] protectedPayload;
        string imageFile;
        using (ClipboardHistory history = OpenHistory(folder, time))
        {
            ClipEntry text = history.Add(Text(secret)).Entry!;
            ClipEntry image = history.Add(Pixels(5, 5, 99, sensitiveFormat: true)).Entry!;
            history.Add(Text("sıradan not"));
            protectedPayload = text.ProtectedPayload!;
            imageFile = Path.Combine(folder, "images", image.ImageFile!);
            Assert(text.ExpiresAt == time.Now + TimeSpan.FromHours(1) && image.Reason == SensitiveReason.SourceFlag, "Sensitive items get a 1-hour expiry.");
            Assert(File.Exists(imageFile), "Sensitive image is stored (encrypted) until it expires.");
            Assert(Contains(ReadShared(Path.Combine(folder, "clipboard.db")), protectedPayload), "Encrypted payload is in the database before expiry.");
            Assert(history.NextExpiry == time.Now + TimeSpan.FromHours(1), "Next expiry is reported for scheduling.");

            time.Advance(TimeSpan.FromMinutes(59));
            Assert(history.PurgeExpired() == 0 && history.Items.Count == 3, "Nothing is removed before one hour.");
            history.Touch(text.Id);
            Assert(history.Find(text.Id)!.ExpiresAt == text.ExpiresAt, "Using a sensitive item does not extend its lifetime.");
            time.Advance(TimeSpan.FromMinutes(1));
            Assert(history.PurgeExpired() == 2, "Both sensitive items are removed at one hour.");
            Assert(history.Items.Count == 1 && history.Items[0].Payload!.Text == "sıradan not", "Normal items stay.");
            Assert(history.Find(text.Id) is null && history.GetPayload(text.Id) is null, "Removed from memory.");
            Assert(!File.Exists(imageFile), "Encrypted image file deleted from disk.");
        }
        byte[] db = ReadShared(Path.Combine(folder, "clipboard.db"));
        Assert(!Contains(db, protectedPayload), "secure_delete: the encrypted payload is gone from the database file.");
        Assert(!Contains(db, Encoding.UTF8.GetBytes("Kirmizi")), "Plaintext was never written.");
        using (var store = new ClipStore(folder, DpapiContentProtector.Instance))
            Assert(store.CountItems() == 1, "Only the normal item remains in the database.");

        // Startup cleanup: expiry passes while the app is closed.
        string folder2 = NewFolder("expiry-startup");
        using (ClipboardHistory history = OpenHistory(folder2, time))
        {
            history.Add(Text("4111 1111 1111 1111"));
            history.Add(Text("kalıcı not"));
        }
        time.Advance(TimeSpan.FromMinutes(61));
        using (ClipboardHistory reopened = OpenHistory(folder2, time))
            Assert(reopened.Items.Count == 1 && !reopened.Items.Any(i => i.Sensitive), "Expired sensitive items are deleted on startup.");
        using (var store = new ClipStore(folder2, DpapiContentProtector.Instance))
            Assert(store.CountItems() == 1, "Startup cleanup removed the row from disk.");
    }

    private static void TagsAndFilters()
    {
        string folder = NewFolder("tags");
        var time = new FakeTime();
        long greeting, link;
        using (ClipboardHistory history = OpenHistory(folder, time))
        {
            ClipTag work = history.CreateTag("İş");
            ClipTag personal = history.CreateTag("  kişisel  ");
            Assert(personal.Name == "kişisel", "Tag names are trimmed.");
            bool duplicate = false;
            try { history.CreateTag("iş"); } catch (InvalidOperationException) { duplicate = true; }
            Assert(duplicate, "Tag names are unique, Turkish case-insensitive (İş = iş).");
            bool empty = false;
            try { history.CreateTag("   "); } catch (ArgumentException) { empty = true; }
            Assert(empty, "Empty tag names are rejected.");

            greeting = history.Add(Text("Günaydın dünya, toplantı ŞUBAT ayında")).Entry!.Id;
            link = history.Add(Text("https://youtu.be/abc123")).Entry!.Id;
            long mail = history.Add(Text("ayse@example.com")).Entry!.Id;
            history.SetTag(greeting, work.Id, true);
            history.SetTag(greeting, personal.Id, true);
            history.SetTag(link, personal.Id, true);
            Assert(history.Find(greeting)!.TagIds.Count == 2, "An item can carry multiple tags.");

            IReadOnlyList<ClipTag> tags = history.Tags;
            List<long> Ids(string? query, ClipFilterKind kind = ClipFilterKind.All, long? tag = null) =>
                ClipFilter.Apply(history.Items, query, kind, tag, tags).Select(e => e.Id).ToList();

            Assert(Ids("gunaydin").SequenceEqual(new[] { greeting }), "Search folds Turkish characters (gunaydin → Günaydın).");
            Assert(Ids("subat dunya").SequenceEqual(new[] { greeting }), "Every query word must match (ŞUBAT/dünya).");
            Assert(Ids("is").Contains(greeting), "Tag names are searchable (is → İş).");
            Assert(Ids("notepad").Count == 3, "Source app is searchable.");
            Assert(Ids("bağlantı").SequenceEqual(new[] { link }), "Type label is searchable.");
            Assert(Ids(null, tag: personal.Id).OrderBy(x => x).SequenceEqual(new[] { greeting, link }.OrderBy(x => x)), "Filter by tag.");
            Assert(Ids(null, ClipFilterKind.Link).SequenceEqual(new[] { link }) && Ids(null, ClipFilterKind.Video).SequenceEqual(new[] { link }), "Filter by type and video.");
            Assert(Ids(null, ClipFilterKind.Email).SequenceEqual(new[] { mail }), "Filter by e-mail type.");
            Assert(Ids("dunya", ClipFilterKind.All, work.Id).SequenceEqual(new[] { greeting }), "Search, type and tag filters combine.");

            history.SetPinned(mail, true);
            Assert(ClipFilter.PageOrder(history.Items).First().Id == mail, "Pinned items are listed first on the page.");
            Assert(Ids(null, ClipFilterKind.Pinned).SequenceEqual(new[] { mail }), "Pinned filter.");

            history.RenameTag(work.Id, "Ofis");
            Assert(history.Tags.Any(t => t.Name == "Ofis") && !history.Tags.Any(t => t.Name == "İş"), "Rename tag.");
            bool clash = false;
            try { history.RenameTag(work.Id, "KİŞİSEL"); } catch (InvalidOperationException) { clash = true; }
            Assert(clash, "Rename to an existing name is rejected.");
            history.SetTag(greeting, personal.Id, false);
            Assert(history.Find(greeting)!.TagIds.SequenceEqual(new[] { work.Id }), "Remove one tag from an item.");
        }
        using (ClipboardHistory reopened = OpenHistory(folder, time))
        {
            ClipTag office = reopened.Tags.Single(t => t.Name == "Ofis");
            Assert(reopened.Find(greeting)!.TagIds.SequenceEqual(new[] { office.Id }), "Tag assignments persist.");
            reopened.DeleteTag(office.Id);
            Assert(reopened.Find(greeting)!.TagIds.Count == 0 && reopened.Tags.Count == 1, "Deleting a tag removes it from items but keeps the items.");
            Assert(reopened.Find(greeting) is not null, "Items survive tag deletion.");
        }
    }

    // ---- Win+V takeover (pure logic; no hook is installed) ----

    private const uint VkV = 0x56, VkA = 0x41, VkLWin = 0x5B;

    private static void WinVFilter()
    {
        var filter = new QuickPanelKeyFilter();
        Assert(filter.Evaluate(VkV, true, false, HookModifiers.Win) == HookAction.SwallowAndOpen, "Win+V key-down opens the panel.");
        Assert(filter.Evaluate(VkV, true, false, HookModifiers.Win) == HookAction.Swallow, "Auto-repeat of V is swallowed without opening again.");
        Assert(filter.Evaluate(VkV, true, false, HookModifiers.Win) == HookAction.Swallow, "Repeat stays swallowed.");
        Assert(filter.Evaluate(VkV, false, false, HookModifiers.Win) == HookAction.Swallow, "The key-up of a swallowed V is swallowed.");
        Assert(filter.Evaluate(VkV, false, false, HookModifiers.None) == HookAction.Pass, "A second key-up is not ours.");
        Assert(filter.Evaluate(VkV, true, false, HookModifiers.Win) == HookAction.SwallowAndOpen, "Next Win+V opens again.");
        filter.Evaluate(VkV, false, false, HookModifiers.None);

        foreach (HookModifiers other in new[] { HookModifiers.None, HookModifiers.Ctrl, HookModifiers.Shift, HookModifiers.Alt,
                     HookModifiers.Win | HookModifiers.Ctrl, HookModifiers.Win | HookModifiers.Shift, HookModifiers.Win | HookModifiers.Alt,
                     HookModifiers.Win | HookModifiers.Ctrl | HookModifiers.Shift | HookModifiers.Alt })
        {
            Assert(filter.Evaluate(VkV, true, false, other) == HookAction.Pass, "V with modifiers " + other + " is left alone.");
            Assert(filter.Evaluate(VkV, false, false, other) == HookAction.Pass, "Its key-up is left alone as well.");
        }
        Assert(filter.Evaluate(VkA, true, false, HookModifiers.Win) == HookAction.Pass && filter.Evaluate(VkA, false, false, HookModifiers.Win) == HookAction.Pass, "Other Win shortcuts (Win+A) stay with Windows.");
        Assert(filter.Evaluate(VkLWin, true, false, HookModifiers.None) == HookAction.Pass && filter.Evaluate(VkLWin, false, false, HookModifiers.None) == HookAction.Pass, "The Win key itself always passes.");

        Assert(filter.Evaluate(VkV, true, true, HookModifiers.Win) == HookAction.Pass && filter.Evaluate(VkV, false, true, HookModifiers.Win) == HookAction.Pass, "Injected events (SendInput) are never swallowed, so injecting cannot loop.");

        // V pressed with Win, then Win released and V pressed again while still down: the second press is a plain V.
        Assert(filter.Evaluate(VkV, true, false, HookModifiers.Win) == HookAction.SwallowAndOpen, "Win+V.");
        Assert(filter.Evaluate(VkV, true, false, HookModifiers.None) == HookAction.Pass, "Repeat after Win was released is a plain V.");
        Assert(filter.Evaluate(VkV, false, false, HookModifiers.None) == HookAction.Pass, "…and its key-up passes.");

        filter.Evaluate(VkV, true, false, HookModifiers.Win);
        filter.Reset();
        Assert(filter.Evaluate(VkV, false, false, HookModifiers.None) == HookAction.Pass, "Reset forgets a swallowed V.");
    }

    private static void WinVTakeoverGlue()
    {
        var calls = new List<string>();
        HookModifiers held = HookModifiers.Win;
        int modifierReads = 0;
        var takeover = new WinVTakeover(
            open: target => calls.Add("open:" + target),
            modifiers: () => { modifierReads++; return held; },
            suppressStartMenu: () => calls.Add("mask"),
            foreground: () => new IntPtr(1234));

        Assert(!takeover.Handle(VkA, true, false) && !takeover.Handle(VkLWin, true, false) && modifierReads == 0, "Other keys pass and do not even read the modifier state (hook stays cheap).");
        Assert(takeover.Handle(VkV, true, false), "Win+V is swallowed.");
        Assert(calls.SequenceEqual(new[] { "mask", "open:1234" }), "The Start menu mask is injected before the panel is requested, with the window that was in front.");
        Assert(takeover.Handle(VkV, true, false) && calls.Count == 2, "Auto-repeat is swallowed without a second panel.");
        Assert(takeover.Handle(VkV, false, false) && calls.Count == 2, "The key-up is swallowed.");
        Assert(!takeover.Handle(VkV, true, true) && calls.Count == 2, "An injected V passes untouched.");
        held = HookModifiers.Ctrl | HookModifiers.Win;
        Assert(!takeover.Handle(VkV, true, false) && calls.Count == 2, "Ctrl+Win+V passes.");
        takeover.Dispose();
        takeover.Dispose();
        Assert(!takeover.IsRunning, "A takeover that was never started is not running and disposes safely.");
    }

    private static async Task WinVSettings()
    {
        Assert(ClipboardSettings.DefaultQuickPanelHotkey == "Win+V" && new ClipboardSettings().UsesWinVTakeover, "Default shortcut is Win+V.");

        // Existing user: the old default Ctrl+Shift+V becomes Win+V; an explicit "off" stays off.
        var host = new TestHost("clipboard-winv");
        host.Settings.Save(new ClipboardSettings { QuickPanelHotkey = "Ctrl+Shift+V", QuickPanelEnabled = true });
        var module = new ClipboardModule(new ClipboardModuleOptions { UseSystemClipboard = false, Time = new FakeTime() });
        module.Initialize(host);
        await module.Ready;
        Assert(module.Settings.QuickPanelHotkey == "Win+V" && module.Settings.UsesWinVTakeover && module.Settings.QuickPanelEnabled, "Legacy Ctrl+Shift+V settings are migrated to Win+V.");

        module.UpdateSettings(new ClipboardSettings { QuickPanelEnabled = false, QuickPanelHotkey = module.Settings.QuickPanelHotkey });
        Assert(!module.Settings.QuickPanelEnabled && !module.WinVTakeoverActive, "Takeover off: nothing is hooked, Windows' own Win+V works.");
        Assert(new ModuleSettings(host.Settings.FilePath).Load<ClipboardSettings>() is { QuickPanelEnabled: false, QuickPanelHotkey: "Win+V" }, "The off switch and shortcut are saved.");
        module.Shutdown();

        File.WriteAllText(host.Settings.FilePath, "{ \"QuickPanelEnabled\": false, \"QuickPanelHotkey\": \"ctrl + shift + v\", \"MaxItems\": 77 }");
        var again = new ClipboardModule(new ClipboardModuleOptions { UseSystemClipboard = false, Time = new FakeTime() });
        again.Initialize(host);
        await again.Ready;
        Assert(!again.Settings.QuickPanelEnabled && again.Settings.UsesWinVTakeover && again.Settings.MaxItems == 77, "A hand-written legacy file: disabled stays disabled, other values survive.");
        again.Shutdown();

        Assert(new ClipboardSettings { QuickPanelHotkey = "  " }.Normalized().QuickPanelHotkey == "Win+V", "A blank shortcut falls back to Win+V.");
        Assert(new ClipboardSettings { QuickPanelHotkey = "win + v" }.Normalized().UsesWinVTakeover, "Win+V is recognized however it is written.");
        ClipboardSettings custom = new ClipboardSettings { QuickPanelHotkey = "Ctrl+Alt+K" }.Normalized();
        Assert(custom.QuickPanelHotkey == "Ctrl+Alt+K" && !custom.UsesWinVTakeover, "A custom shortcut is kept and uses RegisterHotKey, not the hook.");
        Assert(!new ClipboardSettings { QuickPanelHotkey = "Win+Shift+V" }.Normalized().UsesWinVTakeover, "Win+Shift+V is a normal hotkey, not the takeover.");
        Assert(!System.Text.Json.JsonSerializer.Serialize(new ClipboardSettings()).Contains("UsesWinVTakeover"), "The derived flag is not written to settings.json.");
    }

    // ---- module-level ----

    private sealed class TestHost : IToolHost
    {
        public TestHost(string id)
        {
            ModuleId = id;
            Settings = new ModuleSettings(Path.Combine(DataPaths.ModuleFolder(id), "settings.json"));
        }

        public string ModuleId { get; }
        public string DataRoot => DataPaths.Root;
        public string DataFolder { get { string path = Path.Combine(DataPaths.ModuleFolder(ModuleId), "data"); Directory.CreateDirectory(path); return path; } }
        public ModuleSettings Settings { get; }
        public IMessageBus Messages { get; } = new MessageBus(dispatcher: null);
        public IJobTracker Jobs { get; } = new JobTracker { FinishedJobLinger = TimeSpan.Zero };
        public IExternalToolManager ExternalTools { get; } = new ExternalToolManager(Path.Combine(DataPaths.Root, "tools"));
        public Dispatcher Dispatcher => Dispatcher.CurrentDispatcher;
        public List<string> Logs { get; } = new();
        public List<ToastOptions> Toasts { get; } = new();
        public List<string> Navigations { get; } = new();
        public void Navigate(string moduleId) => Navigations.Add(moduleId);
        public void ShowToast(ToastOptions toast) => Toasts.Add(toast);
        public HotkeyRegistration RegisterHotkey(HotkeyGesture gesture, Action callback) => HotkeyRegistration.Failed(gesture, "Testte kısayol kaydedilmez.");
        public void Log(string message, Exception? exception = null) { lock (Logs) Logs.Add(message + " " + exception); }
    }

    private static async Task ModulePipeline()
    {
        var host = new TestHost("clipboard");
        var time = new FakeTime();
        var module = new ClipboardModule(new ClipboardModuleOptions { UseSystemClipboard = false, Time = time });
        Assert(module.Id == "clipboard" && module.Title == "Pano" && module.Category == ToolCategories.Clipboard && DataPaths.IsValidId(module.Id), "Module identity.");
        string[] titles = module.Commands.Select(c => c.Title).ToArray();
        Assert(titles.Contains("Pano geçmişini aç") && titles.Contains("Pano hızlı paneli") && titles.Contains("Pano geçmişini temizle"), "Palette commands.");
        Assert(module.Commands.Single(c => c.Title == "Pano geçmişini aç").NavigateToModuleId == "clipboard", "Open command navigates to the page.");

        module.Initialize(host);
        await module.Ready;
        Assert(module.Settings.MaxItems == 1000 && module.Settings.MaxAgeDays == 30 && module.Settings.QuickPanelHotkey == "Win+V"
            && module.Settings.UsesWinVTakeover && module.Settings.MonitoringEnabled && module.Settings.QuickPanelEnabled, "Default settings (Win+V takeover on).");
        Assert(!module.WinVTakeoverActive && module.WinVTakeoverError is null, "No keyboard hook is installed when the system clipboard/hotkeys are off (tests).");
        Assert(module.History is not null && module.History.Retention == new RetentionPolicy(1000, TimeSpan.FromDays(30)), "History opened with default retention.");
        Assert(File.Exists(Path.Combine(host.DataFolder, "clipboard.db")), "Database lives under host.DataFolder.");

        var urls = new List<UrlCopiedMessage>();
        using IDisposable subscription = host.Messages.Subscribe<UrlCopiedMessage>(urls.Add);

        await module.ProcessCaptureAsync(Text("https://www.youtube.com/watch?v=dQw4w9WgXcQ", source: "chrome"));
        await module.ProcessCaptureAsync(Text("https://example.com/docs", source: "chrome"));
        await module.ProcessCaptureAsync(Text("  www.example.org  "));
        Assert(urls.Count == 3 && urls[0].LooksLikeVideo && urls[0].SourceApplication == "chrome" && !urls[1].LooksLikeVideo, "URLs are published with the video flag.");
        Assert(urls[2].Url == "https://www.example.org", "Published URL is normalized.");
        Assert(host.Toasts.Count == 0, "The clipboard module itself shows no download toast (the media module decides).");

        await module.ProcessCaptureAsync(Text("https://youtu.be/gizli", sensitiveFormat: true));
        await module.ProcessCaptureAsync(Text("https://api.example.com/?api_key=abcd1234efgh"));
        Assert(urls.Count == 3, "Sensitive copies are never published, even when they are URLs.");
        Assert(module.History!.Items.Count(i => i.Sensitive) == 2, "Sensitive URLs are still stored (masked).");

        CaptureResult? blocked = await module.ProcessCaptureAsync(Text("KeePass'tan gelen metin", source: "KeePass"));
        CaptureResult? blockedExe = await module.ProcessCaptureAsync(Text("Bitwarden metni", source: "bitwarden.exe"));
        Assert(blocked is null && blockedExe is null && !module.History.Items.Any(i => i.SourceApp is "KeePass" or "bitwarden.exe"), "Blocklisted apps are not recorded.");

        module.UpdateSettings(new ClipboardSettings { MonitoringEnabled = false, BlockedApps = new List<string> { " Slack.exe ", "slack", "" }, MaxItems = 5, MaxAgeDays = 2 });
        Assert(module.Settings.BlockedApps.SequenceEqual(new[] { "Slack" }) && module.Settings.IsBlocked("SLACK.EXE"), "Blocklist entries are normalized (case, .exe, duplicates).");
        Assert(module.Settings.MaxItems == 10 && module.Settings.MaxAgeDays == 2, "Out-of-range retention values are clamped (min 10 items).");
        Assert(new ModuleSettings(host.Settings.FilePath).Load<ClipboardSettings>() is { MaxItems: 10, MonitoringEnabled: false }, "Settings saved through host.Settings.");
        int before = module.History.Items.Count;
        Assert(await module.ProcessCaptureAsync(Text("izleme kapalıyken")) is null && module.History.Items.Count == before, "Nothing is recorded while monitoring is off.");
        module.UpdateSettings(new ClipboardSettings());

        CaptureResult? secret = await module.ProcessCaptureAsync(Text(SyntheticKey("ghp_", 36)));
        Assert(secret?.Entry?.Sensitive == true, "Token captured as sensitive.");
        Assert(module.RevealText(secret!.Entry!.Id) == SyntheticKey("ghp_", 36), "Explicit reveal returns the content.");
        Assert(await module.ClearHistoryAsync() > 0 && module.History.Items.Count == 0, "Clear history through the module.");

        string allLogs;
        lock (host.Logs) allLogs = string.Join("\n", host.Logs);
        Assert(!allLogs.Contains("ghp_") && !allLogs.Contains("api_key") && !allLogs.Contains("gizli"), "Nothing sensitive reaches the log.");
        module.Shutdown();
        module.Shutdown();
        Assert(true, "Shutdown is idempotent and does not throw.");
    }

    // Runs synchronously so every WPF object is created on the STA main thread.
    private static void ViewSmoke()
    {
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Application.Current!.Resources.MergedDictionaries.Add(ThemeResources.Load());

        var host = new TestHost("clipboard-view");
        var module = new ClipboardModule(new ClipboardModuleOptions { UseSystemClipboard = false, Time = new FakeTime() });
        module.Initialize(host);
        module.Ready.GetAwaiter().GetResult();
        const string secret = "password: Mavi-Balina-77";
        module.ProcessCaptureAsync(Text("Görünür not")).GetAwaiter().GetResult();
        module.ProcessCaptureAsync(Text("#D94046")).GetAwaiter().GetResult();
        CaptureResult? sensitive = module.ProcessCaptureAsync(Text(secret)).GetAwaiter().GetResult();
        module.ProcessCaptureAsync(Pixels(8, 6, 120)).GetAwaiter().GetResult();

        var view = (ClipboardView)module.CreateView();
        view.Refresh();
        var list = (System.Windows.Controls.ListBox)view.FindName("ItemList");
        Assert(list.Items.Count == 4, "Page lists every item.");
        ClipItemViewModel row = list.Items.Cast<ClipItemViewModel>().Single(i => i.Id == sensitive!.Entry!.Id);
        Assert(!row.PreviewText.Contains("Mavi") && row.PreviewText.All(c => c == '•'), "Sensitive row is masked.");
        Assert(row.SensitiveNote.Contains("Parola") && row.SensitiveNote.Contains("dk sonra silinecek"), "Sensitive row explains why and when it is deleted.");
        row.Reveal(true);
        Assert(row.PreviewText == secret, "Reveal shows the content on explicit request.");
        row.Reveal(false);
        Assert(!row.PreviewText.Contains("Mavi"), "Hide masks it again.");
        ClipItemViewModel color = list.Items.Cast<ClipItemViewModel>().Single(i => i.Entry.Kind == ClipKind.Color);
        Assert(color.HasSwatch, "Color codes show a swatch.");
        Assert(list.Items.Cast<ClipItemViewModel>().Single(i => i.Entry.Kind == ClipKind.Image).ShowImage, "Image rows show a thumbnail.");

        var panel = new QuickPanelWindow(module, IntPtr.Zero);
        var panelList = (System.Windows.Controls.ListBox)panel.FindName("List");
        Assert(panelList.Items.Count == 4 && panelList.SelectedIndex == 0, "Quick panel lists recent items with the first selected.");
        Assert(panelList.Items.Cast<ClipItemViewModel>().Single(i => i.IsSensitive).PreviewText.All(c => c == '•'), "Quick panel masks sensitive items.");
        panel.Close();
        module.Shutdown();
    }

    // ---- visual check ----

    private static void RenderPages()
    {
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (Application.Current!.TryFindResource("Card") is null) Application.Current.Resources.MergedDictionaries.Add(ThemeResources.Load());

        var host = new TestHost("clipboard-render");
        var time = new FakeTime();
        var module = new ClipboardModule(new ClipboardModuleOptions { UseSystemClipboard = false, Time = time });
        module.Initialize(host);
        module.Ready.GetAwaiter().GetResult();
        ClipEntry Capture(ClipCapture capture, TimeSpan after)
        {
            time.Advance(after);
            return module.ProcessCaptureAsync(capture).GetAwaiter().GetResult()!.Entry!;
        }

        ClipEntry brand = Capture(Text("#E5484D", source: "Figma"), TimeSpan.Zero);
        ClipEntry release = Capture(Text("https://github.com/broidshot/broidshot/releases/tag/v0.3.0", source: "chrome"), TimeSpan.FromMinutes(40));
        Capture(Text(@"C:\Users\ayse\Belgeler\Raporlar\2026-Q3 Satış Raporu.docx", source: "explorer"), TimeSpan.FromMinutes(20));
        Capture(new ClipCapture { Files = new[] { @"C:\Users\ayse\Resimler\ekran-1.png", @"C:\Users\ayse\Resimler\ekran-2.png", @"C:\Users\ayse\Resimler\logo.svg" }, SourceApp = "explorer" }, TimeSpan.FromMinutes(25));
        ClipEntry code = Capture(Text("public static string Slug(string text) =>\n    Regex.Replace(text.ToLowerInvariant(), \"[^a-z0-9]+\", \"-\").Trim('-');", source: "Code"), TimeSpan.FromMinutes(30));
        Capture(Text("ayse.yilmaz@ornek.com.tr", source: "OUTLOOK"), TimeSpan.FromMinutes(12));
        Capture(new ClipCapture { ImagePixels = PageRenderer.SampleImage(480, 270), ImageWidth = 480, ImageHeight = 270, SourceApp = "SnippingTool" }, TimeSpan.FromMinutes(9));
        Capture(Text("Toplantı notu: Perşembe 14:00'te tasarım incelemesi var. Sunumu Ece hazırlayacak, Kaan bütçe tablosunu güncelleyecek; karar metni cuma gününe kadar paylaşılacak.", source: "Teams"), TimeSpan.FromMinutes(6));
        Capture(Text("https://www.youtube.com/watch?v=dQw4w9WgXcQ", source: "chrome"), TimeSpan.FromMinutes(3));
        Capture(Text("password: Mavi-Balina-77", source: "chrome"), TimeSpan.FromMinutes(2));
        time.Advance(TimeSpan.FromMinutes(8));

        module.CreateTagAsync("iş").GetAwaiter().GetResult();
        module.CreateTagAsync("tasarım").GetAwaiter().GetResult();
        module.CreateTagAsync("kod parçaları").GetAwaiter().GetResult();
        IReadOnlyList<ClipTag> tags = module.History!.Tags;
        long Tag(string name) => tags.Single(t => t.Name == name).Id;
        module.SetTagAsync(brand.Id, Tag("tasarım"), true).GetAwaiter().GetResult();
        module.SetTagAsync(release.Id, Tag("iş"), true).GetAwaiter().GetResult();
        module.SetTagAsync(code.Id, Tag("kod parçaları"), true).GetAwaiter().GetResult();
        module.SetPinnedAsync(brand.Id, true).GetAwaiter().GetResult();
        module.SetPinnedAsync(release.Id, true).GetAwaiter().GetResult();

        // Thumbnails are decoded in the background and handed back through the UI synchronization context
        // (set only now: blocking waits on module tasks above would deadlock on it).
        var previousContext = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var view = (ClipboardView)module.CreateView();
        view.Refresh();
        var list = (System.Windows.Controls.ListBox)view.FindName("ItemList");
        Assert(list.Items.Count == 10, "All rendered items are listed.");
        List<ClipItemViewModel> rows = list.Items.Cast<ClipItemViewModel>().ToList();
        Assert(rows[0].SectionTitle == "Sabitlenenler" && rows[1].SectionTitle is null && rows[2].SectionTitle == "Son kopyalananlar", "Pinned section header, then the recent section.");
        foreach (ClipItemViewModel row in rows) _ = row.Thumbnail; // start the background decode
        PageRenderer.Pump(TimeSpan.FromSeconds(3), () => rows.Where(r => r.ShowImage).All(r => r.Thumbnail is not null));
        Assert(rows.Single(r => r.ShowImage).Thumbnail is not null, "Image thumbnail decoded.");

        // Show one card as hovered so the review image includes the actions.
        PageRenderer.Layout(view, 1036, 708);
        foreach (var presenter in PageRenderer.Descendants<System.Windows.Controls.ContentPresenter>(list))
        {
            if (presenter.Content is not ClipItemViewModel item || item.Entry.Kind != ClipKind.Link || item.IsPinned || item.IsSensitive) continue;
            var template = presenter.ContentTemplate;
            if (template?.FindName("HoverActions", presenter) is UIElement actions) actions.Opacity = 1;
            if (template?.FindName("Card", presenter) is System.Windows.Controls.Border card)
            {
                card.Background = (System.Windows.Media.Brush)Application.Current.FindResource("SurfaceHoverBrush");
                card.BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("StrokeStrongBrush");
            }
            break;
        }
        string page = PageRenderer.RenderPage(view, "clipboard-page.png");
        AssertNone(PageRenderer.MissingIconGlyphs(view), "Page glyphs exist in the icon font.");
        AssertNone(PageRenderer.GlyphsInTextFont(view), "Page draws no icon glyph with the text font.");

        view.ShowSettings(true);
        string settings = PageRenderer.RenderPage(view, "clipboard-settings.png");
        AssertNone(PageRenderer.MissingIconGlyphs(view), "Settings glyphs exist in the icon font.");
        view.ShowSettings(false);

        System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
        var empty = new ClipboardModule(new ClipboardModuleOptions { UseSystemClipboard = false, Time = new FakeTime() });
        empty.Initialize(new TestHost("clipboard-render-empty"));
        empty.Ready.GetAwaiter().GetResult();
        var emptyView = (ClipboardView)empty.CreateView();
        emptyView.Refresh();
        PageRenderer.RenderPage(emptyView, "clipboard-empty.png", height: 560);
        empty.Shutdown();

        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var panel = new QuickPanelWindow(module, IntPtr.Zero);
        var content = (FrameworkElement)panel.Content;
        panel.Content = null; // render the content without showing (and activating) a real window
        var panelList = (System.Windows.Controls.ListBox)panel.FindName("List");
        foreach (ClipItemViewModel row in panelList.Items.Cast<ClipItemViewModel>()) _ = row.Thumbnail;
        PageRenderer.Pump(TimeSpan.FromSeconds(3), () => panelList.Items.Cast<ClipItemViewModel>().Where(r => r.ShowImage).All(r => r.Thumbnail is not null));
        string quick = PageRenderer.RenderPage(content, "clipboard-quickpanel.png", panel.Width, panel.Height, pagePadding: false, background: "RailBrush");
        AssertNone(PageRenderer.MissingIconGlyphs(content), "Quick panel glyphs exist in the icon font.");
        AssertNone(PageRenderer.GlyphsInTextFont(content), "Quick panel draws no icon glyph with the text font.");
        panel.Close();
        System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
        module.Shutdown();
        Assert(File.Exists(page) && File.Exists(settings) && File.Exists(quick), "PNGs written to " + PageRenderer.OutputFolder);
        Console.WriteLine("  render: " + PageRenderer.OutputFolder);
    }

    private static void AssertNone(IReadOnlyList<string> problems, string message) => Assert(problems.Count == 0, message + (problems.Count == 0 ? "" : ": " + string.Join(", ", problems)));

    // ---- opt-in: real keyboard hook ----

    private static void LiveKeyboardHook()
    {
        int seen = 0;
        using (var hook = new LowLevelKeyboardHook((vk, down, injected) => { seen++; return false; })) // never swallows
        {
            Assert(hook.Start(out string? error) && error is null && hook.IsRunning, "Hook installed on its own thread: " + error);
            Pump(TimeSpan.FromMilliseconds(300));
        }
        var takeover = new WinVTakeover(_ => { });
        Assert(takeover.Start(out string? takeoverError) && takeover.IsRunning, "Win+V takeover starts: " + takeoverError);
        takeover.Dispose();
        Assert(!takeover.IsRunning, "Takeover stops on dispose.");
        Assert(true, "Hook removed cleanly (" + seen + " key events observed meanwhile).");
    }

    // ---- opt-in: real clipboard ----

    private static void Pump(TimeSpan duration, Func<bool>? until = null)
    {
        DateTime end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end && (until is null || !until()))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private static void LiveClipboard()
    {
        string? previous = null;
        try { if (System.Windows.Clipboard.ContainsText()) previous = System.Windows.Clipboard.GetText(); } catch (Exception) { }
        var host = new TestHost("clipboard-live");
        var module = new ClipboardModule(new ClipboardModuleOptions { UseSystemClipboard = true });
        module.Initialize(host);
        try
        {
            module.Ready.GetAwaiter().GetResult();
            Assert(module.ListenerRunning, "Listener started on a message-only window.");
            string marker = "BroidShot canlı test " + Guid.NewGuid().ToString("N")[..8];
            System.Windows.Clipboard.SetDataObject(marker, true);
            Pump(TimeSpan.FromSeconds(5), () => module.History!.Items.Any(i => i.Payload?.Text == marker));
            ClipEntry? captured = module.History!.Items.FirstOrDefault(i => i.Payload?.Text == marker);
            Assert(captured is not null, "External clipboard change captured.");

            System.Windows.Clipboard.SetDataObject("ikinci " + marker, true);
            Pump(TimeSpan.FromSeconds(5), () => module.History.Items.Count >= 2);
            int count = module.History.Items.Count;
            Assert(module.CopyToClipboard(captured!.Id), "Copy back to the clipboard.");
            Pump(TimeSpan.FromSeconds(1.5));
            Assert(module.History.Items.Count == count && module.History.Items[0].Id == captured.Id, "Own change is ignored (no duplicate), item moved to top.");
            Assert(System.Windows.Clipboard.GetText() == marker, "Clipboard holds the copied item.");
        }
        finally
        {
            module.Shutdown();
            try { if (previous is not null) System.Windows.Clipboard.SetDataObject(previous, true); } catch (Exception) { }
        }
    }
}
