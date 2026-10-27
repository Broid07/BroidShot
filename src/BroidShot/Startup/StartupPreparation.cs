using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BroidShot.Core;
using BroidShot.Updates;

namespace BroidShot.Startup;

internal sealed record PreparationProgress(string ToolId, string Title, string Detail, double? Fraction, bool Complete = false, string State = "Hazırlanıyor", string? Version = null, string? Notes = null);

/// <summary>Installs missing tools and checks installed tools for updates on every launch.</summary>
internal sealed class StartupPreparation
{
    private readonly IExternalToolManager _tools;
    private readonly Func<CancellationToken, Task>? _waitForIdle;
    private readonly AppUpdateService? _appUpdates;
    private readonly bool _verifyOnly;
    internal string? PendingPackage { get; private set; }
    internal static IReadOnlyList<ExternalToolDefinition> Tools { get; } = new[] { KnownExternalTools.YtDlp, KnownExternalTools.FFmpeg };

    internal StartupPreparation(IExternalToolManager tools, Func<CancellationToken, Task>? waitForIdle = null, AppUpdateService? appUpdates = null, bool verifyOnly = false)
    {
        _tools = tools;
        _waitForIdle = waitForIdle;
        _appUpdates = appUpdates;
        _verifyOnly = verifyOnly;
    }

    internal bool IsInstalled(ExternalToolDefinition tool) => _tools.TryGetExecutable(tool) is not null;

    internal static bool HasOcrModels(string appFolder) =>
        File.Exists(Path.Combine(appFolder, "tessdata", "tur.traineddata")) &&
        File.Exists(Path.Combine(appFolder, "tessdata", "eng.traineddata"));

    internal async Task PrepareAsync(IProgress<PreparationProgress> progress, CancellationToken token)
    {
        int steps = Tools.Count + (_appUpdates is null ? 0 : 1);
        if (_waitForIdle is not null)
        {
            progress.Report(new("", "", "Çalışan işlemler tamamlanınca güncellemeler kontrol edilecek…", null));
            await _waitForIdle(token).ConfigureAwait(false);
        }
        for (int i = 0; i < Tools.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ExternalToolDefinition tool = Tools[i];
            int completed = i;
            var downloadProgress = new InlineProgress<ExternalToolProgress>(p =>
            {
                double? fraction = p.Stage switch
                {
                    ExternalToolStage.Downloading when p.Fraction is { } f => (completed + f * 0.9) / steps,
                    ExternalToolStage.Verifying => (completed + 0.92) / steps,
                    ExternalToolStage.Extracting => (completed + 0.96) / steps,
                    ExternalToolStage.Completed => (completed + 1d) / steps,
                    _ => null
                };
                string detail = p.Describe();
                if (p.Stage == ExternalToolStage.Downloading)
                {
                    static string Mb(long value) => (value / 1048576d).ToString("0.0");
                    detail += p.TotalBytes is { } total ? $" · {Mb(p.BytesReceived)} / {Mb(total)} MB" : $" · {Mb(p.BytesReceived)} MB";
                    if (p.BytesPerSecond is { } speed) detail += $" · {speed / 1048576:0.0} MB/sn";
                    if (p.EstimatedRemaining is { } eta) detail += $" · yaklaşık {Math.Ceiling(eta.TotalSeconds)} sn";
                }
                string state = p.Stage switch
                {
                    ExternalToolStage.Downloading => "İndiriliyor",
                    ExternalToolStage.Verifying => "Doğrulanıyor",
                    ExternalToolStage.Extracting => "Kuruluyor",
                    _ => "Hazırlanıyor"
                };
                progress.Report(new(tool.Name, tool.DisplayName, detail, fraction, State: state));
            });
            string readyState = "Hazır";
            if (!IsInstalled(tool))
            {
                progress.Report(new(tool.Name, tool.DisplayName, tool.DisplayName + " kurulumu hazırlanıyor…", null));
                await _tools.EnsureAsync(tool, downloadProgress, token).ConfigureAwait(false);
            }
            else if (!_verifyOnly)
            {
                progress.Report(new(tool.Name, tool.DisplayName, tool.DisplayName + " güncellemeleri kontrol ediliyor…", null, State: "Kontrol ediliyor"));
                ExternalToolUpdateInfo update;
                try { update = await _tools.CheckForUpdateAsync(tool, token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new ExternalToolException(tool.DisplayName + " güncellemesi kontrol edilemedi. Bağlantını kontrol edip yeniden dene.", ex);
                }
                readyState = "Güncel";
                if (update.UpdateAvailable)
                {
                    progress.Report(new(tool.Name, tool.DisplayName, tool.DisplayName + " " + update.LatestVersion + " güncellemesi indiriliyor…", null, State: "Güncelleniyor"));
                    await _tools.InstallAsync(tool, update.Release, downloadProgress, token).ConfigureAwait(false);
                    readyState = "Güncellendi";
                }
            }
            token.ThrowIfCancellationRequested();
            if (!IsInstalled(tool)) throw new ExternalToolException(tool.DisplayName + " kurulumu tamamlanamadı. Yeniden deneyebilirsin.");
            progress.Report(new(tool.Name, tool.DisplayName, tool.DisplayName + " · " + readyState, (i + 1d) / steps, Complete: true, State: readyState, Version: _tools.GetStatus(tool).Version));
        }
        if (_appUpdates is not null)
        {
            progress.Report(new("app", "BroidShot", "BroidShot güncellemeleri kontrol ediliyor…", null, State: "Kontrol ediliyor"));
            AppUpdate? update = await _appUpdates.CheckAsync(token).ConfigureAwait(false);
            if (update is not null)
            {
                progress.Report(new("app", "BroidShot", "BroidShot " + update.Version + " hazırlanıyor…", null, State: "Yeni sürüm", Version: update.Version, Notes: update.Notes));
                PendingPackage = await _appUpdates.DownloadAsync(update, new InlineProgress<ExternalToolProgress>(p =>
                    progress.Report(new("app", "BroidShot", p.Describe(), p.Fraction is { } f ? (Tools.Count + f) / steps : null, Version: update.Version))), token).ConfigureAwait(false);
            }
            progress.Report(new("app", "BroidShot", PendingPackage is null ? "Açılış kontrolleri tamamlandı." : "Yeni sürüm için yeniden başlatılıyor…", 1, true,
                PendingPackage is null ? _appUpdates.Status : "Hazır", Version: update?.Version ?? typeof(App).Assembly.GetName().Version?.ToString(3)));
        }
    }
}

internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;
    internal InlineProgress(Action<T> report) => _report = report;
    public void Report(T value) => _report(value);
}
