using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BroidShot.Core;

namespace BroidShot.Modules.Media;

/// <summary>Raised when yt-dlp/ffmpeg are missing and the user has not yet agreed to download them.</summary>
internal sealed class ToolConsentRequiredException : Exception
{
    public ToolConsentRequiredException(string prompt) : base(prompt) { }
}

/// <summary>Video/audio downloads with yt-dlp + ffmpeg: info, presets, queue, history, clipboard toasts.</summary>
public sealed class MediaModule : IToolModule
{
    private readonly Func<IProcessRunner> _runnerFactory;
    private IToolHost? _host;
    private MediaSettings _settings = new();
    private IProcessRunner? _runner;
    private ToolProvisioner? _provisioner;
    private YtDlpClient? _client;
    private DownloadQueue? _queue;
    private DownloadHistory? _history;
    private readonly ToastGate _toastGate = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private MediaView? _view;
    private string? _pendingUrl;
    private bool _toolConsent;

    public MediaModule() : this(() => new SystemProcessRunner()) { }

    internal MediaModule(Func<IProcessRunner> runnerFactory) => _runnerFactory = runnerFactory;

    public string Id => "media";
    public string Title => "Medya indir";
    public string Description => "Bağlantıdan video veya ses indir (yt-dlp + ffmpeg).";
    public string Category => ToolCategories.Media;
    public string Icon => "\uE896";
    public IReadOnlyList<string> Keywords { get; } = new[]
    {
        "video", "ses", "müzik", "indir", "youtube", "mp4", "mp3", "m4a", "playlist", "yt-dlp", "download", "media", "instagram", "tiktok"
    };

    public IReadOnlyList<ToolCommand> Commands => new[]
    {
        new ToolCommand("Video indir", () => _view?.FocusUrl())
        {
            Id = "media.download",
            Description = "Medya sayfasını açar; bağlantıyı yapıştırıp indir",
            Keywords = new[] { "video", "indir", "youtube", "download", "mp4", "mp3" },
            NavigateToModuleId = Id
        },
        new ToolCommand("Panodaki linki indir", DownloadFromClipboard)
        {
            Id = "media.download-clipboard",
            Description = "Panodaki bağlantının bilgisini alır; indirmeyi sen başlatırsın",
            Keywords = new[] { "pano", "link", "bağlantı", "clipboard", "url", "indir" }
        },
        new ToolCommand("İndirme klasörünü aç", OpenOutputFolder)
        {
            Id = "media.open-folder",
            Description = "İndirilen dosyaların klasörünü Gezgin'de açar",
            Keywords = new[] { "klasör", "indirilenler", "folder", "downloads" }
        },
    };

    internal IToolHost Host => _host ?? throw new InvalidOperationException("Modül henüz başlatılmadı.");
    internal MediaSettings Settings => _settings;
    internal ToolProvisioner Provisioner => _provisioner ?? throw new InvalidOperationException("Modül henüz başlatılmadı.");
    internal DownloadQueue Queue => _queue ?? throw new InvalidOperationException("Modül henüz başlatılmadı.");
    internal DownloadHistory History => _history ?? throw new InvalidOperationException("Modül henüz başlatılmadı.");
    internal bool ToolConsentGranted => _toolConsent;
    internal string? PendingUrl => _pendingUrl;

