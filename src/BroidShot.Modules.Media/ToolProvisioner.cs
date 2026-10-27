using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BroidShot.Core;

namespace BroidShot.Modules.Media;

internal sealed record ToolPaths(string YtDlp, string FFmpegDirectory);

/// <summary>
/// Installs yt-dlp and ffmpeg through <see cref="IExternalToolManager"/> (HTTPS + SHA-256 verified) after the
/// user agreed in the page, and checks yt-dlp for updates at most once a day while the module is used.
/// </summary>
internal sealed class ToolProvisioner
{
    // Rough download sizes for the consent prompt (yt-dlp.exe ≈ 18 MB, BtbN win64 GPL zip ≈ 190 MB).
    public const int YtDlpApproxMegabytes = 18;
    public const int FFmpegApproxMegabytes = 190;
    public static readonly TimeSpan YtDlpUpdateInterval = TimeSpan.FromDays(1);

    private readonly IExternalToolManager _tools;
    private readonly IJobTracker _jobs;
    private readonly Func<DateTimeOffset?> _getLastCheck;
    private readonly Action<DateTimeOffset> _setLastCheck;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string, Exception?> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _sessionCheck;

    public ToolProvisioner(IExternalToolManager tools, IJobTracker jobs, Func<DateTimeOffset?> getLastCheck, Action<DateTimeOffset> setLastCheck,
        Action<string, Exception?> log, Func<DateTimeOffset>? clock = null, ExternalToolDefinition? ytDlp = null, ExternalToolDefinition? ffmpeg = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _getLastCheck = getLastCheck;
        _setLastCheck = setLastCheck;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
        YtDlp = ytDlp ?? KnownExternalTools.YtDlp;
        FFmpeg = ffmpeg ?? KnownExternalTools.FFmpeg;
    }

    public ExternalToolDefinition YtDlp { get; }
    public ExternalToolDefinition FFmpeg { get; }

    /// <summary>Number of yt-dlp/ffmpeg processes running; updates are skipped while non-zero.</summary>
    public Func<int> ActiveProcessCount { get; set; } = () => 0;

    public bool YtDlpInstalled => _tools.TryGetExecutable(YtDlp) is not null;
    public bool FFmpegInstalled => _tools.TryGetExecutable(FFmpeg) is not null;
    public bool NeedsInstall => !YtDlpInstalled || !FFmpegInstalled;

    public int MissingMegabytes => (YtDlpInstalled ? 0 : YtDlpApproxMegabytes) + (FFmpegInstalled ? 0 : FFmpegApproxMegabytes);

    /// <summary>The consent text shown in the page before anything is downloaded.</summary>
    public string ConsentText
    {
        get
        {
            string what = (YtDlpInstalled, FFmpegInstalled) switch
            {
                (false, false) => "yt-dlp ve ffmpeg",
                (false, true) => "yt-dlp",
                (true, false) => "ffmpeg",
                _ => ""
            };
            return what.Length == 0 ? "" : $"{what} indirilecek (~{MissingMegabytes} MB), devam?";
        }
    }

    /// <summary>Installed paths without network, or null when something is missing.</summary>
    public ToolPaths? TryGetInstalled()
    {
        string? ytDlp = _tools.TryGetExecutable(YtDlp);
        string? ffmpeg = _tools.TryGetExecutable(FFmpeg);
        return ytDlp is null || ffmpeg is null ? null : new ToolPaths(ytDlp, Path.GetDirectoryName(ffmpeg)!);
    }

    public string StatusText
    {
        get
        {
            ExternalToolStatus y = _tools.GetStatus(YtDlp), f = _tools.GetStatus(FFmpeg);
            static string Line(string name, ExternalToolStatus s) => s.IsInstalled ? $"{name} {s.Version}" : $"{name} kurulu değil";
            return Line("yt-dlp", y) + " · " + Line("ffmpeg", f);
        }
    }

    /// <summary>
    /// Installs missing tools (as a visible job) and runs the daily yt-dlp update check. Call only after consent
    /// when <see cref="NeedsInstall"/> was true. Throws <see cref="ExternalToolException"/>/<see cref="MediaException"/> on failure.
    /// </summary>
    public async Task<ToolPaths> EnsureAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetInstalled() is { } ready)
            {
                await CheckYtDlpUpdateAsync(cancellationToken).ConfigureAwait(false);
                return TryGetInstalled() ?? ready;
            }

            Exception? failure = null;
            ToolJobState state = await _jobs.RunAsync("yt-dlp ve ffmpeg hazırlanıyor", async (job, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                var progress = new SyncProgress(p => job.Report(p.Fraction, p.Describe()));
                try
                {
                    if (!YtDlpInstalled)
                    {
                        await _tools.EnsureAsync(YtDlp, progress, linked.Token).ConfigureAwait(false);
                        _setLastCheck(_clock()); // a fresh download is the latest version
                    }
                    if (!FFmpegInstalled) await _tools.EnsureAsync(FFmpeg, progress, linked.Token).ConfigureAwait(false);
                    job.Complete("Araçlar hazır");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = ex;
                    throw;
                }
            }).ConfigureAwait(false);

            if (state == ToolJobState.Cancelled || cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Araç kurulumu iptal edildi.", cancellationToken);
            if (state == ToolJobState.Failed)
                throw failure as ExternalToolException ?? (Exception)new MediaException("Araçlar kurulamadı: " + (failure?.Message ?? "bilinmeyen hata"), failure);
            return TryGetInstalled() ?? throw new MediaException("Araçlar kuruldu ancak bulunamadı.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>At most once per day (persisted) and never while a yt-dlp process is running.</summary>
    internal Task<bool> CheckYtDlpUpdateAsync(CancellationToken cancellationToken) => CheckYtDlpUpdateAsync(force: false, cancellationToken);

    /// <summary>"Güncellemeyi denetle" button: ignores the daily limit and reports failures to the caller.</summary>
    public async Task<bool> CheckNowAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CheckYtDlpUpdateAsync(force: true, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<bool> CheckYtDlpUpdateAsync(bool force, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock();
        DateTimeOffset? last = _sessionCheck ?? _getLastCheck();
        if (!force && last is { } l && now - l < YtDlpUpdateInterval && now >= l) return false;
        if (ActiveProcessCount() > 0)
        {
            if (force) throw new MediaException("İndirme sürerken yt-dlp güncellenemez; indirmeler bitince tekrar dene.");
            return false;
        }
        _sessionCheck = now;
        _setLastCheck(now);
        try
        {
            ExternalToolUpdateInfo info = await _tools.CheckForUpdateAsync(YtDlp, cancellationToken).ConfigureAwait(false);
            if (!info.UpdateAvailable) return false;
            ToolJobState state = await _jobs.RunAsync("yt-dlp güncelleniyor", async (job, token) =>
            {
                var progress = new SyncProgress(p => job.Report(p.Fraction, p.Describe()));
                await _tools.InstallAsync(YtDlp, info.Release, progress, token).ConfigureAwait(false);
                job.Complete("yt-dlp " + info.LatestVersion);
            }).ConfigureAwait(false);
            return state == ToolJobState.Completed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !force)
        {
            _log("yt-dlp güncelleme denetimi başarısız; kurulu sürüm kullanılıyor.", ex);
            return false;
        }
    }

    private sealed class SyncProgress : IProgress<ExternalToolProgress>
    {
        private readonly Action<ExternalToolProgress> _report;
        public SyncProgress(Action<ExternalToolProgress> report) => _report = report;
        public void Report(ExternalToolProgress value) => _report(value);
    }
}
