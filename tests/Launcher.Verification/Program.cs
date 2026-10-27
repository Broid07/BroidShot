using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BroidShot.Core;
using BroidShot.Startup;

internal static class Program
{
    private static int _assertions;
    private static int _exitCode = 1;
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable(DataPaths.DataDirectoryVariable, Path.Combine(Root, "profile"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ThemeResources.ApplyDefaults();
        app.Resources = (ResourceDictionary)Application.LoadComponent(new Uri("/BroidShot;component/Theme.xaml", UriKind.Relative));
        app.Startup += async (_, _) =>
        {
            try
            {
                await Services();
                await Windows();
                _exitCode = 0;
                Console.WriteLine($"PASS: {_assertions} launcher assertions; no real network, installations or user profile used.");
                Console.WriteLine("Rendered: " + Root);
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); }
            finally { app.Shutdown(_exitCode); }
        };
        app.Run();
        return _exitCode;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _assertions++;
    }

    private static async Task Services()
    {
        var fake = new FakeTools();
        var preparation = new StartupPreparation(fake);
        var reports = new List<PreparationProgress>();
        await preparation.PrepareAsync(new InlineProgress<PreparationProgress>(reports.Add), CancellationToken.None);
        Assert(fake.Calls.SequenceEqual(new[] { "yt-dlp", "ffmpeg" }), "A new profile installs both official tool definitions in order.");
        Assert(reports.Count(r => r.Complete) == 2 && reports[^1].Fraction == 1, "Both installed components report ready and 100% overall.");
        Assert(reports.Where(r => r.Fraction is not null).All(r => r.Fraction is >= 0 and <= 1), "Overall progress stays between zero and one.");
        Assert(reports.Any(r => r.Detail.Contains("MB", StringComparison.Ordinal)), "Download progress includes transferred bytes.");
        fake.Calls.Clear();
        await preparation.PrepareAsync(new InlineProgress<PreparationProgress>(_ => { }), CancellationToken.None);
        Assert(fake.Calls.Count == 0 && fake.UpdateCalls.SequenceEqual(new[] { "yt-dlp", "ffmpeg" }), "Every subsequent startup checks both installed tools without downloading current versions again.");

        fake.Updates.Add("ffmpeg");
        reports.Clear();
        await preparation.PrepareAsync(new InlineProgress<PreparationProgress>(reports.Add), CancellationToken.None);
        Assert(fake.InstallCalls.SequenceEqual(new[] { "ffmpeg" }) && reports.Any(r => r.State == "Güncellendi"), "A newer build is installed and reported visibly.");
        var idleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleTools = new FakeTools();
        Task idlePreparation = new StartupPreparation(idleTools, token => idleGate.Task.WaitAsync(token))
            .PrepareAsync(new InlineProgress<PreparationProgress>(_ => { }), CancellationToken.None);
        Assert(!idlePreparation.IsCompleted && idleTools.Calls.Count == 0, "Running application jobs finish before any tool files are changed.");
        idleGate.SetResult();
        await idlePreparation;

        var partial = new FakeTools();
        partial.Installed.Add("yt-dlp");
        await new StartupPreparation(partial).PrepareAsync(new InlineProgress<PreparationProgress>(_ => { }), CancellationToken.None);
        Assert(partial.Calls.SequenceEqual(new[] { "ffmpeg" }), "Only the missing tool is downloaded.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var untouched = new FakeTools();
        try
        {
            await new StartupPreparation(untouched).PrepareAsync(new InlineProgress<PreparationProgress>(_ => { }), cancelled.Token);
            throw new InvalidOperationException("Cancellation should throw.");
        }
        catch (OperationCanceledException) { Assert(untouched.Calls.Count == 0, "Cancellation before startup performs no install."); }

        string bundled = Path.Combine(Root, "bundled");
        Directory.CreateDirectory(Path.Combine(bundled, "tessdata"));
        Assert(!StartupPreparation.HasOcrModels(bundled), "Missing OCR models are detected.");
        File.WriteAllText(Path.Combine(bundled, "tessdata", "tur.traineddata"), "fixture");
        Assert(!StartupPreparation.HasOcrModels(bundled), "Both declared languages are checked.");
        File.WriteAllText(Path.Combine(bundled, "tessdata", "eng.traineddata"), "fixture");
        Assert(StartupPreparation.HasOcrModels(bundled), "Bundled OCR needs no download.");
    }

    private static LauncherWindow Host(FakeTools tools, bool animate = false)
    {
        var window = new LauncherWindow(new StartupPreparation(tools), animate, TimeSpan.Zero)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false
        };
        window.Show();
        return window;
    }

