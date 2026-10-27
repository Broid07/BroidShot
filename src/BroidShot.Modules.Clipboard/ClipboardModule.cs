using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BroidShot.Core;
using BroidShot.Modules.Clipboard.Native;
using BroidShot.Modules.Clipboard.Storage;
using WpfClipboard = System.Windows.Clipboard;

namespace BroidShot.Modules.Clipboard;

/// <summary>Test seams. The shell uses the defaults (real clipboard, system clock, DPAPI).</summary>
internal sealed class ClipboardModuleOptions
{
    /// <summary>False in tests: no clipboard listener, foreground hook or global hotkey.</summary>
    public bool UseSystemClipboard { get; init; } = true;
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public IContentProtector Protector { get; init; } = DpapiContentProtector.Instance;
}

/// <summary>Clipboard manager: history with types, tags, pinning, search, sensitive-data masking and a quick panel.</summary>
public sealed class ClipboardModule : IToolModule
{
    public const string ModuleId = "clipboard";
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromSeconds(30);

    private readonly ClipboardModuleOptions _options;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private IToolHost? _host;
    private ClipboardSettings _settings = new();
    private ClipboardHistory? _history;
    private Task _ready = Task.CompletedTask;
    private ClipboardListener? _listener;
    private ForegroundTracker? _foreground;
    private HotkeyRegistration? _hotkey;
    private WinVTakeover? _takeover;
    private string? _takeoverError;
    private DispatcherTimer? _purgeTimer;
    private QuickPanelWindow? _quickPanel;
    private ClipboardView? _view;
    private int _changePending;
    private bool _shutdown;

    public ClipboardModule() : this(new ClipboardModuleOptions()) { }

    internal ClipboardModule(ClipboardModuleOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    public string Id => ModuleId;
    public string Title => "Pano";
    public string Description => "Kopyaladıklarının geçmişi: arama, etiketler, hassas veri koruması ve hızlı yapıştırma paneli.";
    public string Category => ToolCategories.Clipboard;
    public string Icon => "";
    public IReadOnlyList<string> Keywords { get; } = new[] { "pano", "clipboard", "geçmiş", "history", "kopyala", "yapıştır", "paste", "etiket", "sabitle" };

    public IReadOnlyList<ToolCommand> Commands => new[]
    {
        ToolCommand.Open("Pano geçmişini aç", ModuleId, "Kopyalanan metin, görsel ve dosyaların listesi", new[] { "clipboard", "history", "geçmiş" }, "clipboard.open"),
        new ToolCommand("Pano hızlı paneli", ShowQuickPanel)
        {
            Id = "clipboard.quick-panel",
            Description = "İmlecin yanında son kopyalananları açar; Enter yapıştırır",
            Keywords = new[] { "hızlı", "panel", "yapıştır", "paste", "quick" },
        },
        new ToolCommand("Pano geçmişini temizle", RequestClear)
        {
            Id = "clipboard.clear",
            Description = "Sabitlenmemiş öğeleri siler (onay istenir)",
            Keywords = new[] { "sil", "temizle", "clear", "delete" },
            NavigateToModuleId = ModuleId,
        },
    };

    internal IToolHost Host => _host ?? throw new InvalidOperationException("Modül henüz başlatılmadı.");
    internal ClipboardSettings Settings => _settings;
    internal ClipboardHistory? History => _history;
    internal Task Ready => _ready;
    internal TimeProvider Time => _options.Time;
    internal HotkeyRegistration? QuickPanelHotkey => _hotkey;
    /// <summary>True while the keyboard hook that takes Win+V over from Windows is installed.</summary>
    internal bool WinVTakeoverActive => _takeover?.IsRunning == true;
    /// <summary>Turkish reason when the Win+V hook could not be installed; null otherwise.</summary>
    internal string? WinVTakeoverError => _takeoverError;
    internal bool ListenerRunning => _listener?.IsRunning == true;

    /// <summary>Raised on the UI thread (coalesced) when items or tags change.</summary>
    internal event EventHandler? HistoryChanged;

    public void Initialize(IToolHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = host.Settings.Load<ClipboardSettings>().Normalized();
        string folder = host.DataFolder;
        _ready = RunQueuedAsync(() =>
        {
            var store = new ClipStore(folder, _options.Protector);
            var history = new ClipboardHistory(store, _options.Time) { Retention = _settings.Retention };
            history.Changed += OnHistoryChanged;
            history.Load(); // also deletes expired sensitive items left from the last session
            _history = history;
        });
        _ready.ContinueWith(t => host.Log("Pano geçmişi açılamadı.", t.Exception?.GetBaseException()), TaskContinuationOptions.OnlyOnFaulted);

        _purgeTimer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher) { Interval = PurgeInterval };
        _purgeTimer.Tick += (_, _) => _ = PurgeExpiredAsync();
        _purgeTimer.Start();

        if (_options.UseSystemClipboard)
        {
            _listener = new ClipboardListener(host.Dispatcher, (message, ex) => host.Log(message, ex));
            _listener.Captured += capture => _ = ProcessCaptureAsync(capture);
            ApplyMonitoring();
            try { _foreground = new ForegroundTracker(); }
            catch (Exception ex) { host.Log("Ön plan izleyicisi başlatılamadı.", ex); }
            ApplyHotkey();
        }
    }

