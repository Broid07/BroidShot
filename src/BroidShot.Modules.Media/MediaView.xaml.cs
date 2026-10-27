using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using BroidShot.Core;

namespace BroidShot.Modules.Media;

public partial class MediaView : UserControl
{
    private const int HistoryShown = 50;
    private readonly MediaModule _module;
    private CancellationTokenSource? _fetch;
    private MediaInfo? _info;
    private string? _consentPendingUrl;
    private bool _loading = true;

    internal MediaView(MediaModule module)
    {
        _module = module;
        if (TryFindResource("Card") is null) Resources.MergedDictionaries.Add(ThemeResources.Load());
        InitializeComponent();

        PresetBox.ItemsSource = YtDlpArguments.PresetOptions;
        PresetBox.SelectedItem = YtDlpArguments.PresetOptions.FirstOrDefault(o => o.Preset == module.Settings.LastPreset) ?? YtDlpArguments.PresetOptions[0];
        OutputBox.Text = module.Settings.ResolveOutputFolder();
        TemplateBox.Text = module.Settings.FileNameTemplate;
        ParallelBox.ItemsSource = Enumerable.Range(1, 6).ToArray();
        ParallelBox.SelectedItem = module.Settings.MaxParallelDownloads;
        ClipboardToastsBox.IsChecked = module.Settings.ClipboardToasts;
        CookiesBox.IsChecked = module.Settings.UseBrowserCookies;
        BrowserBox.ItemsSource = YtDlpArguments.SupportedBrowsers;
        BrowserBox.SelectedItem = YtDlpArguments.IsSupportedBrowser(module.Settings.CookieBrowser) ? module.Settings.CookieBrowser : "edge";
        BrowserBox.IsEnabled = module.Settings.UseBrowserCookies;

        QueueList.ItemsSource = module.Queue.Items;
        ((INotifyCollectionChanged)module.Queue.Items).CollectionChanged += (_, _) => UpdateQueueEmpty();
        module.History.Changed += () => _module.Post(RefreshHistory);
        UpdateQueueEmpty();
        RefreshHistory();
        RefreshToolStatus();
        _loading = false;

        Loaded += (_, _) => { ApplyPendingUrl(); RefreshToolStatus(); };
    }

    internal void FocusUrl()
    {
        ShowPage("download");
        UrlBox.Focus();
        UrlBox.SelectAll();
    }

    /// <summary>Takes the URL handed over by a toast/message/command, fills the box and fetches its info.</summary>
    internal void ApplyPendingUrl()
    {
        if (_module.TakePendingUrl() is not { } url) return;
        ShowPage("download");
        UrlBox.Text = url;
        _ = FetchAsync(url);
    }

    private void PageChanged(object sender, RoutedEventArgs e) => ShowPage((sender as FrameworkElement)?.Tag as string ?? "download");