    private static T Named<T>(LauncherWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Click(LauncherWindow window, string name) => Named<ButtonBase>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static async Task Windows()
    {
        var readyTools = new FakeTools();
        readyTools.Installed.UnionWith(new[] { "yt-dlp", "ffmpeg" });
        var ready = Host(readyTools);
        Assert(ready.FontFamily.Source.Contains("Inter", StringComparison.Ordinal), "Launcher uses the shared embedded Inter font.");
        Assert(await ready.PrepareAsync(), "A prepared startup proceeds to the application.");
        Assert(Named<TextBlock>(ready, "Headline").Text == "Her şey hazır." && Named<ProgressBar>(ready, "PreparationBar").Value == 100, "Ready state is shown before the main window transition.");
        Assert(readyTools.Calls.Count == 0 && readyTools.UpdateCalls.Count == 2, "The ready window checks updates without downloading current tools.");
        Assert(ready.FindName("SkipButton") is null, "The launcher has no skip action.");
        Save(ready, "launcher-ready.png");
        await ready.DismissAsync();
        Assert(!ready.IsVisible, "The launcher closes after handing over to the main window.");

        var waiting = new FakeTools();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting.Work = async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        var skip = Host(waiting);
        Task<bool> skipped = skip.PrepareAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        skip.ApplyProgress(new("yt-dlp", "yt-dlp", "yt-dlp: indiriliyor %48 · 8.6 / 18.0 MB", 0.24));
        Save(skip, "launcher-downloading.png");
        skip.Close();
        Assert(!await skipped.WaitAsync(TimeSpan.FromSeconds(5)), "Closing cancels an active download without bypassing preparation.");
        Assert(waiting.Installed.Count == 0, "An interrupted install is not marked as ready.");
        await skip.DismissAsync();

        var closeTools = new FakeTools();
        closeTools.Installed.UnionWith(new[] { "yt-dlp", "ffmpeg" });
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closeTools.CheckWork = async (_, token) => { closeStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        var close = Host(closeTools);
        Task<bool> closing = close.PrepareAsync();
        await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        close.Close();
        Assert(!await closing.WaitAsync(TimeSpan.FromSeconds(5)), "Closing also cancels an active update check without launching the main application.");
        await close.DismissAsync();

        var retryTools = new FakeTools();
        int ffmpegAttempts = 0;
        retryTools.Work = (tool, _) => tool.Name == "ffmpeg" && ffmpegAttempts++ == 0
            ? Task.FromException(new ExternalToolException("FFmpeg indirilemedi. İnternet bağlantını kontrol et."))
            : Task.CompletedTask;
        var retry = Host(retryTools);
        Task<bool> retryTask = retry.PrepareAsync();
        await Until(() => Named<Button>(retry, "RetryButton").Visibility == Visibility.Visible);
        Assert(!retryTask.IsCompleted && Named<TextBlock>(retry, "StatusText").Text.Contains("FFmpeg", StringComparison.Ordinal), "A failed download waits for retry and names the failed tool.");
        Save(retry, "launcher-offline.png");
        Click(retry, "RetryButton");
        Assert(await retryTask.WaitAsync(TimeSpan.FromSeconds(5)), "Retry can finish preparation.");
        Assert(retryTools.Calls.SequenceEqual(new[] { "yt-dlp", "ffmpeg", "ffmpeg" }), "Retry preserves the already installed first tool.");
        await retry.DismissAsync();

        var offlineTools = new FakeTools { Work = (_, _) => Task.FromException(new ExternalToolException("İnternet bağlantısı yok.")) };
        var offline = Host(offlineTools);
        Task<bool> offlineTask = offline.PrepareAsync();
        await Until(() => Named<Button>(offline, "RetryButton").Visibility == Visibility.Visible);
        Assert(offline.FindName("SkipButton") is null && !offlineTask.IsCompleted, "An error does not offer an offline bypass.");
        offline.Close();
        Assert(!await offlineTask.WaitAsync(TimeSpan.FromSeconds(5)), "Closing the error screen does not open the application.");
        await offline.DismissAsync();

        var reconnectTools = new FakeTools();
        int failures = 0;
        reconnectTools.Work = (_, _) => failures++ == 0
            ? Task.FromException(new ExternalToolException("Bağlantı kesildi", new System.Net.Http.HttpRequestException("offline")))
            : Task.CompletedTask;
        var reconnect = new LauncherWindow(new StartupPreparation(reconnectTools), animate: false, minimumDuration: TimeSpan.Zero, retryDelays: new[] { TimeSpan.Zero })
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false };
        reconnect.Show();
        Assert(await reconnect.PrepareAsync().WaitAsync(TimeSpan.FromSeconds(5)), "A transient connection error retries automatically without a skip action.");
        Assert(reconnectTools.Calls.SequenceEqual(new[] { "yt-dlp", "yt-dlp", "ffmpeg" }), "Automatic retry continues the missing component.");
        reconnect.ApplyProgress(new("app", "BroidShot", "Yeni sürüm", 1, true, "Hazır", "0.4.1", "İndirmeye devam etme ve yeni başlatıcı."));
        Assert(Named<TextBlock>(reconnect, "AppState").Text.Contains("0.4.1") && Named<TextBlock>(reconnect, "ReleaseNotesText").Visibility == Visibility.Visible, "The launcher displays the application version and release notes.");
        Save(reconnect, "launcher-new-version.png");
        await reconnect.DismissAsync();

        var animated = Host(readyTools, animate: true);
        Task<bool> animateTask = animated.PrepareAsync();
        await Task.Delay(120);
        Assert(animated.IsVisible && Named<FrameworkElement>(animated, "Entrance").Opacity > 0, "Animated startup paints while preparation is asynchronous.");
        Assert(await animateTask, "Animation completes without preventing startup.");
        await animated.DismissAsync();
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private static void Save(LauncherWindow window, string name)
    {
        window.UpdateLayout();
        Assert(PageRenderer.MissingIconGlyphs(window).Count == 0 && PageRenderer.GlyphsInTextFont(window).Count == 0, name + ": every icon has a valid glyph and uses the icon font.");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Root, name));
        encoder.Save(stream);
    }

