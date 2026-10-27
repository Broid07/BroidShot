using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using BroidShot.Core;
using BroidShot.Shell;

namespace BroidShot.Startup;

internal partial class LauncherWindow : Window
{
    private readonly StartupPreparation _preparation;
    private readonly bool _animate;
    private readonly TimeSpan _minimumDuration;
    private readonly bool _ocrReady;
    private readonly TimeSpan[] _retryDelays;
    internal string? PendingPackage => _preparation.PendingPackage;
    private CancellationTokenSource? _attempt;
    private TaskCompletionSource<bool>? _retry;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _started;
    private HwndSource? _source;

    internal LauncherWindow(StartupPreparation preparation, bool? animate = null, TimeSpan? minimumDuration = null, TimeSpan[]? retryDelays = null)
    {
        _preparation = preparation;
        _animate = animate ?? SystemParameters.ClientAreaAnimation;
        _minimumDuration = minimumDuration ?? (_animate ? TimeSpan.FromMilliseconds(1100) : TimeSpan.Zero);
        _retryDelays = retryDelays ?? new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
        InitializeComponent();
        AppState.Text = "v" + typeof(App).Assembly.GetName().Version?.ToString(3);
        _ocrReady = StartupPreparation.HasOcrModels(AppContext.BaseDirectory);
        if (!_ocrReady)
        {
            OcrState.Text = "Dil dosyaları eksik";
            OcrGlyph.Text = "\uE7BA";
            OcrGlyph.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        }
        SetToolState("yt-dlp", _preparation.IsInstalled(KnownExternalTools.YtDlp));
        SetToolState("ffmpeg", _preparation.IsInstalled(KnownExternalTools.FFmpeg));
        SourceInitialized += (_, _) =>
        {
            DarkWindowChrome.Apply(this, tintCaption: false);
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WindowMessage);
        };
        Loaded += (_, _) => StartAnimation();
        Closed += (_, _) =>
        {
            StopAnimation();
            if (_source is { IsDisposed: false }) _source.RemoveHook(WindowMessage);
        };
    }

    internal async Task<bool> PrepareAsync()
    {
        if (_started) throw new InvalidOperationException("Başlatıcı zaten çalışıyor.");
        _started = true;
        var watch = Stopwatch.StartNew();
        int transientFailures = 0;
        while (!_closeRequested)
        {
            using var cancellation = new CancellationTokenSource();
            _attempt = cancellation;
            _retry = null;
            RetryButton.Visibility = Visibility.Collapsed;
            Headline.Text = "Birazdan hazırız.";
            Subtitle.Text = "Araçlarını hazırlıyoruz, sen anı yakala.";
            PreparationBar.IsIndeterminate = true;
            StatusText.Text = "Bileşenler kontrol ediliyor…";
            var progress = new Progress<PreparationProgress>(p =>
            {
                if (ReferenceEquals(_attempt, cancellation) && !cancellation.IsCancellationRequested) ApplyProgress(p);
            });
            try
            {
                // The first paint and animation remain responsive while tool manifests/downloads are processed.
                await Task.Run(() => _preparation.PrepareAsync(progress, cancellation.Token), cancellation.Token);
                if (_closeRequested) return false;
                Headline.Text = PendingPackage is not null ? "Yeni sürüm hazır." : _ocrReady ? "Her şey hazır." : "BroidShot hazır.";
                Subtitle.Text = _ocrReady ? "Şimdi güzel bir şey yakala." : "OCR için paketteki tessdata klasörünü kontrol et.";
                StatusText.Text = "BroidShot açılıyor…";
                PreparationBar.IsIndeterminate = false;
                PreparationBar.Value = 100;
                TimeSpan remaining = _minimumDuration - watch.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellation.Token);
                if (_animate) await Task.Delay(250, cancellation.Token);
                return !_closeRequested;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                if (_closeRequested) return false;
                ErrorLog.Write("launcher", "Başlangıç bileşenleri hazırlanamadı.", ex);
                _retry = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Headline.Text = "Küçük bir mola.";
                Subtitle.Text = "Bağlantını kontrol edip yeniden deneyebilirsin.";
                StatusText.Text = ex is ExternalToolException ? ex.Message : "Araçlar hazırlanamadı. Bağlantını kontrol edip yeniden dene.";
                PreparationBar.IsIndeterminate = false;
                RetryButton.Visibility = Visibility.Visible;
                if (IsTransient(ex) && transientFailures < _retryDelays.Length)
                {
                    TimeSpan wait = _retryDelays[transientFailures++];
                    Headline.Text = "Bağlantı bekleniyor.";
                    Subtitle.Text = "İnternet gelince kaldığımız yerden devam edeceğiz.";
                    for (int remaining = (int)Math.Ceiling(wait.TotalSeconds); remaining > 0 && !_retry.Task.IsCompleted; remaining--)
                    {
                        StatusText.Text = $"{remaining} sn sonra yeniden denenecek · {transientFailures}/{_retryDelays.Length}";
                        await Task.WhenAny(Task.Delay(1000), _retry.Task);
                    }
                    if (_closeRequested) return false;
                    continue;
                }
                if (!await _retry.Task) return false;
                transientFailures = 0;
            }
            finally { _attempt = null; }
        }
        return false;
    }

    internal void ApplyProgress(PreparationProgress progress)
    {
        if (_closeRequested) return;
        StatusText.Text = progress.Detail;
        PreparationBar.IsIndeterminate = progress.Fraction is null;
        if (progress.Fraction is { } fraction) PreparationBar.Value = fraction * 100;
        if (progress.ToolId.Length == 0) return;
        if (progress.ToolId == "app")
        {
            AppState.Text = (progress.Version is { } version ? "v" + version + " · " : "") + progress.State;
            AppState.ToolTip = AppState.Text;
            if (progress.Complete) { AppGlyph.Text = "\uE73E"; AppGlyph.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush"); }
            if (!string.IsNullOrWhiteSpace(progress.Notes))
            {
                ReleaseNotesText.Text = progress.Notes.Replace("\r", " ").Replace("\n", " ");
                if (ReleaseNotesText.Text.Length > 160) ReleaseNotesText.Text = ReleaseNotesText.Text[..160] + "…";
                ReleaseNotesText.Visibility = Visibility.Visible;
            }
            return;
        }
        var state = progress.ToolId == "yt-dlp" ? YtState : FfmpegState;
        if (progress.Complete) SetToolState(progress.ToolId, true);
        string? shownVersion = progress.Version;
        if (shownVersion is not null && progress.ToolId == "ffmpeg")
        {
            var match = System.Text.RegularExpressions.Regex.Match(shownVersion, @"ffmpeg-n(\d+(?:\.\d+)+)");
            if (match.Success) shownVersion = match.Groups[1].Value;
        }
        state.Text = (shownVersion is not null ? shownVersion + " · " : "") + progress.State;
        state.ToolTip = progress.Version;
    }

    private void SetToolState(string id, bool ready)
    {
        TextBlock state = id == "yt-dlp" ? YtState : FfmpegState;
        TextBlock glyph = id == "yt-dlp" ? YtGlyph : FfmpegGlyph;
        state.Text = ready ? "Hazır" : "İndirilecek";
        glyph.Text = ready ? "\uE73E" : "\uE896";
        glyph.SetResourceReference(TextBlock.ForegroundProperty, ready ? "SuccessBrush" : "TextTertiaryBrush");
    }

    private void RetryClicked(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        _retry?.TrySetResult(true);
        RetryButton.IsEnabled = true;
    }

    private static bool IsTransient(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is HttpRequestException or TaskCanceledException or IOException && current is not InvalidDataException) return true;
        return false;
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    private void DragHeader(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            _closeRequested = true;
            _attempt?.Cancel();
            _retry?.TrySetResult(false);
            RetryButton.IsEnabled = false;
            StatusText.Text = "Kapatılıyor…";
        }
        base.OnClosing(e);
    }

    internal async Task DismissAsync()
    {
        StopAnimation();
        if (_animate && IsVisible)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)));
            await Task.Delay(160);
        }
        _allowClose = true;
        Close();
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == WindowActivation.RestoreMessage)
        {
            handled = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        return IntPtr.Zero;
    }

    private void StartAnimation()
    {
        if (!_animate) return;
        Entrance.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650)));
        EntranceOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(700))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        OrbitRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(16)) { RepeatBehavior = RepeatBehavior.Forever });
        AccentCorner.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(1100)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopAnimation()
    {
        Entrance.BeginAnimation(OpacityProperty, null);
        EntranceOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        OrbitRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        AccentCorner.BeginAnimation(OpacityProperty, null);
    }
}