    public void Initialize(IToolHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = host.Settings.Load<MediaSettings>();
        _settings.MaxParallelDownloads = MediaSettings.ClampParallel(_settings.MaxParallelDownloads);
        if (!YtDlpArguments.IsValidFileNameTemplate(_settings.FileNameTemplate)) _settings.FileNameTemplate = MediaSettings.DefaultFileNameTemplate;

        _runner = _runnerFactory();
        _provisioner = new ToolProvisioner(host.ExternalTools, host.Jobs, () => _settings.LastYtDlpUpdateCheck,
            when => { _settings.LastYtDlpUpdateCheck = when; SaveSettings(); }, host.Log);
        _client = new YtDlpClient(_runner, EnsureToolsAsync);
        _provisioner.ActiveProcessCount = () => _client.ActiveProcesses;
        _queue = new DownloadQueue(_client.DownloadAsync, host.Jobs, Post, _settings.MaxParallelDownloads);
        _queue.ItemCompleted += OnDownloadCompleted;
        _history = new DownloadHistory(Path.Combine(host.DataFolder, "history.json"));
        DownloadHistory history = _history;
        _ = Task.Run(() =>
        {
            try { history.Load(); }
            catch (Exception ex) { host.Log("İndirme geçmişi yüklenemedi", ex); }
        });

        _subscriptions.Add(host.Messages.Subscribe<UrlCopiedMessage>(m => HandleUrlCopied(m)));
        _subscriptions.Add(host.Messages.Subscribe<MediaDownloadRequestedMessage>(m => OpenWithUrl(m.Url)));
    }

    public FrameworkElement CreateView()
    {
        _view = new MediaView(this);
        return _view;
    }

    public void Shutdown()
    {
        try
        {
            foreach (IDisposable subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
            try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
            _queue?.CancelAll();
            _runner?.KillAll();
        }
        catch (Exception ex) { _host?.Log("Medya modülü kapatılırken hata", ex); }
    }

    internal void SaveSettings()
    {
        try { Host.Settings.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Host.Log("Medya ayarları kaydedilemedi", ex); }
    }

    internal void SetMaxParallel(int value)
    {
        _settings.MaxParallelDownloads = MediaSettings.ClampParallel(value);
        Queue.MaxParallel = _settings.MaxParallelDownloads;
        SaveSettings();
    }

    internal void GrantToolConsent() => _toolConsent = true;

    private Task<ToolPaths> EnsureToolsAsync(CancellationToken cancellationToken)
    {
        if (Provisioner.NeedsInstall && !_toolConsent) throw new ToolConsentRequiredException(Provisioner.ConsentText);
        return Provisioner.EnsureAsync(cancellationToken);
    }

    internal async Task<MediaInfo> FetchInfoAsync(string url, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await _client!.FetchInfoAsync(url, _settings.CookieBrowserOrNull(), linked.Token).ConfigureAwait(true);
    }

    /// <summary>Clipboard → toast. Returns true when a toast was shown (supported site, enabled, first time this session).</summary>
    internal bool HandleUrlCopied(UrlCopiedMessage message)
    {
        if (_host is null || !_settings.ClipboardToasts || message is null) return false;
        if (!UrlRules.TryNormalize(message.Url, out string url)) return false;
        string? site = UrlRules.DetectVideoSite(url);
        if (site is null) return false;
        if (!_toastGate.TryEnter(url)) return false;
        _host.ShowToast(new ToastOptions("Video linki algılandı", site + " · " + Shorten(url, 70))
        {
            Kind = ToastKind.Info,
            ActionLabel = "İndir",
            Action = () => OpenWithUrl(url),
            Duration = TimeSpan.FromSeconds(8)
        });
        return true;
    }

    /// <summary>Shows the page with the URL prefilled and fetches its info. Never starts a download.</summary>
    internal void OpenWithUrl(string? url)
    {
        if (_host is null || !UrlRules.TryNormalize(url, out string normalized)) return;
        _pendingUrl = normalized;
        _host.NavigateToSelf();
        _view?.ApplyPendingUrl();
    }

    internal string? TakePendingUrl()
    {
        string? url = _pendingUrl;
        _pendingUrl = null;
        return url;
    }

    private void DownloadFromClipboard()
    {
        string text = "";
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            Host.Log("Pano okunamadı", ex);
        }
        if (UrlRules.TryNormalize(text, out string url)) OpenWithUrl(url);
        else Host.ShowToast("Panoda bağlantı yok", "Panoda http/https ile başlayan bir bağlantı bulunamadı.", kind: ToastKind.Warning);
    }

