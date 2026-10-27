using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BroidShot.Core;

// Core contract checks. No real network: every HTTP call goes to FakeHandler.
internal static class Program
{
    private static int assertions;
    private static string root = "";

    [STAThread]
    private static int Main()
    {
        root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(DataPaths.DataDirectoryVariable, Path.Combine(root, "profile"));
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Data paths honour BROIDSHOT_DATA_DIR and reject unsafe ids", Sync(DataPathsAndIds)),
            ("Module settings round-trip, tolerate damaged files and write atomically", Sync(SettingsRoundTrip)),
            ("Message bus delivers typed messages, isolates failing handlers, unsubscribes", Sync(Messaging)),
            ("Hotkey gestures parse, format and map to Win32 values", Sync(Hotkeys)),
            ("Turkish-aware palette matching", Sync(Search)),
            ("Job tracker reports, cancels and records failures", Jobs),
            ("Checksum files (GNU and BSD formats)", Sync(Checksums)),
            ("External tool installs only a verified HTTPS download", ToolInstallAndReuse),
            ("External tool rejects a wrong SHA-256 and leaves nothing behind", ToolRejectsBadChecksum),
            ("External tool refuses missing checksum entries, HTTP and HTTPS→HTTP redirects", ToolRejectsUnsafeSources),
            ("External tool extracts zips safely and blocks path traversal", ToolZip),
            ("External tool update check compares SHA-256 and swaps the install", ToolUpdate),
            ("Known tool resolvers read GitHub release assets and fall back", KnownResolvers),
            ("Shared theme dictionary loads from BroidShot.Core", Sync(Theme)),
        };
        int failures = 0;
        foreach (var test in tests)
        {
            try { test.Run().GetAwaiter().GetResult(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
        }
        Console.WriteLine($"Core verification: {tests.Length - failures}/{tests.Length} passed, {assertions} assertions; no real network used.");
        return failures == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException(message);
    }

    private static void DataPathsAndIds()
    {
        Assert(DataPaths.Root == Path.GetFullPath(Path.Combine(root, "profile")), "Root follows BROIDSHOT_DATA_DIR.");
        Assert(DataPaths.ModuleFolder("clipboard") == Path.Combine(DataPaths.Root, "modules", "clipboard"), "Module folder is <root>/modules/<id>.");
        Assert(DataPaths.ToolsRoot == Path.Combine(DataPaths.Root, "tools"), "Tools folder is <root>/tools.");
        foreach (string bad in new[] { "../x", "Upper", "", "a/b", "a\\b", new string('a', 70), ".hidden" })
            Assert(!DataPaths.IsValidId(bad), "Unsafe id rejected: " + bad);
        Assert(DataPaths.IsValidId("yt-dlp") && DataPaths.IsValidId("ffmpeg-lgpl") && DataPaths.IsValidId("media.v2"), "Normal ids accepted.");
    }

    private sealed class SampleSettings
    {
        public string Name { get; set; } = "varsayılan";
        public int Count { get; set; }
        public ToastKind Kind { get; set; } = ToastKind.Info;
        public List<string> Tags { get; set; } = new();
    }

    private static void SettingsRoundTrip()
    {
        string path = Path.Combine(DataPaths.ModuleFolder("sample"), "settings.json");
        var settings = new ModuleSettings(path);
        Assert(!settings.Exists && settings.Load<SampleSettings>().Name == "varsayılan", "Missing file loads defaults.");
        settings.Save(new SampleSettings { Name = "Çığ öşü İı", Count = 3, Kind = ToastKind.Warning, Tags = { "a", "b" } });
        var loaded = new ModuleSettings(path).Load<SampleSettings>();
        Assert(loaded.Name == "Çığ öşü İı" && loaded.Count == 3 && loaded.Kind == ToastKind.Warning && loaded.Tags.SequenceEqual(new[] { "a", "b" }),
            "Settings round-trip with Turkish text, enums and lists.");
        Assert(File.ReadAllText(path).Contains("\"Warning\""), "Enums are stored by name.");
        Assert(!File.Exists(path + ".tmp"), "Atomic write leaves no temp file.");
        File.WriteAllText(path, "{ bozuk json");
        Assert(settings.Load<SampleSettings>().Count == 0, "Damaged settings fall back to defaults instead of throwing.");
    }

    private record BaseMessage(string Text);
    private sealed record DerivedMessage(string Text, int Value) : BaseMessage(Text);
    private sealed record OtherMessage;

    private static void Messaging()
    {
        var errors = new List<Exception>();
        var bus = new MessageBus(dispatcher: null, onHandlerError: errors.Add);
        var received = new List<string>();
        using (bus.Subscribe<UrlCopiedMessage>(_ => throw new InvalidOperationException("handler failure")))
        using (bus.Subscribe<UrlCopiedMessage>(m => received.Add("url:" + m.Url)))
        using (bus.Subscribe<BaseMessage>(m => received.Add("base:" + m.Text)))
        {
            bus.Publish(new UrlCopiedMessage("https://example.com/v", true, "test", DateTimeOffset.Now));
            bus.Publish(new DerivedMessage("türetilmiş", 1));
            bus.Publish(new OtherMessage());
            Assert(received.SequenceEqual(new[] { "url:https://example.com/v", "base:türetilmiş" }), "Typed and base-type subscribers receive their messages only.");
            Assert(errors.Count == 1, "A failing handler is reported without blocking other handlers.");
            Assert(bus.SubscriberCount == 3, "Three active subscriptions.");
        }
        Assert(bus.SubscriberCount == 0, "Disposing unsubscribes.");
        bus.Publish(new UrlCopiedMessage("https://example.com/x", false, null, DateTimeOffset.Now));
        Assert(received.Count == 2, "No delivery after unsubscribe.");
    }

    private static void Hotkeys()
    {
        HotkeyGesture palette = HotkeyGesture.Parse("Ctrl+Alt+Space");
        Assert(palette.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && palette.Key == Key.Space, "Ctrl+Alt+Space parses.");
        Assert(palette.ToString() == "Ctrl+Alt+Space" && palette.DisplayText == "Ctrl + Alt + Space", "Gesture formats for settings and display.");
        Assert(palette.NativeModifiers == 0x3 && palette.VirtualKey == 0x20, "Win32 modifiers and virtual key.");
        Assert(HotkeyGesture.TryParse(" ctrl + shift + v ", out HotkeyGesture paste) && paste.ToString() == "Ctrl+Shift+V", "Lowercase with spaces parses.");
        Assert(HotkeyGesture.TryParse("Win+Shift+1", out HotkeyGesture digit) && digit.Key == Key.D1 && digit.ToString() == "Shift+Win+1", "Digits map to D0-D9.");
        Assert(HotkeyGesture.TryParse("Ctrl+Shift+F9", out HotkeyGesture f9) && f9.VirtualKey == 0x78, "Function keys map.");
        foreach (string bad in new[] { "", "Ctrl+Alt", "Ctrl+A+B", "Ctrl+Bilinmeyen", "Ctrl+5000" })
            Assert(!HotkeyGesture.TryParse(bad, out _), "Invalid gesture rejected: " + bad);
    }

    private static void Search()
    {
        Assert(SearchMatcher.Normalize("PDF'i PNG'ye DÖNÜŞTÜR · Işık") == "pdf i png ye donustur isik", "Turkish casing and diacritics fold.");
        Assert(SearchMatcher.Score("donustur", "PDF'i PNG'ye dönüştür") > 0, "ASCII query matches Turkish title.");
        Assert(SearchMatcher.Score("pdf png", "PDF'i PNG'ye dönüştür") > 0, "Every word must match; multi-word query matches.");
        Assert(SearchMatcher.Score("pdf mp3", "PDF'i PNG'ye dönüştür") == 0, "A word without any match excludes the entry.");
        Assert(SearchMatcher.Score("ekal", "Ekran alıntısı") > 0, "Subsequence fuzzy match.");
        Assert(SearchMatcher.Score("indir", "Video kaydet", new[] { "indir", "download" }) > 0, "Keywords match.");
        Assert(SearchMatcher.Score("ALAN", "Alan seç") > SearchMatcher.Score("alan", "Tüm alanları temizle"), "Title prefix ranks above inner word prefix.");
        Assert(SearchMatcher.Score("pano", "Pano") > SearchMatcher.Score("pano", "Not", new[] { "pano" }), "Title match ranks above keyword match.");
        Assert(SearchMatcher.Score("", "Herhangi") == 1, "Empty query lists everything.");
    }

    private static async Task Jobs()
    {
        var tracker = new JobTracker { FinishedJobLinger = TimeSpan.Zero };
        ToolJob job = tracker.Start("İndirme");
        var changes = new List<string>();
        job.PropertyChanged += (_, e) => changes.Add(e.PropertyName!);
        Assert(tracker.Jobs.Count == 1 && job.IsRunning && job.IsIndeterminate, "Started job is listed and indeterminate.");
        job.Report(1.7, "yarı");
        Assert(job.Progress == 1 && job.Percent == 100 && job.Status == "yarı" && changes.Contains(nameof(ToolJob.Percent)), "Progress is clamped and announced.");
        job.Complete("bitti");
        Assert(job.State == ToolJobState.Completed && tracker.Jobs.Count == 0, "Completed job is removed after the linger time.");
        job.Report(0.1);
        Assert(job.Progress == 1, "Finished jobs ignore later reports.");

        ToolJobState cancelled = await tracker.RunAsync("İptal", async (j, token) =>
        {
            j.Cancel();
            await Task.Delay(Timeout.Infinite, token);
        });
        Assert(cancelled == ToolJobState.Cancelled, "Cancellation ends the job as cancelled.");
        ToolJobState failed = await tracker.RunAsync("Hata", (_, _) => throw new IOException("disk dolu"));
        Assert(failed == ToolJobState.Failed, "Failures are recorded, not thrown.");
        ToolJobState done = await tracker.RunAsync("Tamam", (j, _) => { j.Report(0.5); return Task.CompletedTask; });
        Assert(done == ToolJobState.Completed && tracker.Jobs.Count == 0, "Successful work completes.");
        ToolJob fixedJob = tracker.Start("Sabit", canCancel: false);
        fixedJob.Cancel();
        Assert(!fixedJob.IsCancellationRequested && !fixedJob.CanCancelNow, "Non-cancellable jobs ignore Cancel.");
        fixedJob.Fail("x");
    }

    private static void Checksums()
    {
        string a = new('a', 64), b = new('B', 64), c = new('c', 64);
        string gnu = $"{a}  yt-dlp.exe\n{b} *yt-dlp_linux\n# yorum\n{c}  ./ffmpeg.zip\n";
        Assert(ChecksumFile.FindSha256(gnu, "yt-dlp.exe") == a, "GNU two-space format.");
        Assert(ChecksumFile.FindSha256(gnu, "yt-dlp_linux") == new string('b', 64), "Binary-mode star prefix; result is lowercase.");
        Assert(ChecksumFile.FindSha256(gnu, "ffmpeg.zip") == c, "Leading ./ is ignored.");
        Assert(ChecksumFile.FindSha256($"SHA256 (tool.zip) = {a}", "tool.zip") == a, "BSD format.");
        Assert(ChecksumFile.FindSha256(gnu, "yt-dlp") is null && ChecksumFile.FindSha256(gnu, "YT-DLP.EXE") is null, "Names match exactly.");
        Assert(ChecksumFile.FindSha256("zz  yt-dlp.exe", "yt-dlp.exe") is null, "Malformed hashes are ignored.");
    }

    // ---------- External tools ----------

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static ExternalToolDefinition ExeTool(string name, ExternalToolRelease release, long max = 1024 * 1024) =>
        new(name, name + ".exe", (_, _) => Task.FromResult(release)) { MaxDownloadBytes = max, StripSingleRootFolder = false };

    private static async Task ToolInstallAndReuse()
    {
        byte[] payload = Encoding.UTF8.GetBytes("MZ fake tool v1");
        var http = new FakeHandler();
        http.Add("https://downloads.test/v1/good.exe", payload);
        http.Add("https://downloads.test/v1/SUMS", $"{Sha(payload)}  good.exe\n{new string('0', 64)}  other.exe\n");
        var release = new ExternalToolRelease { Version = "1.0", DownloadUrl = new Uri("https://downloads.test/v1/good.exe"), AssetFileName = "good.exe", ChecksumFileUrl = new Uri("https://downloads.test/v1/SUMS") };
        var tool = ExeTool("good", release);
        using var manager = new ExternalToolManager(Path.Combine(root, "tools-a"), http);
        Assert(manager.TryGetExecutable(tool) is null && !manager.GetStatus(tool).IsInstalled, "Nothing installed initially.");
        var stages = new List<ExternalToolStage>();
        string exe = await manager.EnsureAsync(tool, new SyncProgress(p => stages.Add(p.Stage)));
        Assert(File.ReadAllBytes(exe).SequenceEqual(payload), "Verified executable is installed.");
        Assert(exe == Path.Combine(manager.ToolsRoot, "good", "current", "good.exe"), "Layout is <tools>/<name>/current/<exe>.");
        ExternalToolStatus status = manager.GetStatus(tool);
        Assert(status.IsInstalled && status.Version == "1.0" && status.Sha256 == Sha(payload) && status.LastUpdateCheck is not null, "Manifest records version, hash and check time.");
        Assert(stages.Contains(ExternalToolStage.Downloading) && stages.Contains(ExternalToolStage.Verifying) && stages.Last() == ExternalToolStage.Completed, "Progress stages are reported.");
        int requests = http.Requests.Count;
        string again = await manager.EnsureAsync(tool);
        Assert(again == exe && http.Requests.Count == requests, "Installed tool is reused without network.");
        Assert(!Directory.EnumerateFileSystemEntries(Path.Combine(manager.ToolsRoot, "good")).Any(p => Path.GetFileName(p).StartsWith('.')), "No staging or download leftovers.");
    }

    private static async Task ToolRejectsBadChecksum()
    {
        byte[] payload = Encoding.UTF8.GetBytes("MZ tampered");
        var http = new FakeHandler();
        http.Add("https://downloads.test/bad.exe", payload);
        http.Add("https://downloads.test/SUMS", $"{Sha(Encoding.UTF8.GetBytes("MZ original"))}  bad.exe\n");
        var tool = ExeTool("bad", new ExternalToolRelease { Version = "1", DownloadUrl = new Uri("https://downloads.test/bad.exe"), AssetFileName = "bad.exe", ChecksumFileUrl = new Uri("https://downloads.test/SUMS") });
        using var manager = new ExternalToolManager(Path.Combine(root, "tools-b"), http);
        await AssertThrowsAsync<ExternalToolException>(() => manager.EnsureAsync(tool), "Mismatched SHA-256 must be rejected.");
        string folder = Path.Combine(manager.ToolsRoot, "bad");
        Assert(manager.TryGetExecutable(tool) is null && !Directory.EnumerateFileSystemEntries(folder).Any(), "Rejected download leaves no files.");

        var direct = ExeTool("bad2", new ExternalToolRelease { Version = "1", DownloadUrl = new Uri("https://downloads.test/bad.exe"), AssetFileName = "bad.exe", Sha256 = new string('1', 64) });
        await AssertThrowsAsync<ExternalToolException>(() => manager.EnsureAsync(direct), "Mismatched inline SHA-256 must be rejected.");
        var noHash = ExeTool("bad3", new ExternalToolRelease { Version = "1", DownloadUrl = new Uri("https://downloads.test/bad.exe"), AssetFileName = "bad.exe" });
        int before = http.Requests.Count;
        await AssertThrowsAsync<ExternalToolException>(() => manager.EnsureAsync(noHash), "A release without any checksum is refused.");
        Assert(http.Requests.Count == before, "Nothing is downloaded without a checksum.");
    }

    private static async Task ToolRejectsUnsafeSources()
    {
        byte[] payload = Encoding.UTF8.GetBytes("MZ x");
        var http = new FakeHandler();
        http.Add("https://downloads.test/x.exe", payload);
        http.Add("https://downloads.test/SUMS", $"{Sha(payload)}  another.exe\n");
        using var manager = new ExternalToolManager(Path.Combine(root, "tools-c"), http);

        var missing = ExeTool("missing", new ExternalToolRelease { Version = "1", DownloadUrl = new Uri("https://downloads.test/x.exe"), AssetFileName = "x.exe", ChecksumFileUrl = new Uri("https://downloads.test/SUMS") });
        await AssertThrowsAsync<ExternalToolException>(() => manager.EnsureAsync(missing), "Asset absent from checksum file is refused.");
        Assert(!http.Requests.Contains("https://downloads.test/x.exe"), "Payload is not downloaded when its checksum is unknown.");

        int before = http.Requests.Count;
        var plain = ExeTool("plain", new ExternalToolRelease { Version = "1", DownloadUrl = new Uri("http://downloads.test/x.exe"), AssetFileName = "x.exe", Sha256 = Sha(payload) });
        await AssertThrowsAsync<ExternalToolException>(() => manager.EnsureAsync(plain), "HTTP download URL is refused.");
        var plainSums = ExeTool("plainsums", new ExternalToolRelease { Version = "1", DownloadUrl = new Uri("https://downloads.test/x.exe"), AssetFileName = "x.exe", ChecksumFileUrl = new Uri("http://downloads.test/SUMS") });
        await AssertThrowsAsync<ExternalToolException>(() => manager.EnsureAsync(plainSums), "HTTP checksum URL is refused.");
        Assert(http.Requests.Count == before, "No request is made for HTTP URLs.");

        http.Add("https://downloads.test/redirected.exe", payload, finalUri: "http://mirror.test/x.exe");
        var redirected = ExeTool("redirected", new ExternalToolRelease { Version = "1", DownloadUrl = new Uri("https://downloads.test/redirected.exe"), AssetFileName = "x.exe", Sha256 = Sha(payload) });
        await AssertThrowsAsync<ExternalToolException>(() => manager.EnsureAsync(redirected), "A response that ended on HTTP is refused.");
        Assert(manager.TryGetExecutable(redirected) is null, "Redirected download is not installed.");

        var large = ExeTool("large", new ExternalToolRelease { Version = "1", DownloadUrl = new Uri("https://downloads.test/x.exe"), AssetFileName = "x.exe", Sha256 = Sha(payload) }, max: 2);
        await AssertThrowsAsync<ExternalToolException>(() => manager.EnsureAsync(large), "Oversized download is refused.");
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(content);
        return memory.ToArray();
    }

    private static async Task ToolZip()
    {
        byte[] good = Zip(("ffmpeg-n9.0-latest-win64-gpl-9.0/bin/ffmpeg.exe", "MZ ffmpeg"), ("ffmpeg-n9.0-latest-win64-gpl-9.0/bin/ffprobe.exe", "MZ ffprobe"), ("ffmpeg-n9.0-latest-win64-gpl-9.0/LICENSE.txt", "GPL"));
        byte[] evil = Zip(("bin/ffmpeg.exe", "MZ"), ("../../evil.txt", "kötü"));
        var http = new FakeHandler();
        http.Add("https://downloads.test/good.zip", good);
        http.Add("https://downloads.test/evil.zip", evil);
        using var manager = new ExternalToolManager(Path.Combine(root, "tools-d"), http);
        var tool = new ExternalToolDefinition("zipped", "bin/ffmpeg.exe", (_, _) => Task.FromResult(new ExternalToolRelease
        { Version = "n9.0", DownloadUrl = new Uri("https://downloads.test/good.zip"), AssetFileName = "good.zip", Sha256 = Sha(good), Kind = ExternalToolPackageKind.Zip }));
        string exe = await manager.EnsureAsync(tool);
        Assert(exe == Path.Combine(manager.GetInstallDirectory(tool), "bin", "ffmpeg.exe") && File.ReadAllText(exe) == "MZ ffmpeg", "Single root folder is stripped.");
        Assert(File.Exists(Path.Combine(manager.GetInstallDirectory(tool), "bin", "ffprobe.exe")), "Sibling files are extracted (ffprobe next to ffmpeg).");

        var bad = new ExternalToolDefinition("evil", "bin/ffmpeg.exe", (_, _) => Task.FromResult(new ExternalToolRelease
        { Version = "1", DownloadUrl = new Uri("https://downloads.test/evil.zip"), AssetFileName = "evil.zip", Sha256 = Sha(evil), Kind = ExternalToolPackageKind.Zip }));
        await AssertThrowsAsync<ExternalToolException>(() => manager.EnsureAsync(bad), "Zip path traversal is refused.");
        Assert(!File.Exists(Path.Combine(root, "evil.txt")) && !File.Exists(Path.Combine(manager.ToolsRoot, "evil.txt")) && manager.TryGetExecutable(bad) is null,
            "Nothing is written outside the staging folder and nothing is installed.");
    }

    private static async Task ToolUpdate()
    {
        byte[] v1 = Encoding.UTF8.GetBytes("MZ v1"), v2 = Encoding.UTF8.GetBytes("MZ v2");
        var http = new FakeHandler();
        http.Add("https://downloads.test/v1.exe", v1);
        http.Add("https://downloads.test/v2.exe", v2);
        ExternalToolRelease latest = new() { Version = "1", DownloadUrl = new Uri("https://downloads.test/v1.exe"), AssetFileName = "upd.exe", Sha256 = Sha(v1) };
        var tool = new ExternalToolDefinition("upd", "upd.exe", (_, _) => Task.FromResult(latest)) { UpdateInterval = TimeSpan.Zero };
        using var manager = new ExternalToolManager(Path.Combine(root, "tools-e"), http);
        string exe = await manager.EnsureAsync(tool);
        ExternalToolUpdateInfo same = await manager.CheckForUpdateAsync(tool);
        Assert(!same.UpdateAvailable && same.InstalledVersion == "1", "Same SHA-256 means no update.");
        latest = new ExternalToolRelease { Version = "2", DownloadUrl = new Uri("https://downloads.test/v2.exe"), AssetFileName = "upd.exe", Sha256 = Sha(v2) };
        ExternalToolUpdateInfo newer = await manager.CheckForUpdateAsync(tool);
        Assert(newer.UpdateAvailable && newer.LatestVersion == "2" && newer.LatestSha256 == Sha(v2), "Different SHA-256 means an update is available.");
        string updated = await manager.EnsureUpToDateAsync(tool);
        Assert(updated == exe && File.ReadAllBytes(updated).SequenceEqual(v2) && manager.GetStatus(tool).Version == "2", "EnsureUpToDateAsync swaps in the new build.");
        Assert(!Directory.EnumerateDirectories(Path.Combine(manager.ToolsRoot, "upd")).Any(d => Path.GetFileName(d).StartsWith('.')), "Old install folder is removed.");

        latest = new ExternalToolRelease { Version = "3", DownloadUrl = new Uri("https://downloads.test/v2.exe"), AssetFileName = "upd.exe", Sha256 = new string('9', 64) };
        string kept = await manager.EnsureUpToDateAsync(tool);
        Assert(File.ReadAllBytes(kept).SequenceEqual(v2) && manager.GetStatus(tool).Version == "2", "A failed update keeps the working version.");
    }

    private const string FFmpegApiJson = """
    {"tag_name":"latest","name":"Latest Auto-Build (2026-10-26 14:25)","published_at":"2026-10-26T14:47:22Z","assets":[
      {"name":"checksums.sha256","browser_download_url":"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256","size":2000},
      {"name":"ffmpeg-master-latest-win64-gpl.zip","browser_download_url":"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip","size":1},
      {"name":"ffmpeg-n8.1-latest-win64-gpl-8.1.zip","browser_download_url":"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-gpl-8.1.zip","size":1},
      {"name":"ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip","browser_download_url":"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip","size":1},
      {"name":"ffmpeg-n9.0-latest-win64-gpl-9.0.zip","browser_download_url":"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-9.0.zip","size":1,"updated_at":"2026-10-26T14:40:00Z"},
      {"name":"ffmpeg-n9.0-latest-win64-lgpl-9.0.zip","browser_download_url":"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-9.0.zip","size":1},
      {"name":"ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz","browser_download_url":"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz","size":1}]}
    """;

    private const string YtDlpApiJson = """
    {"tag_name":"2026.10.26","assets":[
      {"name":"SHA2-256SUMS","browser_download_url":"https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/SHA2-256SUMS"},
      {"name":"yt-dlp.exe","browser_download_url":"https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe","digest":"sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789"},
      {"name":"yt-dlp_x86.exe","browser_download_url":"http://insecure.test/yt-dlp_x86.exe"}]}
    """;

    private static async Task KnownResolvers()
    {
        var http = new FakeHandler();
        http.Add("https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest", FFmpegApiJson);
        http.Add("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest", YtDlpApiJson);
        using var client = new HttpClient(http, disposeHandler: false);
        ExternalToolRelease ffmpeg = await KnownExternalTools.FFmpeg.ResolveLatest(client, CancellationToken.None);
        Assert(ffmpeg.AssetFileName == "ffmpeg-n9.0-latest-win64-gpl-9.0.zip" && ffmpeg.Kind == ExternalToolPackageKind.Zip, "Newest win64 GPL release-branch zip is chosen (not shared, not master).");
        Assert(ffmpeg.ChecksumFileUrl?.AbsoluteUri == "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256", "FFmpeg checksum file comes from the same release.");
        Assert(ffmpeg.Version.Contains("2026-10-26"), "FFmpeg version includes the build date.");
        ExternalToolRelease lgpl = await KnownExternalTools.CreateFFmpeg(lgpl: true).ResolveLatest(client, CancellationToken.None);
        Assert(lgpl.AssetFileName == "ffmpeg-n9.0-latest-win64-lgpl-9.0.zip", "LGPL variant selects the LGPL zip.");
        Assert(KnownExternalTools.FFmpeg.ExecutableRelativePath == Path.Combine("bin", "ffmpeg.exe"), "FFmpeg executable lives in bin/.");

        ExternalToolRelease ytdlp = await KnownExternalTools.YtDlp.ResolveLatest(client, CancellationToken.None);
        Assert(ytdlp.Version == "2026.10.26" && ytdlp.DownloadUrl.AbsoluteUri.EndsWith("/2026.10.26/yt-dlp.exe") && ytdlp.ChecksumFileUrl!.AbsoluteUri.EndsWith("/2026.10.26/SHA2-256SUMS"),
            "yt-dlp is pinned to the tag's asset and SHA2-256SUMS.");
        GitHubReleases.Release parsed = GitHubReleases.Parse(YtDlpApiJson);
        Assert(parsed.Assets.Count == 2 && parsed.Assets[1].Sha256Digest == "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "Non-HTTPS assets are dropped; digests are parsed.");

        var offline = new FakeHandler(); // every request → 403 (e.g. API rate limit)
        using var offlineClient = new HttpClient(offline, disposeHandler: false);
        ExternalToolRelease fallback = await KnownExternalTools.YtDlp.ResolveLatest(offlineClient, CancellationToken.None);
        Assert(fallback.DownloadUrl.AbsoluteUri == KnownExternalTools.YtDlpLatestExeUrl && fallback.ChecksumFileUrl!.AbsoluteUri == KnownExternalTools.YtDlpLatestChecksumUrl, "yt-dlp falls back to latest/download URLs.");
        ExternalToolRelease ffFallback = await KnownExternalTools.FFmpeg.ResolveLatest(offlineClient, CancellationToken.None);
        Assert(ffFallback.AssetFileName == "ffmpeg-master-latest-win64-gpl.zip" && ffFallback.ChecksumFileUrl is not null, "FFmpeg falls back to the rolling master build with checksums.");
    }

    private static void Theme()
    {
        var dictionary = (ResourceDictionary)Application.LoadComponent(new Uri("/BroidShot.Core;component/Themes/Theme.xaml", UriKind.Relative));
        Assert(dictionary["AccentBrush"] is SolidColorBrush { Color: { R: 0xE5, G: 0x48, B: 0x4D } }, "Accent brush matches the shared brand red.");
        foreach (string key in new[] { "AppFont", "HeadingFont", "IconFont", "NavButton", "AccentButton", "Card", "SectionHeading", "MutedText", "SmallLabel" })
            Assert(dictionary.Contains(key), "Theme key exists: " + key);
        Assert(dictionary.Contains(typeof(System.Windows.Controls.Button)) && dictionary.Contains(typeof(System.Windows.Controls.ProgressBar)), "Implicit control styles exist.");
        Assert(ThemeResources.PackUri.EndsWith("/BroidShot.Core;component/Themes/Theme.xaml"), "Documented pack URI points at the Core theme.");
    }

    private sealed class SyncProgress : IProgress<ExternalToolProgress>
    {
        private readonly Action<ExternalToolProgress> _report;
        public SyncProgress(Action<ExternalToolProgress> report) => _report = report;
        public void Report(ExternalToolProgress value) => _report(value);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (byte[] Body, string? FinalUri)> _routes = new();
        public List<string> Requests { get; } = new();

        public void Add(string url, string body, string? finalUri = null) => Add(url, Encoding.UTF8.GetBytes(body), finalUri);
        public void Add(string url, byte[] body, string? finalUri = null) => _routes[url] = (body, finalUri);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            if (!_routes.TryGetValue(url, out var route))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request, Content = new StringContent("rate limited") });
            HttpRequestMessage final = route.FinalUri is null ? request : new HttpRequestMessage(request.Method, route.FinalUri);
            var content = new ByteArrayContent(route.Body);
            content.Headers.ContentLength = route.Body.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = final, Content = content });
        }
    }
}
