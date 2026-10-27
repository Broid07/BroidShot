using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BroidShot.Core;
using BroidShot.Modules.Media;

// Media module checks. No real network and no real yt-dlp: processes go to FakeRunner, HTTP to FakeHandler.
internal static partial class Program
{
    private static int assertions;
    private static string root = "";

    private const string VideoUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private const string YtDlpExe = @"C:\tools\yt-dlp\current\yt-dlp.exe";
    private const string FFmpegDir = @"C:\tools\ffmpeg\current\bin";
    private const string DlTemplate = "download:BSPROG|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s";
    private const string PpTemplate = "postprocess:BSPP|%(progress.status)s|%(progress.postprocessor)s";

    [STAThread]
    private static int Main(string[] args)
    {
        // A controllable parent/child pair for cancellation checks, with no network dependency.
        if (args.Contains("--wait-child")) { Thread.Sleep(30000); return 0; }
        if (args.Contains("--spawn-wait-child"))
        {
            var childInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in SelfArguments("--wait-child")) childInfo.ArgumentList.Add(argument);
            using var child = Process.Start(childInfo)!;
            Console.WriteLine(child.Id);
            child.WaitForExit();
            return 0;
        }
        root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(DataPaths.DataDirectoryVariable, Path.Combine(root, "profile"));
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ThemeResources.ApplyDefaults();
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("URL validation accepts only absolute http/https links", Sync(UrlValidation)),
            ("Video site detection (supported sites, look-alikes, home pages)", Sync(SiteDetection)),
            ("yt-dlp info arguments are an exact ArgumentList", Sync(InfoArguments)),
            ("yt-dlp download arguments for every preset (exact ArgumentList)", Sync(DownloadArguments)),
            ("Unsafe download inputs are rejected", Sync(ArgumentValidation)),
            ("Progress, file and error lines are parsed", Sync(ProgressParsing)),
            ("Info JSON: single video fixture", Sync(SingleVideoInfo)),
            ("Info JSON: playlist fixture and request planning", Sync(PlaylistInfo)),
            ("Queue: parallel limit, progress, job cancel kills process, retry, queued cancel", QueueConcurrency),
            ("Toasts: supported links only, once per session, setting, navigation without download", Sync(ClipboardToasts)),
            ("First use: consent before tools, install as a job, then yt-dlp -J with UTF-8 env", ToolConsentFlow),
            ("Daily yt-dlp update check through the manager (skipped while downloading)", DailyUpdateCheck),
            ("Corrupt SHA-256 from the release is rejected and nothing is installed", BadChecksumRejected),
            ("Download history round-trip, clear and damaged file", Sync(History)),
            ("Locked browser cookie store: info and download retry once without cookies", CookieFallback),
            ("Real process runner kills the process tree on cancel", ProcessRunnerCancel),
            ("Page loads with the shared theme and shows the consent prompt", Sync(ViewLoads)),
            ("Page states: navigation, presets, playlist selection, queue actions and themed renders", Sync(PageStates)),
        };
        int failures = 0;
        foreach (var test in tests)
        {
            try { test.Run().GetAwaiter().GetResult(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
        }
        Console.WriteLine($"Media verification: {tests.Length - failures}/{tests.Length} passed, {assertions} assertions; no real network or yt-dlp used.");
        return failures == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException(message);
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException(message);
    }

    private static void AssertSequence(IReadOnlyList<string> actual, IReadOnlyList<string> expected, string message)
    {
        if (actual.SequenceEqual(expected)) { assertions++; return; }
        throw new InvalidOperationException(message + Environment.NewLine + "  expected: " + string.Join(" ¦ ", expected) + Environment.NewLine + "  actual:   " + string.Join(" ¦ ", actual));
    }

    private static async Task WaitUntil(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(message);
            await Task.Delay(10).ConfigureAwait(false);
        }
        assertions++;
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // ---------------------------------------------------------------- URLs

    private static void UrlValidation()
    {
        Assert(UrlRules.TryNormalize("  https://www.youtube.com/watch?v=abc  ", out string url) && url == "https://www.youtube.com/watch?v=abc", "Trimmed https URL accepted.");
        Assert(UrlRules.TryNormalize("\"http://example.com/video\"", out _), "Quoted http URL accepted.");
        foreach (string bad in new[] { "", "   ", "ftp://example.com/a.mp4", "file:///C:/x.mp4", "javascript:alert(1)", "www.youtube.com/watch?v=a",
                     "/watch?v=a", "https://", "https://exa mple.com/", "https://example.com/a\nb", "-https://example.com", "C:\\video.mp4",
                     "https://example.com/" + new string('a', 5000) })
            Assert(!UrlRules.IsAcceptable(bad), "Rejected: " + bad.Replace("\n", "\\n")[..Math.Min(bad.Length, 40)]);
    }

    private static void SiteDetection()
    {
        var positive = new Dictionary<string, string>
        {
            ["https://www.youtube.com/watch?v=dQw4w9WgXcQ"] = "YouTube",
            ["https://m.youtube.com/watch?v=dQw4w9WgXcQ&t=10"] = "YouTube",
            ["https://youtu.be/dQw4w9WgXcQ"] = "YouTube",
            ["https://www.youtube.com/shorts/abc123"] = "YouTube",
            ["https://www.youtube.com/playlist?list=PL123"] = "YouTube",
            ["https://music.youtube.com/watch?v=abc"] = "YouTube Music",
            ["https://www.instagram.com/reel/Cxyz/"] = "Instagram",
            ["https://www.instagram.com/p/Cxyz/"] = "Instagram",
            ["https://www.tiktok.com/@user/video/7234567890"] = "TikTok",
            ["https://vm.tiktok.com/ZMabc/"] = "TikTok",
            ["https://x.com/user/status/1234567890"] = "X",
            ["https://twitter.com/user/status/1234567890"] = "X",
            ["https://vimeo.com/123456789"] = "Vimeo",
            ["https://www.twitch.tv/videos/123456"] = "Twitch",
            ["https://clips.twitch.tv/FunnyClipName"] = "Twitch",
            ["https://www.dailymotion.com/video/x8abc"] = "Dailymotion",
            ["https://dai.ly/x8abc"] = "Dailymotion",
            ["https://www.reddit.com/r/videos/comments/abc123/title/"] = "Reddit",
            ["https://v.redd.it/abc123"] = "Reddit",
        };
        foreach (var pair in positive)
            Assert(UrlRules.DetectVideoSite(pair.Key) == pair.Value, $"Detected {pair.Value}: {pair.Key} (got {UrlRules.DetectVideoSite(pair.Key) ?? "null"})");

        foreach (string negative in new[]
                 {
                     "https://www.youtube.com/", "https://www.youtube.com/feed/subscriptions", "https://www.instagram.com/someuser/",
                     "https://www.tiktok.com/@user", "https://x.com/user", "https://vimeo.com/about", "https://www.twitch.tv/somechannel",
                     "https://www.reddit.com/r/videos/", "https://youtube.com.evil.example/watch?v=abc", "https://notyoutube.com/watch?v=abc",
                     "https://example.com/watch?v=abc", "ftp://youtube.com/watch?v=abc", "https://evilx.com/user/status/1"
                 })
            Assert(UrlRules.DetectVideoSite(negative) is null, "Not detected: " + negative);
    }

    // ---------------------------------------------------------------- arguments

    private static void InfoArguments()
    {
        AssertSequence(YtDlpArguments.BuildInfo(VideoUrl, null),
            new[] { "--ignore-config", "--no-colors", "--encoding", "utf-8", "--no-warnings", "-J", "--flat-playlist", "--", VideoUrl },
            "Info arguments.");
        AssertSequence(YtDlpArguments.BuildInfo(VideoUrl, "firefox"),
            new[] { "--ignore-config", "--no-colors", "--encoding", "utf-8", "--no-warnings", "-J", "--flat-playlist", "--cookies-from-browser", "firefox", "--", VideoUrl },
            "Info arguments with browser cookies.");
    }

    private static string[] DownloadPrefix(string folder, string template) => new[]
    {
        "--ignore-config", "--no-colors", "--encoding", "utf-8",
        "--newline", "--progress", "--no-mtime",
        "--progress-template", DlTemplate,
        "--progress-template", PpTemplate,
        "--print", "after_move:BSFILE|%(filepath)s",
        "--ffmpeg-location", FFmpegDir,
        "-P", folder,
        "-o", template,
    };

    private static void DownloadArguments()
    {
        const string folder = @"C:\Users\Test\Downloads\BroidShot";
        const string template = "%(title)s [%(id)s].%(ext)s";
        var video = new Func<string, string[]>(sort => new[] { "-f", "bv*+ba/b", "-S", sort, "--merge-output-format", "mp4", "--remux-video", "mp4", "--embed-metadata" });
        var expectations = new (DownloadPreset Preset, string? Format, string[] Args)[]
        {
            (DownloadPreset.VideoBest, null, video("res,ext:mp4:m4a")),
            (DownloadPreset.Video1080, null, video("res:1080,ext:mp4:m4a")),
            (DownloadPreset.Video720, null, video("res:720,ext:mp4:m4a")),
            (DownloadPreset.Video480, null, video("res:480,ext:mp4:m4a")),
            (DownloadPreset.AudioMp3_192, null, new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "192K", "--embed-metadata" }),
            (DownloadPreset.AudioMp3_320, null, new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "320K", "--embed-metadata" }),
            (DownloadPreset.AudioM4a, null, new[] { "-f", "ba[ext=m4a]/ba/b", "-x", "--audio-format", "m4a", "--embed-metadata" }),
            (DownloadPreset.Advanced, " 137+140 ", new[] { "-f", "137+140" }),
        };
        Assert(expectations.Length == Enum.GetValues<DownloadPreset>().Length, "Every preset has an expectation.");
        foreach (var e in expectations)
        {
            var request = new DownloadRequest { Url = VideoUrl, Title = "t", Preset = e.Preset, FormatId = e.Format, OutputFolder = folder, FileNameTemplate = template };
            string[] expected = DownloadPrefix(folder, template).Concat(new[] { "--no-playlist" }).Concat(e.Args).Concat(new[] { "--", VideoUrl }).ToArray();
            AssertSequence(YtDlpArguments.BuildDownload(request, FFmpegDir), expected, "Download arguments for " + e.Preset);
        }

        var playlistItem = new DownloadRequest
        {
            Url = "https://www.youtube.com/playlist?list=PL1", Title = "t", Preset = DownloadPreset.AudioM4a, OutputFolder = folder,
            FileNameTemplate = "%(playlist_index)s - %(title)s.%(ext)s", PlaylistItem = 3, CookiesBrowser = "edge"
        };
        AssertSequence(YtDlpArguments.BuildDownload(playlistItem, FFmpegDir),
            DownloadPrefix(folder, "%(playlist_index)s - %(title)s.%(ext)s")
                .Concat(new[] { "--playlist-items", "3", "-f", "ba[ext=m4a]/ba/b", "-x", "--audio-format", "m4a", "--embed-metadata",
                    "--cookies-from-browser", "edge", "--", "https://www.youtube.com/playlist?list=PL1" }).ToArray(),
            "Playlist item + cookies arguments.");

        var badTemplate = new DownloadRequest { Url = VideoUrl, Title = "t", OutputFolder = folder, FileNameTemplate = @"..\..\Windows\%(title)s.%(ext)s" };
        Assert(YtDlpArguments.BuildDownload(badTemplate, FFmpegDir).SkipWhile(a => a != "-o").Skip(1).First() == MediaSettings.DefaultFileNameTemplate,
            "Escaping template falls back to the default.");
        Assert(YtDlpArguments.BuildDownload(badTemplate, FFmpegDir)[^2] == "--", "URL always follows \"--\".");
    }

    private static void ArgumentValidation()
    {
        const string folder = @"C:\Out";
        AssertThrows<ArgumentException>(() => YtDlpArguments.BuildInfo("file:///C:/a.mp4", null), "Info rejects file URLs.");
        AssertThrows<ArgumentException>(() => YtDlpArguments.BuildDownload(new DownloadRequest { Url = "ftp://x/y", Title = "t", OutputFolder = folder }, FFmpegDir), "Download rejects ftp.");
        AssertThrows<ArgumentException>(() => YtDlpArguments.BuildDownload(new DownloadRequest { Url = VideoUrl, Title = "t", OutputFolder = "relative" }, FFmpegDir), "Relative folder rejected.");
        AssertThrows<ArgumentException>(() => YtDlpArguments.BuildDownload(new DownloadRequest { Url = VideoUrl, Title = "t", OutputFolder = folder, CookiesBrowser = "netscape; rm" }, FFmpegDir), "Unknown browser rejected.");
        foreach (string bad in new[] { "", "--exec calc", "-o x", "137;calc", "137\n140", "$(calc)" + new string('a', 300) })
            AssertThrows<ArgumentException>(() => YtDlpArguments.BuildDownload(new DownloadRequest { Url = VideoUrl, Title = "t", OutputFolder = folder, Preset = DownloadPreset.Advanced, FormatId = bad }, FFmpegDir),
                "Bad format selector rejected: " + bad.Replace("\n", "\\n")[..Math.Min(bad.Length, 20)]);
        foreach (string good in new[] { "137+140", "bv*[height<=720]+ba/b", "best", "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best" })
            Assert(YtDlpArguments.IsValidFormatSelector(good), "Format selector accepted: " + good);
        foreach (string bad in new[] { "C:\\x\\%(title)s.%(ext)s", "../%(title)s.%(ext)s", "plain.mp4", "", "-o%(title)s" })
            Assert(!YtDlpArguments.IsValidFileNameTemplate(bad), "Template rejected: " + bad);
        Assert(YtDlpArguments.IsValidFileNameTemplate("%(uploader)s/%(title)s.%(ext)s"), "Subfolder template accepted.");
    }

    // ---------------------------------------------------------------- parsing

    private static async Task CookieFallback()
    {
        const string cookieError = "ERROR: Could not copy Chrome cookie database. See  https://github.com/yt-dlp/yt-dlp/issues/7271  for more info";
        string json = Fixture("single.json");
        var runner = new FakeRunner();
        runner.Behavior = (spec, onOutput, onError, _) =>
        {
            if (spec.Arguments.Contains("--cookies-from-browser")) { onError(cookieError); return Task.FromResult(1); }
            if (spec.Arguments.Contains("-J")) onOutput(json);
            else onOutput("BSFILE|" + Path.Combine(root, "cookie", "a.mp4"));
            return Task.FromResult(0);
        };
        var client = new YtDlpClient(runner, _ => Task.FromResult(new ToolPaths(YtDlpExe, FFmpegDir)));
        MediaInfo info = await client.FetchInfoAsync(VideoUrl, "chrome", CancellationToken.None);
        Assert(info.Title.Length > 0 && runner.Specs.Count == 2, "Info succeeds on the second run.");
        Assert(runner.Specs[0].Arguments.Contains("--cookies-from-browser") && !runner.Specs[1].Arguments.Contains("--cookies-from-browser"),
            "The retry drops only the cookie arguments.");

        var request = new DownloadRequest { Url = VideoUrl, Title = "a", Preset = DownloadPreset.VideoBest, OutputFolder = Path.Combine(root, "cookie"), FileNameTemplate = MediaSettings.DefaultFileNameTemplate, CookiesBrowser = "chrome" };
        string? file = await client.DownloadAsync(request, _ => { }, CancellationToken.None);
        Assert(file == Path.Combine(root, "cookie", "a.mp4") && runner.Specs.Count == 4, "Download succeeds on the second run without cookies.");

        runner.Behavior = (_, _, onError, _) => { onError("ERROR: [youtube] x: Video unavailable"); return Task.FromResult(1); };
        runner.Specs.Clear();
        await AssertThrowsAsync<MediaException>(() => client.FetchInfoAsync(VideoUrl, "chrome", CancellationToken.None), "Other errors still fail.");
        Assert(runner.Specs.Count == 1, "Other errors are not retried.");
        Assert(!YtDlpClient.IsCookieFailure("ERROR: [youtube] x: Video unavailable"), "Video errors are not cookie failures.");
    }

    private static void ProgressParsing()
    {
        Assert(ProgressParser.TryParse("BSPROG|downloading|524288|1048576|NA|262144.5|2", out DownloadProgress p), "Progress line parsed.");
        Assert(p.Status == "downloading" && p.DownloadedBytes == 524288 && p.TotalBytes == 1048576 && p.EtaSeconds == 2, "Fields parsed.");
        Assert(Math.Abs(p.Fraction!.Value - 0.5) < 1e-9, "Fraction is downloaded/total.");
        Assert(p.SpeedText == "256,0 KB/sn" && p.EtaText == "kalan 0:02", "Speed and ETA formatted in Turkish: " + p.SpeedText + " / " + p.EtaText);
        Assert(p.Describe().StartsWith("%50,0") || p.Describe().StartsWith("%50.0"), "Description shows percent: " + p.Describe());

        Assert(ProgressParser.TryParse("BSPROG|downloading|1000|NA|4000.0|NA|NA", out p) && p.TotalBytes == 4000 && Math.Abs(p.Fraction!.Value - 0.25) < 1e-9 && p.Speed is null && p.EtaSeconds is null,
            "Estimate is used when the total is NA; NA speed/ETA are null.");
        Assert(ProgressParser.TryParse("BSPROG|downloading|1000|NA|NA|None|NA", out p) && p.Fraction is null && p.SpeedText == "" && p.EtaText == "", "Unknown size gives indeterminate progress.");
        Assert(ProgressParser.TryParse("BSPROG|finished|4000|4000|NA|NA|NA", out p) && p.Fraction == 1, "Finished is 100%.");
        Assert(ProgressParser.TryParse("BSPP|started|Merger", out p) && p.IsPostProcessing && p.PostProcessor == "Merger" && p.Describe().Contains("birleştiriliyor"), "Post-processing line parsed.");
        Assert(ProgressParser.TryParse("BSPROG|downloading|3600|7200|NA|1.5|3725", out p) && p.EtaText == "kalan 1:02:05", "Long ETA uses h:mm:ss.");
        Assert(!ProgressParser.TryParse("[download]  12.5% of 10.00MiB", out _) && !ProgressParser.TryParse("", out _) && !ProgressParser.TryParse("BSPROG|x", out _),
            "Other lines are not progress.");

        Assert(ProgressParser.TryParseFile(@"BSFILE|C:\Out\Örnek Video — Çığ [abc].mp4", out string file) && file == @"C:\Out\Örnek Video — Çığ [abc].mp4", "Final path (UTF-8 text) parsed.");
        Assert(!ProgressParser.TryParseFile("BSFILE|NA", out _) && !ProgressParser.TryParseFile("[info] x", out _), "Missing path ignored.");
        Assert(ProgressParser.TryParseError("ERROR: [youtube] abc: Video unavailable", out string error) && error == "[youtube] abc: Video unavailable", "Error line parsed.");
        Assert(!ProgressParser.TryParseError("WARNING: something", out _), "Warnings are not errors.");
    }

    private static void SingleVideoInfo()
    {
        MediaInfo info = MediaInfoParser.Parse(Fixture("single.json"), VideoUrl);
        Assert(!info.IsPlaylist && info.Entries.Count == 0, "Single video is not a playlist.");
        Assert(info.Title == "Örnek Video — Çığ Şükür" && info.Uploader == "Örnek Kanal" && info.Duration == 212 && info.DurationText == "3:32", "Title, uploader and duration parsed.");
        Assert(info.Thumbnail == "https://i.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg" && info.WebpageUrl == VideoUrl && info.Extractor == "Youtube", "Thumbnail, page URL and extractor parsed.");
        Assert(info.Formats.Count == 9 && info.Formats.All(f => f.FormatId != "sb0"), "Storyboards are skipped.");
        Assert(info.VideoHeights.SequenceEqual(new[] { 2160, 1080, 720, 480, 360 }), "Distinct heights, highest first.");
        Assert(info.QualitiesText == "2160p, 1080p, 720p, 480p, 360p · ayrı ses", "Qualities text: " + info.QualitiesText);
        MediaFormat f137 = info.Formats.Single(f => f.FormatId == "137");
        Assert(f137.HasVideo && !f137.HasAudio && f137.Label.Contains("1080p") && f137.Label.Contains("sessiz"), "Format label: " + f137.Label);
        MediaFormat f140 = info.Formats.Single(f => f.FormatId == "140");
        Assert(!f140.HasVideo && f140.HasAudio && f140.Label.Contains("yalnız ses 130k"), "Audio label: " + f140.Label);

        AssertThrows<MediaException>(() => MediaInfoParser.Parse("", VideoUrl), "Empty output rejected.");
        AssertThrows<MediaException>(() => MediaInfoParser.Parse("{ bozuk", VideoUrl), "Broken JSON rejected.");
        AssertThrows<MediaException>(() => MediaInfoParser.Parse("[1,2]", VideoUrl), "Non-object JSON rejected.");
        MediaInfo minimal = MediaInfoParser.Parse("{\"id\":\"x\",\"thumbnail\":\"javascript:alert(1)\"}", VideoUrl);
        Assert(minimal.Title == "x" && minimal.Thumbnail is null && minimal.Formats.Count == 0, "Minimal JSON parses; unsafe thumbnail dropped.");

        IReadOnlyList<DownloadRequest> requests = MediaModule.BuildRequests(info, null, DownloadPreset.Video720, null, @"C:\Out", MediaSettings.DefaultFileNameTemplate, null);
        Assert(requests.Count == 1 && requests[0].Url == VideoUrl && requests[0].PlaylistItem is null && requests[0].Title == info.Title && requests[0].Preset == DownloadPreset.Video720,
            "A single video becomes one request.");
    }

    private static void PlaylistInfo()
    {
        const string source = "https://www.youtube.com/playlist?list=PLabc123";
        MediaInfo info = MediaInfoParser.Parse(Fixture("playlist.json"), source);
        Assert(info.IsPlaylist && info.Title == "Örnek Oynatma Listesi" && info.Thumbnail == "https://i.ytimg.com/pl/large.jpg", "Playlist header parsed (largest thumbnail).");
        Assert(info.Entries.Count == 3, "Null entries skipped: " + info.Entries.Count);
        Assert(info.Entries.Select(e => e.Index).SequenceEqual(new[] { 1, 2, 3 }), "Entry indexes are 1-based positions.");
        Assert(info.Entries[0].Title == "Birinci şarkı" && info.Entries[0].Url == "https://www.youtube.com/watch?v=aaa111" && info.Entries[0].DurationText == "3:05" && info.Entries[0].Uploader == "Kanal A",
            "First entry parsed.");
        Assert(info.Entries[1].DurationText == "1:02:05" && info.Entries[1].Uploader == "Kanal B", "Second entry parsed.");
        Assert(info.Entries[2].Url is null, "Relative entry URL is dropped (downloaded by index instead).");
        Assert(info.Entries.All(e => e.IsSelected), "All entries are selected by default.");

        info.Entries[1].IsSelected = false;
        IReadOnlyList<DownloadRequest> requests = MediaModule.BuildRequests(info, info.Entries.Where(e => e.IsSelected), DownloadPreset.AudioMp3_320, null,
            @"C:\Out", MediaSettings.DefaultFileNameTemplate, "firefox");
        Assert(requests.Count == 2, "Only selected entries are queued.");
        Assert(requests[0].Url == "https://www.youtube.com/watch?v=aaa111" && requests[0].PlaylistItem is null && requests[0].CookiesBrowser == "firefox", "Entry with URL downloads directly.");
        Assert(requests[1].Url == source && requests[1].PlaylistItem == 3 && requests[1].Title == "Göreli bağlantılı öğe", "Entry without URL uses the playlist URL + --playlist-items.");

        AssertThrows<MediaException>(() => MediaModule.BuildRequests(info, Array.Empty<MediaEntry>(), DownloadPreset.VideoBest, null, @"C:\Out", MediaSettings.DefaultFileNameTemplate, null),
            "Empty selection rejected.");
        AssertThrows<MediaException>(() => MediaModule.BuildRequests(info, info.Entries, DownloadPreset.Advanced, "--exec x", @"C:\Out", MediaSettings.DefaultFileNameTemplate, null),
            "Bad advanced format rejected.");
        AssertThrows<MediaException>(() => MediaModule.BuildRequests(info, info.Entries, DownloadPreset.VideoBest, null, "Downloads", MediaSettings.DefaultFileNameTemplate, null),
            "Relative output folder rejected.");
    }

    // ---------------------------------------------------------------- queue

    private static async Task QueueConcurrency()
    {
        string output = Path.Combine(root, "downloads");
        var runner = new FakeRunner();
        var gates = new Dictionary<string, TaskCompletionSource<int>>();
        runner.Behavior = async (spec, onOutput, onError, token) =>
        {
            string url = spec.Arguments[^1];
            TaskCompletionSource<int> gate;
            lock (gates)
            {
                gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                gates[url] = gate;
            }
            onOutput("[youtube] Extracting URL: " + url);
            onOutput("BSPROG|downloading|500|1000|NA|2048|5");
            int exit = await gate.Task.WaitAsync(token);
            if (exit == 0)
            {
                onOutput("BSPP|started|Merger");
                onOutput("BSFILE|" + Path.Combine(output, url[^1] + ".mp4"));
            }
            else onError("ERROR: [youtube] " + url[^1] + ": Video unavailable");
            return exit;
        };
        var client = new YtDlpClient(runner, _ => Task.FromResult(new ToolPaths(YtDlpExe, FFmpegDir)));
        var jobs = new JobTracker(null) { FinishedJobLinger = TimeSpan.Zero };
        var queue = new DownloadQueue(client.DownloadAsync, jobs, a => a(), maxParallel: 2);
        var completed = new List<DownloadItem>();
        queue.ItemCompleted += completed.Add;

        DownloadRequest Req(string id) => new() { Url = "https://example.com/v/" + id, Title = "Video " + id, OutputFolder = output };
        void Release(string id, int exit) { lock (gates) gates["https://example.com/v/" + id].SetResult(exit); }
        bool Started(string id) { lock (gates) return gates.ContainsKey("https://example.com/v/" + id); }

        DownloadItem a = queue.Enqueue(Req("a")), b = queue.Enqueue(Req("b")), c = queue.Enqueue(Req("c"));
        await WaitUntil(() => runner.Active == 2 && Started("a") && Started("b"), "Two downloads start.");
        await WaitUntil(() => a.Fraction == 0.5, "Progress reaches the item.");
        Assert(c.State == DownloadState.Queued && !Started("c") && queue.QueuedCount == 1, "Third download waits in the queue.");
        Assert(a.State == DownloadState.Running && a.SpeedText == "2,0 KB/sn" && a.EtaText == "kalan 0:05", "Speed/ETA shown: " + a.DetailText);
        Assert(queue.Items.Count == 3 && queue.Items[0] == c, "Newest item is listed first.");
        await WaitUntil(() => jobs.Jobs.Count == 2 && jobs.Jobs.All(j => j.Progress == 0.5), "Each running download is a job with progress.");

        ProcessSpec spec = runner.Specs.First(s => s.Arguments[^1].EndsWith("/a"));
        Assert(spec.FileName == YtDlpExe && spec.Arguments.Contains("--ffmpeg-location") && spec.Arguments.Contains(FFmpegDir), "yt-dlp gets --ffmpeg-location.");
        Assert(spec.Environment["PYTHONIOENCODING"] == "utf-8" && spec.Environment["PYTHONUTF8"] == "1", "UTF-8 environment set.");

        Release("a", 0);
        await WaitUntil(() => a.State == DownloadState.Completed, "First download completes.");
        Assert(a.FilePath == Path.Combine(output, "a.mp4") && a.CanOpen && !a.CanCancel && a.Percent == 100, "Completed item has its file.");
        Assert(completed.SequenceEqual(new[] { a }), "Completion event raised once.");
        await WaitUntil(() => Started("c") && c.State == DownloadState.Running, "Queued download starts when a slot frees.");
        Assert(runner.MaxActive == 2, "Never more than 2 parallel processes.");

        // Cancel from the shell's job list → item cancelled → process (tree) killed.
        ToolJob jobB = jobs.Jobs.Single(j => j.Title.EndsWith("Video b"));
        jobB.Cancel();
        await WaitUntil(() => b.State == DownloadState.Cancelled, "Cancelling the job cancels the download.");
        Assert(runner.Killed == 1 && b.CanRetry && b.StatusText == "İptal edildi", "Cancelled process was killed.");

        // Queued cancel never starts a process.
        DownloadItem d = queue.Enqueue(Req("d"));
        await WaitUntil(() => Started("d"), "d starts (slot of b).");
        DownloadItem e = queue.Enqueue(Req("e"));
        Assert(e.State == DownloadState.Queued, "e waits.");
        queue.Cancel(e);
        Assert(e.State == DownloadState.Cancelled && queue.QueuedCount == 0, "Queued item cancelled immediately.");

        // Failure, then retry.
        Release("d", 1);
        await WaitUntil(() => d.State == DownloadState.Failed, "Failed exit code marks the item failed.");
        Assert(d.StatusText == "[youtube] d: Video unavailable" && d.CanRetry, "yt-dlp ERROR line is the message: " + d.StatusText);
        int specsBefore = runner.Specs.Count;
        queue.Retry(d);
        await WaitUntil(() => runner.Specs.Count == specsBefore + 1 && d.State == DownloadState.Running && d.Fraction == 0.5, "Retry starts the download again.");
        Release("d", 0);
        await WaitUntil(() => d.State == DownloadState.Completed, "Retried download completes.");

        // Retry the cancelled one, then cancel it from the page.
        queue.Retry(b);
        await WaitUntil(() => b.State == DownloadState.Running && runner.Active == 2, "Retried cancelled item runs.");
        queue.Cancel(b);
        await WaitUntil(() => b.State == DownloadState.Cancelled, "Page cancel stops a running download.");
        Assert(runner.Killed == 2, "Second kill recorded.");

        // Raising the limit pumps the queue.
        queue.MaxParallel = 1;
        DownloadItem f = queue.Enqueue(Req("f")), g = queue.Enqueue(Req("g"));
        await Task.Delay(50);
        Assert(!Started("f") && f.State == DownloadState.Queued, "Limit 1 with c running keeps f queued.");
        queue.MaxParallel = 3;
        await WaitUntil(() => Started("f") && Started("g"), "Raising the limit starts waiting items.");

        queue.CancelAll();
        await WaitUntil(() => c.State == DownloadState.Cancelled && f.State == DownloadState.Cancelled && g.State == DownloadState.Cancelled && runner.Active == 0,
            "CancelAll stops everything.");
        await WaitUntil(() => jobs.Jobs.Count == 0, "Finished jobs leave the job list.");
        AssertThrows<InvalidOperationException>(() => queue.Enqueue(Req("h")), "No new downloads after shutdown.");
        queue.ClearFinished();
        Assert(queue.Items.Count == 0, "Clear finished empties the list.");
    }

    // ---------------------------------------------------------------- module + toasts

    private static (MediaModule Module, FakeHost Host, FakeRunner Runner, FakeToolManager Tools) CreateModule(string name, bool toolsInstalled = true)
    {
        var tools = new FakeToolManager(Path.Combine(root, name, "tools"));
        if (toolsInstalled) { tools.Installed.Add("yt-dlp"); tools.Installed.Add("ffmpeg"); }
        var host = new FakeHost(Path.Combine(root, name), tools);
        var runner = new FakeRunner();
        var module = new MediaModule(() => runner);
        module.Initialize(host);
        return (module, host, runner, tools);
    }

    private static void ClipboardToasts()
    {
        var (module, host, runner, _) = CreateModule("toasts");
        Assert(module.Id == "media" && module.Title == "Medya indir" && module.Category == ToolCategories.Media, "Module identity.");
        Assert(module.Commands.Select(c => c.Title).SequenceEqual(new[] { "Video indir", "Panodaki linki indir", "İndirme klasörünü aç" }), "Palette commands.");

        void Copy(string url) => host.Messages.Publish(new UrlCopiedMessage(url, true, "test", DateTimeOffset.Now));
        Copy("https://www.youtube.com/watch?v=abc123");
        Assert(host.Toasts.Count == 1 && host.Toasts[0].Title == "Video linki algılandı" && host.Toasts[0].ActionLabel == "İndir" && host.Toasts[0].Message.StartsWith("YouTube"),
            "Supported link shows an İndir toast.");
        Copy("https://www.youtube.com/watch?v=abc123");
        Copy("https://youtube.com/watch?v=abc123#t=5");
        Copy("https://WWW.YouTube.com/watch?v=abc123");
        Assert(host.Toasts.Count == 1, "Same link does not toast again in this session.");
        Copy("https://example.com/page");
        Copy("https://www.youtube.com/");
        Copy("ftp://www.youtube.com/watch?v=zzz");
        Assert(host.Toasts.Count == 1, "Unsupported or non-video links do not toast.");
        Copy("https://www.tiktok.com/@u/video/123");
        Assert(host.Toasts.Count == 2 && host.Toasts[1].Message.StartsWith("TikTok"), "Another supported link toasts.");

        Assert(host.Navigations.Count == 0, "Toasts alone do not navigate.");
        host.Toasts[0].Action!();
        Assert(host.Navigations.SequenceEqual(new[] { "media" }) && module.PendingUrl == "https://www.youtube.com/watch?v=abc123", "İndir opens the media page with the URL prefilled.");
        Assert(runner.Specs.Count == 0 && module.Queue.Items.Count == 0, "Nothing is downloaded automatically.");
        Assert(module.TakePendingUrl() == "https://www.youtube.com/watch?v=abc123" && module.PendingUrl is null, "Page takes the pending URL once.");

        module.Settings.ClipboardToasts = false;
        Copy("https://vimeo.com/123456");
        Assert(host.Toasts.Count == 2, "Setting off: no toast.");
        module.Settings.ClipboardToasts = true;
        Copy("https://vimeo.com/123456");
        Assert(host.Toasts.Count == 3, "Setting on again: toast.");

        host.Messages.Publish(new MediaDownloadRequestedMessage("https://x.com/u/status/1"));
        Assert(host.Navigations.Count == 2 && module.PendingUrl == "https://x.com/u/status/1", "MediaDownloadRequestedMessage opens the page with the URL.");
        host.Messages.Publish(new MediaDownloadRequestedMessage("javascript:alert(1)"));
        Assert(host.Navigations.Count == 2, "Non-http request ignored.");

        module.Shutdown();
        Copy("https://www.dailymotion.com/video/x1");
        Assert(host.Toasts.Count == 3 && runner.KillAllCalls == 1, "Shutdown unsubscribes and kills running processes.");
    }

    private static async Task ToolConsentFlow()
    {
        var (module, host, runner, tools) = CreateModule("consent", toolsInstalled: false);
        Assert(module.Provisioner.NeedsInstall && module.Provisioner.ConsentText == "yt-dlp ve ffmpeg indirilecek (~208 MB), devam?", "Consent text: " + module.Provisioner.ConsentText);
        await AssertThrowsAsync<ToolConsentRequiredException>(() => module.FetchInfoAsync(VideoUrl, CancellationToken.None), "Fetching without consent asks first.");
        Assert(tools.Calls.Count == 0 && runner.Specs.Count == 0, "Nothing downloaded or started before consent.");

        var jobTitles = new List<string>();
        ((INotifyCollectionChanged)host.Jobs.Jobs).CollectionChanged += (_, e) => { if (e.NewItems is not null) foreach (ToolJob j in e.NewItems) jobTitles.Add(j.Title); };
        string json = Fixture("single.json");
        runner.Behavior = (spec, onOutput, onError, token) =>
        {
            foreach (string line in json.Split('\n')) onOutput(line.TrimEnd('\r'));
            return Task.FromResult(0);
        };
        module.GrantToolConsent();
        MediaInfo info = await module.FetchInfoAsync(VideoUrl, CancellationToken.None);
        Assert(info.Title == "Örnek Video — Çığ Şükür", "Info fetched after install.");
        Assert(tools.Calls.SequenceEqual(new[] { "ensure:yt-dlp", "ensure:ffmpeg" }), "Both tools installed through the manager: " + string.Join(",", tools.Calls));
        Assert(jobTitles.Contains("yt-dlp ve ffmpeg hazırlanıyor"), "Installation runs as a job.");
        Assert(tools.ProgressReports > 0, "Install progress is reported.");
        ProcessSpec spec = runner.Specs.Single();
        Assert(spec.FileName == tools.ExecutablePath(KnownExternalTools.YtDlp), "Installed yt-dlp is executed.");
        AssertSequence(spec.Arguments, YtDlpArguments.BuildInfo(VideoUrl, null), "Info call uses the info ArgumentList.");
        Assert(spec.Environment["PYTHONIOENCODING"] == "utf-8", "UTF-8 environment.");
        Assert(module.Settings.LastYtDlpUpdateCheck is not null, "Fresh install counts as today's update check.");

        await module.FetchInfoAsync(VideoUrl, CancellationToken.None);
        Assert(tools.Calls.Count == 2, "Second use: no install, no update check on the same day.");

        module.Settings.UseBrowserCookies = true;
        module.Settings.CookieBrowser = "firefox";
        await module.FetchInfoAsync(VideoUrl, CancellationToken.None);
        Assert(runner.Specs[^1].Arguments.Contains("--cookies-from-browser") && runner.Specs[^1].Arguments.Contains("firefox"), "Cookies setting reaches yt-dlp.");

        runner.Behavior = (spec2, onOutput, onError, token) => { onError("ERROR: [generic] Unsupported URL: https://example.com/"); return Task.FromResult(1); };
        try { await module.FetchInfoAsync("https://example.com/", CancellationToken.None); throw new InvalidOperationException("Expected failure."); }
        catch (MediaException ex) { Assert(ex.Message == "[generic] Unsupported URL: https://example.com/", "yt-dlp error is shown: " + ex.Message); }
        await AssertThrowsAsync<MediaException>(() => module.FetchInfoAsync("file:///C:/x", CancellationToken.None), "Non-http URL rejected before running anything.");
        module.Shutdown();
    }

    private static async Task DailyUpdateCheck()
    {
        var tools = new FakeToolManager(Path.Combine(root, "daily", "tools"));
        tools.Installed.Add("yt-dlp");
        tools.Installed.Add("ffmpeg");
        var jobs = new JobTracker(null) { FinishedJobLinger = TimeSpan.Zero };
        DateTimeOffset now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? stored = now.AddHours(-3);
        var logs = new List<string>();
        var provisioner = new ToolProvisioner(tools, jobs, () => stored, t => stored = t, (m, _) => logs.Add(m), () => now);

        await provisioner.EnsureAsync(CancellationToken.None);
        Assert(tools.Calls.Count == 0, "Checked 3 hours ago: no network.");

        now = now.AddHours(22);
        tools.UpdateAvailable = true;
        int running = 1;
        provisioner.ActiveProcessCount = () => running;
        await provisioner.EnsureAsync(CancellationToken.None);
        Assert(tools.Calls.Count == 0, "No update check while yt-dlp is running.");

        running = 0;
        await provisioner.EnsureAsync(CancellationToken.None);
        Assert(tools.Calls.SequenceEqual(new[] { "check:yt-dlp", "install:yt-dlp" }) && stored == now, "After a day: check + verified install via the manager.");

        await provisioner.EnsureAsync(CancellationToken.None);
        Assert(tools.Calls.Count == 2, "Only once per day.");

        now = now.AddDays(2);
        tools.CheckFailure = new ExternalToolException("GitHub erişilemedi.");
        ToolPaths paths = await provisioner.EnsureAsync(CancellationToken.None);
        Assert(paths.YtDlp.EndsWith("yt-dlp.exe") && logs.Count == 1, "Update failure is logged and the installed tool is still used.");

        tools.CheckFailure = null;
        tools.UpdateAvailable = false;
        Assert(!await provisioner.CheckNowAsync(CancellationToken.None) && tools.Calls[^1] == "check:yt-dlp", "Manual check ignores the daily limit.");
        running = 1;
        await AssertThrowsAsync<MediaException>(() => provisioner.CheckNowAsync(CancellationToken.None), "Manual check refuses while downloading.");
    }

    private static async Task BadChecksumRejected()
    {
        var http = new FakeHandler();
        byte[] exe = Encoding.UTF8.GetBytes("MZ fake yt-dlp");
        http.Add(KnownExternalTools.YtDlpLatestExeUrl, exe);
        http.Add(KnownExternalTools.YtDlpLatestChecksumUrl, Encoding.UTF8.GetBytes(new string('0', 64) + "  yt-dlp.exe\n"));
        var manager = new ExternalToolManager(Path.Combine(root, "badsum", "tools"), http);
        var jobs = new JobTracker(null) { FinishedJobLinger = TimeSpan.Zero };
        var states = new List<ToolJobState>();
        ((INotifyCollectionChanged)jobs.Jobs).CollectionChanged += (_, e) => { if (e.OldItems is not null) foreach (ToolJob j in e.OldItems) states.Add(j.State); };
        DateTimeOffset? stored = null;
        var provisioner = new ToolProvisioner(manager, jobs, () => stored, t => stored = t, (_, _) => { });
        Assert(provisioner.NeedsInstall && provisioner.TryGetInstalled() is null, "Tools are missing at first.");
        await AssertThrowsAsync<ExternalToolException>(() => provisioner.EnsureAsync(CancellationToken.None), "Wrong SHA-256 fails the install.");
        Assert(manager.TryGetExecutable(KnownExternalTools.YtDlp) is null && provisioner.NeedsInstall, "Nothing installed.");
        Assert(states.SequenceEqual(new[] { ToolJobState.Failed }), "The install job is marked failed.");
        Assert(!http.Requests.Any(r => r.Contains("FFmpeg")), "ffmpeg is not attempted after yt-dlp failed.");
        Assert(http.Requests.All(r => r.StartsWith("https://")), "Only HTTPS requested.");
        string toolFolder = Path.Combine(root, "badsum", "tools", "yt-dlp");
        Assert(!Directory.Exists(Path.Combine(toolFolder, "current")), "No current folder left behind.");
        Assert(stored is null, "Failed install is not recorded as an update check.");
    }

    // ---------------------------------------------------------------- history, runner, view

    private static void History()
    {
        string path = Path.Combine(root, "history", "history.json");
        var history = new DownloadHistory(path);
        history.Load();
        Assert(history.Entries.Count == 0, "Missing file → empty history.");
        int changes = 0;
        history.Changed += () => changes++;
        history.Add(new HistoryEntry { Title = "Bir", Url = VideoUrl, FilePath = @"C:\Out\bir.mp4", Preset = DownloadPreset.Video720, CompletedAt = DateTimeOffset.Now.AddMinutes(-1) });
        history.Add(new HistoryEntry { Title = "İki — ğüşıöç", Url = VideoUrl, FilePath = @"C:\Out\iki.mp3", Preset = DownloadPreset.AudioMp3_320, CompletedAt = DateTimeOffset.Now });
        var reloaded = new DownloadHistory(path);
        reloaded.Load();
        Assert(reloaded.Entries.Count == 2 && reloaded.Entries[0].Title == "İki — ğüşıöç" && reloaded.Entries[1].Preset == DownloadPreset.Video720, "History persisted newest first.");
        Assert(File.ReadAllText(path).Contains("\"AudioMp3_320\"") && !File.Exists(path + ".tmp"), "Stored as JSON with enum names, atomically.");
        Assert(reloaded.Entries[0].FileName == "iki.mp3", "File name shown.");
        history.Clear();
        Assert(changes == 3 && history.Entries.Count == 0, "Clear empties and notifies.");
        var cleared = new DownloadHistory(path);
        cleared.Load();
        Assert(cleared.Entries.Count == 0, "Clear persisted.");
        File.WriteAllText(path, "[ bozuk");
        cleared.Load();
        Assert(cleared.Entries.Count == 0, "Damaged file → empty history (no throw).");
        for (int i = 0; i < DownloadHistory.MaxEntries + 5; i++) cleared.Add(new HistoryEntry { Title = "x" + i, CompletedAt = DateTimeOffset.Now });
        Assert(cleared.Entries.Count == DownloadHistory.MaxEntries && cleared.Entries[0].Title == "x" + (DownloadHistory.MaxEntries + 4), "History is capped.");
    }

    private static async Task ProcessRunnerCancel()
    {
        // The helper launches another helper and waits, exercising a real process tree without ping.
        var runner = new SystemProcessRunner();
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var lines = new List<string>();
        int exit = await runner.RunAsync(new ProcessSpec(cmd, new[] { "/d", "/c", "echo", "merhaba" }, YtDlpClient.ProcessEnvironment), l => { lock (lines) lines.Add(l); }, _ => { }, CancellationToken.None);
        Assert(exit == 0 && lines.Any(l => l.Trim() == "merhaba"), "Output lines are delivered.");

        using var cancellation = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var childStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> run = runner.RunAsync(new ProcessSpec(Environment.ProcessPath!, SelfArguments("--spawn-wait-child"), YtDlpClient.ProcessEnvironment),
            line => { if (int.TryParse(line, out int pid)) childStarted.TrySetResult(pid); }, _ => { }, cancellation.Token);
        int childId = await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var child = Process.GetProcessById(childId);
        cancellation.CancelAfter(0);
        await AssertThrowsAsync<OperationCanceledException>(() => run, "Cancel throws OperationCanceledException.");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(10), "Cancelled long before the helper would finish.");
        Assert(child.WaitForExit(5000), "The exact child process was killed with the tree.");

        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> second = runner.RunAsync(new ProcessSpec(Environment.ProcessPath!, SelfArguments("--spawn-wait-child"), YtDlpClient.ProcessEnvironment),
            _ => secondStarted.TrySetResult(), _ => { }, CancellationToken.None);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        runner.KillAll();
        Task finished = await Task.WhenAny(second, Task.Delay(10000));
        Assert(finished == second, "KillAll ends running processes (shutdown).");
    }

    private static string[] SelfArguments(string mode) =>
        string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { typeof(Program).Assembly.Location, mode }
            : new[] { mode };

    private static void ViewLoads()
    {
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Application.Current!.Resources.MergedDictionaries.Add(ThemeResources.Load());
        var (module, host, runner, tools) = CreateModule("view", toolsInstalled: false);
        FrameworkElement view = module.CreateView();
        Assert(view is MediaView, "CreateView returns the page.");
        module.OpenWithUrl("https://youtu.be/abc");
        var consentCard = (FrameworkElement)view.FindName("ConsentCard");
        var consentText = (System.Windows.Controls.TextBlock)view.FindName("ConsentText");
        var urlBox = (System.Windows.Controls.TextBox)view.FindName("UrlBox");
        Assert(urlBox.Text == "https://youtu.be/abc" && module.PendingUrl is null, "Prefilled URL taken by the page.");
        Assert(consentCard.Visibility == Visibility.Visible && consentText.Text == "yt-dlp ve ffmpeg indirilecek (~208 MB), devam?", "Consent prompt shown on first use.");
        Assert(runner.Specs.Count == 0 && tools.Calls.Count == 0, "Nothing started before consent.");
        module.Shutdown();
    }

    // ---------------------------------------------------------------- fakes

    private sealed class FakeRunner : IProcessRunner
    {
        private int _active;
        public List<ProcessSpec> Specs { get; } = new();
        public Func<ProcessSpec, Action<string>, Action<string>, CancellationToken, Task<int>> Behavior { get; set; } = (_, _, _, _) => Task.FromResult(0);
        public int Active => Volatile.Read(ref _active);
        public int MaxActive;
        public int Killed;
        public int KillAllCalls;

        public async Task<int> RunAsync(ProcessSpec spec, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
        {
            lock (Specs) Specs.Add(spec);
            int now = Interlocked.Increment(ref _active);
            lock (Specs) MaxActive = Math.Max(MaxActive, now);
            try { return await Behavior(spec, onOutput, onError, cancellationToken); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Killed); throw; }
            finally { Interlocked.Decrement(ref _active); }
        }

        public void KillAll() => KillAllCalls++;
    }

    private sealed class FakeToolManager : IExternalToolManager
    {
        public FakeToolManager(string toolsRoot) => ToolsRoot = toolsRoot;
        public string ToolsRoot { get; }
        public HashSet<string> Installed { get; } = new();
        public List<string> Calls { get; } = new();
        public bool UpdateAvailable { get; set; }
        public Exception? CheckFailure { get; set; }
        public int ProgressReports;

        public string ExecutablePath(ExternalToolDefinition tool) => Path.Combine(GetInstallDirectory(tool), tool.ExecutableRelativePath);
        public string GetInstallDirectory(ExternalToolDefinition tool) => Path.Combine(ToolsRoot, tool.Name, "current");
        public ExternalToolStatus GetStatus(ExternalToolDefinition tool) =>
            new(tool.Name, Installed.Contains(tool.Name), Installed.Contains(tool.Name) ? "2026.10.26" : null, null, TryGetExecutable(tool), null, null);
        public string? TryGetExecutable(ExternalToolDefinition tool) => Installed.Contains(tool.Name) ? ExecutablePath(tool) : null;

        public Task<string> EnsureAsync(ExternalToolDefinition tool, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Installed.Contains(tool.Name)) return Task.FromResult(ExecutablePath(tool));
            Calls.Add("ensure:" + tool.Name);
            progress?.Report(new ExternalToolProgress(tool.Name, ExternalToolStage.Downloading, 50, 100));
            progress?.Report(new ExternalToolProgress(tool.Name, ExternalToolStage.Completed, 100, 100));
            Interlocked.Add(ref ProgressReports, 2);
            Installed.Add(tool.Name);
            return Task.FromResult(ExecutablePath(tool));
        }

        public Task<string> EnsureUpToDateAsync(ExternalToolDefinition tool, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default) =>
            EnsureAsync(tool, progress, cancellationToken);

        public Task<ExternalToolUpdateInfo> CheckForUpdateAsync(ExternalToolDefinition tool, CancellationToken cancellationToken = default)
        {
            Calls.Add("check:" + tool.Name);
            if (CheckFailure is not null) return Task.FromException<ExternalToolUpdateInfo>(CheckFailure);
            var release = new ExternalToolRelease { Version = "2026.10.27", DownloadUrl = new Uri("https://example.invalid/yt-dlp.exe"), AssetFileName = "yt-dlp.exe", Sha256 = new string('a', 64) };
            return Task.FromResult(new ExternalToolUpdateInfo(tool.Name, "2026.10.26", "2026.10.27", UpdateAvailable, release, new string('a', 64)));
        }

        public Task<string> InstallAsync(ExternalToolDefinition tool, ExternalToolRelease release, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls.Add("install:" + tool.Name);
            Installed.Add(tool.Name);
            return Task.FromResult(ExecutablePath(tool));
        }
    }

    private sealed class FakeHost : IToolHost
    {
        private readonly string _folder;

        public FakeHost(string folder, IExternalToolManager tools)
        {
            _folder = folder;
            Directory.CreateDirectory(folder);
            Settings = new ModuleSettings(Path.Combine(folder, "settings.json"));
            ExternalTools = tools;
        }

        public string ModuleId => "media";
        public string DataRoot => _folder;
        public string DataFolder { get { string path = Path.Combine(_folder, "data"); Directory.CreateDirectory(path); return path; } }
        public ModuleSettings Settings { get; }
        public IMessageBus Messages { get; } = new MessageBus(dispatcher: null);
        public IJobTracker Jobs { get; } = new JobTracker(null) { FinishedJobLinger = TimeSpan.Zero };
        public IExternalToolManager ExternalTools { get; }
        public Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;
        public List<string> Navigations { get; } = new();
        public List<ToastOptions> Toasts { get; } = new();
        public List<string> Logs { get; } = new();

        public void Navigate(string moduleId) => Navigations.Add(moduleId);
        public void ShowToast(ToastOptions toast) => Toasts.Add(toast);
        public HotkeyRegistration RegisterHotkey(HotkeyGesture gesture, Action callback) => throw new NotSupportedException();
        public void Log(string message, Exception? exception = null) { lock (Logs) Logs.Add(message); }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _routes = new();
        public List<string> Requests { get; } = new();

        public void Add(string url, byte[] body) => _routes[url] = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            if (!_routes.TryGetValue(url, out byte[]? body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request, Content = new StringContent("rate limited") });
            var content = new ByteArrayContent(body);
            content.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }
    }
}