    private void ShowPage(string page)
    {
        DownloadTab.IsChecked = page == "download";
        HistoryTab.IsChecked = page == "history";
        SettingsTab.IsChecked = page == "settings";
        DownloadPage.Visibility = page == "download" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "settings") RefreshToolStatus();
    }

    // ---- info ----

    private void UrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = FetchAsync(UrlBox.Text);
    }

    private void FetchClicked(object sender, RoutedEventArgs e) => _ = FetchAsync(UrlBox.Text);

    private void PasteUrl(object sender, RoutedEventArgs e)
    {
        string text = "";
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException) { }
        if (UrlRules.TryNormalize(text, out string url))
        {
            UrlBox.Text = url;
            _ = FetchAsync(url);
        }
        else ShowFetchStatus("Panoda http/https ile başlayan bir bağlantı yok.");
    }

    private async Task FetchAsync(string? text)
    {
        if (!UrlRules.TryNormalize(text, out string url))
        {
            ShowFetchStatus("Geçerli bir bağlantı gir: yalnız http:// veya https:// ile başlayan adresler desteklenir.");
            return;
        }
        if (_module.Provisioner.NeedsInstall && !_module.ToolConsentGranted)
        {
            ShowConsent(url);
            return;
        }

        _fetch?.Cancel();
        var cancellation = new CancellationTokenSource();
        _fetch = cancellation;
        FetchProgress.Visibility = Visibility.Visible;
        ShowFetchStatus(_module.Provisioner.NeedsInstall ? "Araçlar hazırlanıyor, ardından bilgi alınacak…" : "Bilgi alınıyor…");
        try
        {
            MediaInfo info = await _module.FetchInfoAsync(url, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            ShowInfo(info);
            HideFetchStatus();
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_fetch, cancellation)) HideFetchStatus(); }
        catch (ToolConsentRequiredException) { ShowConsent(url); HideFetchStatus(); }
        catch (Exception ex) when (ex is MediaException or ExternalToolException)
        {
            if (ReferenceEquals(_fetch, cancellation)) ShowFetchStatus(ex.Message);
        }
        catch (Exception ex)
        {
            _module.Host.Log("Video bilgisi alınamadı", ex);
            if (ReferenceEquals(_fetch, cancellation)) ShowFetchStatus("Bilgi alınamadı: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_fetch, cancellation)) FetchProgress.Visibility = Visibility.Collapsed;
            RefreshToolStatus();
        }
    }

    private void ShowInfo(MediaInfo info)
    {
        _info = info;
        InfoCard.Visibility = Visibility.Visible;
        InfoTitle.Text = info.Title;
        SetText(InfoUploader, info.Uploader is null ? "" : "Yükleyen: " + info.Uploader);
        SetText(InfoDuration, info.IsPlaylist ? $"Oynatma listesi · {info.Entries.Count} öğe" : info.DurationText.Length > 0 ? "Süre: " + info.DurationText : "");
        SetText(InfoQualities, info.QualitiesText.Length > 0 ? "Kaliteler: " + info.QualitiesText : "");
        Thumbnail.Source = LoadThumbnail(info.Thumbnail);

        PlaylistPanel.Visibility = info.IsPlaylist ? Visibility.Visible : Visibility.Collapsed;
        EntryList.ItemsSource = info.IsPlaylist ? info.Entries : null;
        foreach (MediaEntry entry in info.Entries) entry.PropertyChanged += (_, _) => UpdatePlaylistCount();
        UpdatePlaylistCount();

        FormatList.ItemsSource = info.Formats.Reverse().ToArray();
        DownloadStatus.Visibility = Visibility.Collapsed;
    }

    private static BitmapImage? LoadThumbnail(string? url)
    {
        if (url is null || !UrlRules.TryNormalize(url, out string safe)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(safe);
            image.DecodePixelWidth = 384;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UriFormatException or ArgumentException) { return null; }
    }

    private void UpdatePlaylistCount()
    {
        if (_info is not { IsPlaylist: true } info) return;
        PlaylistCount.Text = $"{info.Entries.Count(e => e.IsSelected)} / {info.Entries.Count} öğe seçili";
    }

    private void SelectAll(object sender, RoutedEventArgs e) { foreach (MediaEntry entry in _info?.Entries ?? Array.Empty<MediaEntry>()) entry.IsSelected = true; }
    private void SelectNone(object sender, RoutedEventArgs e) { foreach (MediaEntry entry in _info?.Entries ?? Array.Empty<MediaEntry>()) entry.IsSelected = false; }

    // ---- consent ----

    private void ShowConsent(string url)
    {
        _consentPendingUrl = url;
        ConsentText.Text = _module.Provisioner.ConsentText;
        ConsentCard.Visibility = Visibility.Visible;
        ConsentCard.BringIntoView();
    }

    private void AcceptConsent(object sender, RoutedEventArgs e)
    {
        _module.GrantToolConsent();
        ConsentCard.Visibility = Visibility.Collapsed;
        string? url = _consentPendingUrl;
        _consentPendingUrl = null;
        if (url is not null) _ = FetchAsync(url);
    }

    private void DeclineConsent(object sender, RoutedEventArgs e)
    {
        ConsentCard.Visibility = Visibility.Collapsed;
        _consentPendingUrl = null;
        ShowFetchStatus("İndirme için yt-dlp ve ffmpeg gerekli. Hazır olduğunda \"Bilgi al\" ile tekrar başlayabilirsin.");
    }

    // ---- options ----

    private DownloadPreset SelectedPreset => (PresetBox.SelectedItem as PresetOption)?.Preset ?? DownloadPreset.VideoBest;

    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        AdvancedPanel.Visibility = SelectedPreset == DownloadPreset.Advanced ? Visibility.Visible : Visibility.Collapsed;
        if (_loading) return;
        _module.Settings.LastPreset = SelectedPreset;
        _module.SaveSettings();
    }

    private void FormatPicked(object sender, SelectionChangedEventArgs e)
    {
        if (FormatList.SelectedItem is MediaFormat format) FormatBox.Text = format.FormatId;
    }

    private void BrowseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "İndirme klasörünü seç" };
        if (Directory.Exists(OutputBox.Text)) dialog.InitialDirectory = OutputBox.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) OutputBox.Text = dialog.FolderName;
    }

    private void ResetTemplate(object sender, RoutedEventArgs e) => TemplateBox.Text = MediaSettings.DefaultFileNameTemplate;

    private void DownloadClicked(object sender, RoutedEventArgs e)
    {
        if (_info is not { } info) return;
        string folder = OutputBox.Text.Trim();
        string template = TemplateBox.Text.Trim();
        try
        {
            var requests = MediaModule.BuildRequests(info, info.IsPlaylist ? info.Entries.Where(x => x.IsSelected) : null, SelectedPreset,
                FormatBox.Text, folder, template, _module.Settings.CookieBrowserOrNull());
            _module.Settings.OutputFolder = string.Equals(folder, MediaSettings.DefaultOutputFolder(), StringComparison.OrdinalIgnoreCase) ? null : folder;
            _module.Settings.FileNameTemplate = template;
            _module.Settings.LastPreset = SelectedPreset;
            _module.SaveSettings();
            int count = _module.StartDownloads(requests);
            DownloadStatus.Text = count == 1 ? "İndirme kuyruğa eklendi." : $"{count} indirme kuyruğa eklendi.";
        }
        catch (MediaException ex) { DownloadStatus.Text = ex.Message; }
        DownloadStatus.Visibility = Visibility.Visible;
    }

    // ---- queue ----

    private void UpdateQueueEmpty()
    {
        int count = _module.Queue.Items.Count;
        QueueEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueCount.Text = count.ToString();
    }

    private void ParallelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ParallelBox.SelectedItem is not int value) return;
        _module.SetMaxParallel(value);
    }

    private static DownloadItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as DownloadItem;

    private void CancelItem(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } item) _module.Queue.Cancel(item); }
    private void RetryItem(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } item) _module.Queue.Retry(item); }
    private void RemoveItem(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } item) _module.Queue.Remove(item); }
    private void ClearFinished(object sender, RoutedEventArgs e) => _module.Queue.ClearFinished();
    private void OpenItemFile(object sender, RoutedEventArgs e) { if (ItemOf(sender)?.FilePath is { } path) Try(() => MediaModule.OpenFile(path)); }
    private void RevealItemFile(object sender, RoutedEventArgs e) { if (ItemOf(sender)?.FilePath is { } path) Try(() => MediaModule.RevealFile(path)); }

    // ---- history ----

    private void RefreshHistory()
    {
        var entries = _module.History.Entries.Take(HistoryShown).ToArray();
        HistoryList.ItemsSource = entries;
        HistoryCount.Text = entries.Length.ToString();
        HistoryEmpty.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static HistoryEntry? HistoryOf(object sender) => (sender as FrameworkElement)?.DataContext as HistoryEntry;

    private void OpenHistoryFile(object sender, RoutedEventArgs e)
    {
        if (HistoryOf(sender) is not { } entry) return;
        if (entry.FilePath is { } path) Try(() => MediaModule.OpenFile(path));
    }

    private void RevealHistoryFile(object sender, RoutedEventArgs e)
    {
        if (HistoryOf(sender) is not { } entry) return;
        if (entry.FilePath is { } path) Try(() => MediaModule.RevealFile(path));
        else Try(_module.OpenOutputFolder);
    }

    private void ClearHistory(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, "İndirme geçmişi silinsin mi? İndirilen dosyalar silinmez.", "Geçmişi temizle",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _module.History.Clear();
    }

    // ---- settings ----

    private void ClipboardToastsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _module.Settings.ClipboardToasts = ClipboardToastsBox.IsChecked == true;
        _module.SaveSettings();
    }

    private void CookiesChanged(object sender, RoutedEventArgs e)
    {
        BrowserBox.IsEnabled = CookiesBox.IsChecked == true;
        if (_loading) return;
        _module.Settings.UseBrowserCookies = CookiesBox.IsChecked == true;
        _module.SaveSettings();
    }

    private void BrowserChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BrowserBox.SelectedItem is not string browser) return;
        _module.Settings.CookieBrowser = browser;
        _module.SaveSettings();
    }

    private async void CheckUpdate(object sender, RoutedEventArgs e)
    {
        if (!_module.Provisioner.YtDlpInstalled)
        {
            ToolStatus.Text = "yt-dlp henüz kurulu değil; ilk indirmede kurulur.";
            return;
        }
        UpdateButton.IsEnabled = false;
        ToolStatus.Text = "yt-dlp güncellemesi denetleniyor…";
        try
        {
            bool updated = await _module.Provisioner.CheckNowAsync(CancellationToken.None);
            RefreshToolStatus();
            if (!updated) ToolStatus.Text += " · güncel";
        }
        catch (Exception ex) when (ex is ExternalToolException or MediaException or InvalidOperationException)
        {
            ToolStatus.Text = "Denetlenemedi: " + ex.Message;
        }
        finally { UpdateButton.IsEnabled = true; }
    }

    private void RefreshToolStatus()
    {
        try { ToolStatus.Text = _module.Provisioner.StatusText; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ToolStatus.Text = ""; }
    }

    // ---- helpers ----

    private void ShowFetchStatus(string text)
    {
        FetchStatus.Text = text;
        FetchStatus.Visibility = Visibility.Visible;
    }

    private void HideFetchStatus() => FetchStatus.Visibility = Visibility.Collapsed;

    private static void SetText(TextBlock block, string text)
    {
        block.Text = text;
        block.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            _module.Host.ShowToast("Açılamadı", ex.Message, kind: ToastKind.Error);
        }
    }
}