    private sealed class FakeTools : IExternalToolManager
    {
        public HashSet<string> Installed { get; } = new();
        public List<string> Calls { get; } = new();
        public List<string> UpdateCalls { get; } = new();
        public List<string> InstallCalls { get; } = new();
        public HashSet<string> Updates { get; } = new();
        public Func<ExternalToolDefinition, CancellationToken, Task> Work { get; set; } = (_, _) => Task.CompletedTask;
        public Func<ExternalToolDefinition, CancellationToken, Task> CheckWork { get; set; } = (_, _) => Task.CompletedTask;
        public string ToolsRoot => Path.Combine(Root, "tools");
        public string GetInstallDirectory(ExternalToolDefinition tool) => Path.Combine(ToolsRoot, tool.Name, "current");
        public string? TryGetExecutable(ExternalToolDefinition tool) => Installed.Contains(tool.Name) ? Path.Combine(GetInstallDirectory(tool), tool.ExecutableRelativePath) : null;
        public ExternalToolStatus GetStatus(ExternalToolDefinition tool) => new(tool.Name, Installed.Contains(tool.Name), "test", null, TryGetExecutable(tool), null, null);
        public async Task<string> EnsureAsync(ExternalToolDefinition tool, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(tool.Name);
            progress?.Report(new(tool.DisplayName, ExternalToolStage.Downloading, 50, 100));
            await Work(tool, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Installed.Add(tool.Name);
            return TryGetExecutable(tool)!;
        }
        public Task<string> EnsureUpToDateAsync(ExternalToolDefinition tool, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Startup must not check updates.");
        public async Task<ExternalToolUpdateInfo> CheckForUpdateAsync(ExternalToolDefinition tool, CancellationToken cancellationToken = default)
        {
            UpdateCalls.Add(tool.Name);
            await CheckWork(tool, cancellationToken);
            return new(tool.Name, "test", "new", Updates.Contains(tool.Name), new ExternalToolRelease
            {
                Version = "new", DownloadUrl = new Uri("https://example.invalid/fixture"), AssetFileName = "fixture.exe", Sha256 = new string('a', 64)
            }, new string('a', 64));
        }
        public async Task<string> InstallAsync(ExternalToolDefinition tool, ExternalToolRelease release, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            InstallCalls.Add(tool.Name);
            progress?.Report(new(tool.DisplayName, ExternalToolStage.Downloading, 50, 100));
            await Work(tool, cancellationToken);
            Installed.Add(tool.Name);
            Updates.Remove(tool.Name);
            return TryGetExecutable(tool)!;
        }
    }
}