    public FrameworkElement CreateView() => _view = new ClipboardView(this);

    public void Shutdown()
    {
        _shutdown = true;
        try { _purgeTimer?.Stop(); } catch (Exception) { }
        try { _listener?.Dispose(); } catch (Exception) { }
        try { _foreground?.Dispose(); } catch (Exception) { }
        try { _hotkey?.Dispose(); } catch (Exception) { }
        try { _takeover?.Dispose(); } catch (Exception) { }
        _takeover = null;
        try { _quickPanel?.Close(); } catch (Exception) { }
        try
        {
            // Let queued writes finish briefly, then close the database.
            if (_queue.Wait(TimeSpan.FromSeconds(2)))
            {
                _history?.Dispose();
                _queue.Release();
            }
        }
        catch (Exception) { }
    }

    // ---- background queue: every history/store call runs here, one at a time, off the UI thread ----

    internal async Task RunQueuedAsync(Action work)
    {
        await _queue.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(work).ConfigureAwait(false); }
        finally { _queue.Release(); }
    }

    internal async Task<T> RunQueuedAsync<T>(Func<T> work)
    {
        await _queue.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(work).ConfigureAwait(false); }
        finally { _queue.Release(); }
    }

    /// <summary>Runs a history action in the queue; failures are logged (without content) and reported as a toast.</summary>
    internal async Task<bool> UpdateHistoryAsync(Action<ClipboardHistory> action, string failureMessage)
    {
        try
        {
            await _ready.ConfigureAwait(true);
            ClipboardHistory history = _history ?? throw new InvalidOperationException("Pano geçmişi açık değil.");
            await RunQueuedAsync(() => action(history)).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _host?.ShowToast(failureMessage, ex.Message, kind: ToastKind.Warning);
            return false;
        }
        catch (Exception ex)
        {
            _host?.Log(failureMessage, ex);
            _host?.ShowToast(failureMessage, "Ayrıntılar hata günlüğünde.", kind: ToastKind.Error);
            return false;
        }
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (_host is null || Interlocked.Exchange(ref _changePending, 1) == 1) return;
        _host.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _changePending, 0);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }));
    }

    // ---- capture pipeline ----

    /// <summary>Blocklist → store (classify, detect sensitive, dedupe) → publish non-sensitive URLs for the media module.</summary>
    internal async Task<CaptureResult?> ProcessCaptureAsync(ClipCapture capture)
    {
        if (_shutdown || !_settings.MonitoringEnabled || _settings.IsBlocked(capture.SourceApp)) return null;
        try
        {
            await _ready.ConfigureAwait(false);
            ClipboardHistory history = _history ?? throw new InvalidOperationException("Pano geçmişi açık değil.");
            CaptureResult result = await RunQueuedAsync(() => history.Add(capture)).ConfigureAwait(false);
            if (result.Url is { } url && result.Entry is { Sensitive: false } entry)
                _host?.Messages.Publish(new UrlCopiedMessage(url, entry.IsVideoLink, capture.SourceApp, DateTimeOffset.Now));
            return result;
        }
        catch (Exception ex)
        {
            _host?.Log("Pano öğesi kaydedilemedi.", ex); // the content is never logged
            return null;
        }
    }

    private async Task PurgeExpiredAsync()
    {
        if (_history is not { } history || _shutdown) return;
        try { await RunQueuedAsync(() => history.PurgeExpired()); }
        catch (Exception ex) { _host?.Log("Süresi dolan hassas öğeler silinemedi.", ex); }
    }

    // ---- item actions (UI thread) ----

    /// <summary>Puts an item back on the clipboard (ignored by our own listener) and moves it to the top.</summary>
    internal bool CopyToClipboard(long id)
    {
        if (_history is not { } history) return false;
        ClipEntry? entry = history.Find(id);
        if (entry is null) return false;
        var data = new DataObject();
        if (entry.Kind == ClipKind.Image)
        {
            byte[]? png = history.GetImagePng(id);
            if (png is null) return false;
            BitmapSource image = DecodePng(png, 0);
            data.SetImage(image);
            data.SetData("PNG", new MemoryStream(png));
        }
        else
        {
            ClipPayload? payload = history.GetPayload(id);
            if (payload is null) return false;
            if (payload.Files is { Count: > 0 } files)
            {
                var collection = new StringCollection();
                collection.AddRange(files.ToArray());
                data.SetFileDropList(collection);
            }
            if (!string.IsNullOrEmpty(payload.Text)) data.SetText(payload.Text, TextDataFormat.UnicodeText);
            if (!string.IsNullOrEmpty(payload.Html)) data.SetData(DataFormats.Html, payload.Html);
        }
        try
        {
            if (_listener is not null) _listener.SetClipboard(data);
            else WpfClipboard.SetDataObject(data, true);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            Host.ShowToast("Panoya kopyalanamadı", "Pano başka bir uygulama tarafından kullanılıyor. Tekrar dene.", kind: ToastKind.Warning);
            return false;
        }
        _ = UpdateHistoryAsync(h => h.Touch(id), "Öğe güncellenemedi");
        return true;
    }

    /// <summary>Copies the item, returns to <paramref name="target"/> (or the last other-app window) and sends Ctrl+V.</summary>
    internal async Task PasteAsync(long id, IntPtr target = default)
    {
        if (!CopyToClipboard(id)) return;
        if (target == IntPtr.Zero) target = _foreground?.LastExternalWindow ?? IntPtr.Zero;
        if (!await PasteHelper.PasteIntoAsync(target))
            Host.ShowToast("Panoya kopyalandı", "Hedef pencere bulunamadı; Ctrl+V ile yapıştırabilirsin.");
    }

    internal Task SetPinnedAsync(long id, bool pinned) => UpdateHistoryAsync(h => h.SetPinned(id, pinned), "Sabitleme değiştirilemedi");
    internal Task DeleteAsync(long id) => UpdateHistoryAsync(h => h.Delete(id), "Öğe silinemedi");
    internal Task SetTagAsync(long itemId, long tagId, bool assigned) => UpdateHistoryAsync(h => h.SetTag(itemId, tagId, assigned), "Etiket değiştirilemedi");
    internal Task<bool> CreateTagAsync(string name) => UpdateHistoryAsync(h => h.CreateTag(name), "Etiket oluşturulamadı");
    internal Task<bool> RenameTagAsync(long id, string name) => UpdateHistoryAsync(h => h.RenameTag(id, name), "Etiket yeniden adlandırılamadı");
    internal Task DeleteTagAsync(long id) => UpdateHistoryAsync(h => h.DeleteTag(id), "Etiket silinemedi");

    internal async Task<int> ClearHistoryAsync()
    {
        int removed = 0;
        await UpdateHistoryAsync(h => removed = h.ClearUnpinned(), "Geçmiş temizlenemedi");
        return removed;
    }

    /// <summary>Decrypts a sensitive item for an explicit reveal. Returns null when it is gone.</summary>
    internal string? RevealText(long id)
    {
        try
        {
            ClipPayload? payload = _history?.GetPayload(id);
            if (payload is null) return null;
            if (payload.Files is { Count: > 0 } files) return string.Join(Environment.NewLine, files);
            return payload.Text ?? "";
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            Host.Log("Hassas öğenin şifresi çözülemedi.", ex);
            return null;
        }
    }

    internal static BitmapSource DecodePng(byte[] png, int decodeWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // ---- settings ----

    internal void UpdateSettings(ClipboardSettings settings)
    {
        _settings = settings.Normalized();
        Host.Settings.Save(_settings);
        if (_history is { } history)
        {
            history.Retention = _settings.Retention;
            _ = UpdateHistoryAsync(h => h.ApplyRetention(), "Saklama kuralları uygulanamadı");
        }
        if (_options.UseSystemClipboard)
        {
            ApplyMonitoring();
            ApplyHotkey();
        }
    }

    private void ApplyMonitoring()
    {
        if (_listener is null) return;
        if (!_settings.MonitoringEnabled) { _listener.Stop(); return; }
        try { _listener.Start(); }
        catch (Exception ex) { Host.Log("Pano dinleyicisi başlatılamadı.", ex); }
    }

    private void ApplyHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        _takeover?.Dispose(); // setting off (or another shortcut): Windows' own Win+V works again right away
        _takeover = null;
        _takeoverError = null;
        if (!_settings.QuickPanelEnabled || _shutdown) return;
        if (_settings.UsesWinVTakeover)
        {
            var takeover = new WinVTakeover(target => _host?.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => ShowQuickPanel(target))));
            if (takeover.Start(out string? error)) _takeover = takeover;
            else
            {
                _takeoverError = error;
                Host.Log("Win+V klavye kancası kurulamadı: " + error);
            }
            return;
        }
        HotkeyGesture gesture = HotkeyGesture.TryParse(_settings.QuickPanelHotkey, out HotkeyGesture parsed) ? parsed : HotkeyGesture.Parse(ClipboardSettings.DefaultQuickPanelHotkey);
        _hotkey = Host.RegisterHotkey(gesture, ShowQuickPanel);
    }

    // ---- quick panel and palette ----

    internal void ShowQuickPanel() => ShowQuickPanel(IntPtr.Zero);

    /// <param name="foreground">Window that was in front when the shortcut was pressed (captured by the hook); zero reads it now.</param>
    internal void ShowQuickPanel(IntPtr foreground)
    {
        if (_host is null || _shutdown) return;
        if (_quickPanel is not null) { _quickPanel.Close(); return; }
        IntPtr target = foreground != IntPtr.Zero ? foreground : NativeMethods.GetForegroundWindow();
        if (PasteHelper.IsOwnWindow(target) || target == IntPtr.Zero) target = _foreground?.LastExternalWindow ?? IntPtr.Zero;
        var panel = new QuickPanelWindow(this, target);
        panel.Closed += (_, _) => { if (ReferenceEquals(_quickPanel, panel)) _quickPanel = null; };
        _quickPanel = panel;
        panel.ShowNearCursor();
    }

    private void RequestClear()
    {
        if (_view is not null) _view.ShowClearConfirmation();
        else Host.NavigateToSelf();
    }
}