    internal void OpenOutputFolder()
    {
        string folder = _settings.ResolveOutputFolder();
        try
        {
            Directory.CreateDirectory(folder);
            OpenFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Host.ShowToast("Klasör açılamadı", ex.Message, kind: ToastKind.Error);
        }
    }

    /// <summary>Turns the page selection into queue requests (one per video; playlists: one per selected entry).</summary>
    internal static IReadOnlyList<DownloadRequest> BuildRequests(MediaInfo info, IEnumerable<MediaEntry>? selectedEntries, DownloadPreset preset,
        string? formatId, string outputFolder, string template, string? cookiesBrowser)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (preset == DownloadPreset.Advanced && !YtDlpArguments.IsValidFormatSelector(formatId))
            throw new MediaException("Geçerli bir format kimliği gir (ör. 137+140 veya bv*+ba).");
        if (string.IsNullOrWhiteSpace(outputFolder) || !Path.IsPathRooted(outputFolder))
            throw new MediaException("Çıktı klasörü tam bir yol olmalı (ör. C:\\Kullanıcılar\\...\\İndirilenler).");
        if (!YtDlpArguments.IsValidFileNameTemplate(template))
            throw new MediaException("Dosya adı şablonu geçersiz: göreli olmalı, \"..\" içermemeli ve %(...)s alanı içermeli.");
        string? format = preset == DownloadPreset.Advanced ? formatId!.Trim() : null;

        if (!info.IsPlaylist)
        {
            string url = info.WebpageUrl is { } page && UrlRules.IsAcceptable(page) ? page : info.SourceUrl;
            return new[] { Request(url, info.Title, null) };
        }
        MediaEntry[] entries = (selectedEntries ?? Array.Empty<MediaEntry>()).ToArray();
        if (entries.Length == 0) throw new MediaException("Listeden en az bir öğe seç.");
        return entries.Select(e => e.Url is { } entryUrl
            ? Request(entryUrl, e.Title, null)
            : Request(info.SourceUrl, e.Title, e.Index)).ToArray();

        DownloadRequest Request(string url, string title, int? playlistItem) => new()
        {
            Url = url,
            Title = title,
            Preset = preset,
            FormatId = format,
            OutputFolder = outputFolder,
            FileNameTemplate = template,
            PlaylistItem = playlistItem,
            CookiesBrowser = cookiesBrowser
        };
    }

    internal int StartDownloads(IReadOnlyList<DownloadRequest> requests)
    {
        foreach (DownloadRequest request in requests) Queue.Enqueue(request);
        return requests.Count;
    }

    private void OnDownloadCompleted(DownloadItem item)
    {
        History.Add(new HistoryEntry
        {
            Title = item.Title,
            Url = item.Request.Url,
            FilePath = item.FilePath,
            Preset = item.Request.Preset,
            CompletedAt = DateTimeOffset.Now
        });
        string? path = item.FilePath;
        Host.ShowToast(new ToastOptions("İndirme bitti", path is null ? item.Title : Path.GetFileName(path))
        {
            Kind = ToastKind.Success,
            ActionLabel = path is null ? "Klasörü aç" : "Aç",
            Action = () => { if (path is null) OpenFolder(item.Request.OutputFolder); else OpenFile(path); },
            Duration = TimeSpan.FromSeconds(8)
        });
    }

    internal void Post(Action action)
    {
        if (_host is null) { action(); return; }
        var dispatcher = _host.Dispatcher;
        if (dispatcher.CheckAccess()) action();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
    }

    internal static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    internal static void OpenFile(string path)
    {
        if (!File.Exists(path)) { OpenFolder(Path.GetDirectoryName(path) ?? path); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    internal static void OpenFolder(string folder)
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add(folder);
        Process.Start(info)?.Dispose();
    }

    internal static void RevealFile(string path)
    {
        if (!File.Exists(path)) { OpenFolder(Path.GetDirectoryName(path) ?? path); return; }
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add("/select," + path);
        Process.Start(info)?.Dispose();
    }
}
