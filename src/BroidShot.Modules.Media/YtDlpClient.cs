using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BroidShot.Modules.Media;

/// <summary>Runs yt-dlp for info (-J) and downloads, parsing its machine-readable output.</summary>
internal sealed class YtDlpClient
{
    private readonly IProcessRunner _runner;
    private readonly Func<CancellationToken, Task<ToolPaths>> _tools;
    private int _active;

    public static IReadOnlyDictionary<string, string> ProcessEnvironment { get; } = new Dictionary<string, string>
    {
        ["PYTHONIOENCODING"] = "utf-8",
        ["PYTHONUTF8"] = "1"
    };

    public YtDlpClient(IProcessRunner runner, Func<CancellationToken, Task<ToolPaths>> tools)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    }

    public int ActiveProcesses => Volatile.Read(ref _active);

    public async Task<MediaInfo> FetchInfoAsync(string url, string? cookiesBrowser, CancellationToken cancellationToken)
    {
        if (!UrlRules.TryNormalize(url, out string normalized)) throw new MediaException("Yalnız http:// veya https:// ile başlayan bağlantılar desteklenir.");
        ToolPaths tools = await _tools(cancellationToken).ConfigureAwait(false);
        try { return await FetchInfoCoreAsync(tools, normalized, cookiesBrowser, cancellationToken).ConfigureAwait(false); }
        catch (MediaException ex) when (cookiesBrowser is not null && IsCookieFailure(ex.Message))
        {
            // Chromium browsers lock (and app-bound encrypt) their cookie store while running; most links work without it.
            return await FetchInfoCoreAsync(tools, normalized, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<MediaInfo> FetchInfoCoreAsync(ToolPaths tools, string normalized, string? cookiesBrowser, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> args = YtDlpArguments.BuildInfo(normalized, cookiesBrowser);
        var output = new StringBuilder();
        var errors = new ErrorCollector();
        int exit = await RunAsync(new ProcessSpec(tools.YtDlp, args, ProcessEnvironment), line => { lock (output) output.AppendLine(line); }, errors.Add, cancellationToken)
            .ConfigureAwait(false);
        string json;
        lock (output) json = output.ToString();
        if (exit != 0 && json.TrimStart().StartsWith('{') is false)
            throw new MediaException(errors.Message ?? $"Bilgi alınamadı (yt-dlp çıkış kodu {exit}).");
        return MediaInfoParser.Parse(json, normalized);
    }

    /// <summary>Downloads and returns the final file path (null when yt-dlp did not report one).</summary>
    public async Task<string?> DownloadAsync(DownloadRequest request, Action<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        ToolPaths tools = await _tools(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(request.OutputFolder);
        try { return await DownloadCoreAsync(tools, request, progress, cancellationToken).ConfigureAwait(false); }
        catch (MediaException ex) when (request.CookiesBrowser is not null && IsCookieFailure(ex.Message))
        {
            return await DownloadCoreAsync(tools, request with { CookiesBrowser = null }, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>True for yt-dlp errors about reading or decrypting the browser cookie store.</summary>
    internal static bool IsCookieFailure(string message) =>
        message.Contains("cookie", StringComparison.OrdinalIgnoreCase) &&
        (message.Contains("database", StringComparison.OrdinalIgnoreCase) || message.Contains("decrypt", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("DPAPI", StringComparison.OrdinalIgnoreCase) || message.Contains("copy", StringComparison.OrdinalIgnoreCase));

    private async Task<string?> DownloadCoreAsync(ToolPaths tools, DownloadRequest request, Action<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> args = YtDlpArguments.BuildDownload(request, tools.FFmpegDirectory);
        string? file = null;
        var errors = new ErrorCollector();
        // Progress and --print output normally arrive on stdout, but quiet mode may route them to stderr; accept both.
        void OnLine(string line)
        {
            if (ProgressParser.TryParse(line, out DownloadProgress p)) progress(p);
            else if (ProgressParser.TryParseFile(line, out string path)) Volatile.Write(ref file, path);
            else errors.Add(line);
        }
        int exit = await RunAsync(new ProcessSpec(tools.YtDlp, args, ProcessEnvironment, request.OutputFolder), OnLine, OnLine, cancellationToken).ConfigureAwait(false);
        if (exit != 0) throw new MediaException(errors.Message ?? $"İndirme başarısız (yt-dlp çıkış kodu {exit}).");
        return file;
    }

    private async Task<int> RunAsync(ProcessSpec spec, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _active);
        try { return await _runner.RunAsync(spec, onOutput, onError, cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _active); }
    }

    /// <summary>Keeps the last "ERROR:" line from yt-dlp as the user-facing message.</summary>
    private sealed class ErrorCollector
    {
        private readonly object _gate = new();
        private string? _message;

        public string? Message { get { lock (_gate) return _message; } }

        public void Add(string line)
        {
            if (ProgressParser.TryParseError(line, out string message)) lock (_gate) _message = Shorten(message);
        }

        private static string Shorten(string message) => message.Length > 400 ? message[..400] + "…" : message;
    }
}
