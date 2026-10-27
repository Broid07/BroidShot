using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BroidShot.Capture;
using BroidShot.Core;
using BroidShot.Editor;
using BroidShot.Services;
using BroidShot.Lifecycle;
using BroidShot.Shell;
using Forms = System.Windows.Forms;

namespace BroidShot;

public partial class MainWindow : Window
{
    private readonly CaptureService _capture = new();
    private readonly HistoryStore _history = new(Path.Combine(DataPaths.Root, "history"));
    private readonly OcrService _ocr = new();
    private readonly UserPreferences _preferences;
    private readonly StartupService _startup;
    private readonly SemaphoreSlim _ocrLock = new(1, 1);
    private IReadOnlyList<HistoryItem> _items = Array.Empty<HistoryItem>();
    private GlobalHotkey? _hotkey;
    private F12CaptureHotkey? _f12Hotkey;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripItem? _trayCaptureItem;
    private string _shortcutText = "F12";
    private HwndSource? _messageSource;
    private CaptureOverlayWindow? _overlay;
    private bool _capturing;
    private bool _exit;
    private bool _ready;
    private bool _sessionInitialized;
    private bool _updatingLifecycleControls;
    private BitmapSource? _lastImage;

    private const string ScreenshotModuleId = "screenshot";
    private const string SettingsPageId = "settings";
    private static readonly (string Gesture, string Label)[] PalettePresets =
    {
        ("Ctrl+Alt+K", "Ctrl + Alt + K"), ("Ctrl+Alt+Space", "Ctrl + Alt + Space"), ("Ctrl+Shift+Space", "Ctrl + Shift + Space"), ("Ctrl+Alt+P", "Ctrl + Alt + P"), ("", "Kapalı")
    };
    private readonly ShellServices _services;
    private readonly List<ModuleSlot> _slots = new();
    private HotkeyRegistration? _paletteHotkey;
    private CommandPaletteWindow? _palette;
    private Forms.ToolStripItem? _trayPaletteItem;
    private string _currentPageId = ScreenshotModuleId;
    private bool _modulesInitialized;
    private bool _modulesShutDown;
    private bool _updatingPaletteControls;

    static MainWindow() => ThemeResources.ApplyDefaults();

    public MainWindow(UserPreferences? preferences = null, StartupService? startup = null, IEnumerable<IToolModule>? modules = null)
    {
        _preferences = preferences ?? UserPreferences.Load();
        _startup = startup ?? new StartupService();
        InitializeComponent();
        VersionLabel.Text = "Sürüm " + VersionText() + " · Tüm işlemler bu bilgisayarda yapılır.";
        SettingsNavButton.ToolTip = RailTooltip("Ayarlar", "Başlangıç, kısayollar ve kayıt tercihleri", "Ctrl + ,");
        KeepHistoryCheck.IsChecked = _preferences.KeepHistory;
        F12EnabledCheck.IsChecked = _preferences.F12Enabled;
        RunInBackgroundCheck.IsChecked = _preferences.RunInBackground;
        StartInTrayCheck.IsChecked = _preferences.StartInTrayOnWindowsStartup;
        FolderBox.Text = _preferences.ExportFolder;
        HotkeyPresetBox.SelectedIndex = Math.Clamp(_preferences.HotkeyPreset, 0, 2);
        _services = new ShellServices(Dispatcher, RevealModule);
        JobList.ItemsSource = _services.Jobs.Jobs;
        ((INotifyCollectionChanged)_services.Jobs.Jobs).CollectionChanged += (_, _) => UpdateJobsIndicator();
        RegisterModules(modules ?? ModuleRegistry.CreateModules());
        BuildSidebar();
        InitializePaletteHotkeyBox();
        NavigateToModule(ScreenshotModuleId);
        PreviewKeyDown += OnShellKeyDown;
        _ready = true;
        SourceInitialized += (_, _) =>
        {
            ConfigureHotkey();
            var handle = new WindowInteropHelper(this).Handle;
            _messageSource = HwndSource.FromHwnd(handle);
            _messageSource?.AddHook(OnWindowMessage);
            ApplyWindowChrome(this);
        };
        Loaded += (_, _) => InitializeSession();
        Activated += (_, _) => RefreshLifecycleSettings();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _exit = true;
            _overlay?.Close();
            if (_messageSource is { IsDisposed: false }) _messageSource.RemoveHook(OnWindowMessage);
            _hotkey?.Dispose(); _f12Hotkey?.Dispose(); DisposeTray();
            _palette?.Close();
            ShutdownModules();
            _paletteHotkey?.Dispose();
            _services.Dispose();
            Application.Current?.Shutdown();
        };
    }

    public void InitializeSession()
    {
        if (_sessionInitialized || _exit) return;
        new WindowInteropHelper(this).EnsureHandle();
        InitializeTray();
        _sessionInitialized = true;
        OcrStatus.Text = _ocr.IsAvailable ? _ocr.LanguageLabel + " · çevrimdışı metin tanıma hazır." : "OCR dil dosyaları bulunamadı. Kurulum paketindeki tessdata klasörünü kontrol et.";
        RefreshLifecycleSettings();
        InitializeModules();
        ConfigurePaletteHotkey();
        _ = RefreshHistoryAsync();
    }

    private void InitializeTray()
    {
        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/BroidShot;component/Branding/BroidShot.ico"))!.Stream;
        using var icon = new System.Drawing.Icon(iconStream);
        var menu = new Forms.ContextMenuStrip();
        _trayCaptureItem = menu.Items.Add("Alan seç  ·  " + _shortcutText, null, (_, _) => Dispatcher.Invoke(() => _ = BeginCaptureAsync(CaptureMode.Region)));
        _trayPaletteItem = menu.Items.Add("Araç ara  ·  " + PaletteHintLabel.Text, null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowCommandPalette)));
        menu.Items.Add("Alıntılarımı aç", null, (_, _) => Dispatcher.Invoke(Reveal));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _tray = new Forms.NotifyIcon { Text = "BroidShot · " + _shortcutText, Icon = (System.Drawing.Icon)icon.Clone(), Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(Reveal);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exit) return;
        if (!_preferences.RunInBackground)
        {
            _exit = true;
            return;
        }
        e.Cancel = true;
        Hide();
    }

    private void ExitApplication()
    {
        _exit = true;
        _overlay?.Close();
        _hotkey?.Dispose();
        _f12Hotkey?.Dispose();
        DisposeTray();
        Close();
    }

    private void DisposeTray()
    {
        if (_tray is null) return;
        _tray.Visible = false;
        var icon = _tray.Icon;
        var menu = _tray.ContextMenuStrip;
        _tray.Dispose();
        icon?.Dispose();
        menu?.Dispose();
        _tray = null;
        _trayCaptureItem = null;
        _trayPaletteItem = null;
    }

    private void Reveal()
    {
        if (_overlay != null) { _overlay.Activate(); return; }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal void RevealAfterPreparation() => Reveal();

    private IntPtr OnWindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message != WindowActivation.RestoreMessage) return IntPtr.Zero;
        handled = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Application.Current is App application) _ = application.ShowLauncherForExistingAsync(this);
            else Reveal();
        }));
        return IntPtr.Zero;
    }

    private static void ApplyWindowChrome(Window window) => DarkWindowChrome.Apply(window);

    /// <summary>Ctrl+K palette, Ctrl+1…9 rail entries, Ctrl+, settings.</summary>
    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.K) { e.Handled = true; ShowCommandPalette(null, anchorToWindow: true); return; }
        if (key == Key.OemComma) { e.Handled = true; NavigateToModule(SettingsPageId); return; }
        int index = key is >= Key.D1 and <= Key.D9 ? key - Key.D1 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad1 : -1;
        if (index >= 0 && index < _slots.Count)
        {
            e.Handled = true;
            NavigateToModule(_slots[index].Id);
            _slots[index].NavButton?.Focus();
        }
    }

    private static string VersionText()
    {
        Version? version = typeof(MainWindow).Assembly.GetName().Version;
        return version is null ? "0.3" : $"{version.Major}.{version.Minor} alfa";
    }

    /// <summary>Two-line tooltip used by the rail: name, description and optional shortcut.</summary>
    private static object RailTooltip(string title, string description, string? shortcut)
    {
        var panel = new StackPanel { MaxWidth = 300 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0)
        {
            var text = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            panel.Children.Add(text);
        }
        if (!string.IsNullOrEmpty(shortcut))
        {
            var keys = new TextBlock { Text = shortcut, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
            keys.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            panel.Children.Add(keys);
        }
        return panel;
    }

    private void UpdateJobsIndicator()
    {
        int count = _services.Jobs.Jobs.Count;
        JobsPanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        JobsCount.Text = count > 9 ? "9+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        JobsTooltip.Text = count == 1 ? "1 arka plan işi sürüyor" : count + " arka plan işi sürüyor";
        if (count == 0) JobsPopup.IsOpen = false;
    }

    private void ToggleJobs(object sender, RoutedEventArgs e) => JobsPopup.IsOpen = !JobsPopup.IsOpen;

    private enum CaptureMode { Region, Window, Monitor, All }
    private async void CaptureRegion(object sender, RoutedEventArgs e) => await BeginCaptureAsync(CaptureMode.Region);
    private async void CaptureWindow(object sender, RoutedEventArgs e) => await BeginCaptureAsync(CaptureMode.Window);
    private async void CaptureMonitor(object sender, RoutedEventArgs e) => await BeginCaptureAsync(CaptureMode.Monitor);
    private async void CaptureAll(object sender, RoutedEventArgs e) => await BeginCaptureAsync(CaptureMode.All);

    private async Task BeginCaptureAsync(CaptureMode mode)
    {
        if (_exit || _capturing || _overlay != null) { _overlay?.Activate(); return; }
        _capturing = true;
        bool restore = IsVisible && WindowState != WindowState.Minimized;
        try
        {
            PixelRect? selection = null;
            IntPtr selectedHandle = IntPtr.Zero;
            if (mode == CaptureMode.Window)
            {
                CapturableWindow? selected = ChooseWindow();
                if (selected == null) return;
                selectedHandle = selected.Handle;
                selection = _capture.GetWindowBounds(selected.Handle);
                if (selection == null) throw new InvalidOperationException("Seçilen pencere artık yakalanamıyor.");
            }
            else if (mode == CaptureMode.Monitor) selection = _capture.GetCursorMonitorBounds();
            Hide();
            await Task.Delay(180);
            if (_exit) return;
            CaptureSnapshot snapshot = await Task.Run(() => selectedHandle != IntPtr.Zero
                ? _capture.CaptureWindow(selectedHandle) : _capture.CaptureDesktop());
            if (_exit) return;
            if (mode == CaptureMode.Window)
            {
                selection = snapshot.Bounds;
                snapshot = WindowCaptureCanvas.Place(snapshot);
            }
            if (mode == CaptureMode.All) selection = snapshot.Bounds;
            OpenOverlay(snapshot, selection, restore);
        }
        catch (Exception ex)
        {
            _overlay?.Close(); _overlay = null;
            if (restore && !_exit) Reveal();
            if (!_exit) Report("Ekran alınamadı", ex);
        }
        finally { _capturing = false; }
    }

    private CapturableWindow? ChooseWindow()
    {
        var windows = _capture.GetWindows();
        var dialog = CreateDialog("Yakalanacak pencere", 560, 480);
        dialog.Owner = this;
        dialog.ResizeMode = ResizeMode.NoResize;
        var panel = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        var instructions = new TextBlock { Text = "Görünür bir pencere seç", Margin = new Thickness(0, 0, 0, 4) };
        instructions.SetResourceReference(StyleProperty, "SubtitleText");
        DockPanel.SetDock(instructions, Dock.Top); panel.Children.Add(instructions);
        var hint = new TextBlock { Text = "Çift tıklayarak da yakalayabilirsin.", Margin = new Thickness(0, 0, 0, 14) };
        hint.SetResourceReference(StyleProperty, "MutedText");
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Vazgeç", IsCancel = true };
        var button = new Button { Content = "Pencereyi yakala", IsDefault = true, Margin = new Thickness(0) };
        button.SetResourceReference(StyleProperty, "PrimaryButton");
        buttons.Children.Add(cancel); buttons.Children.Add(button);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var list = new ListBox { ItemsSource = windows, DisplayMemberPath = "Title", Padding = new Thickness(4), BorderThickness = new Thickness(1) };
        list.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        list.SetResourceReference(BorderBrushProperty, "StrokeSubtleBrush");
        list.Loaded += (_, _) => { if (list.Items.Count > 0) { list.SelectedIndex = 0; list.Focus(); } };
        panel.Children.Add(list);
        CapturableWindow? result = null;
        void Accept() { if (list.SelectedItem is CapturableWindow item) { result = item; dialog.DialogResult = true; } }
        button.Click += (_, _) => Accept();
        list.MouseDoubleClick += (_, _) => Accept();
        dialog.Content = panel;
        dialog.ShowDialog();
        return result;
    }

    private void OpenOverlay(CaptureSnapshot snapshot, PixelRect? selection, bool restore)
    {
        var overlay = new CaptureOverlayWindow(snapshot, selection);
        _overlay = overlay;
        overlay.SourceInitialized += (_, _) => CaptureService.PositionOverlay(overlay, snapshot.Bounds);
        overlay.Completed += image => _ = FinishCaptureAsync(image);
        overlay.SaveRequested += image => SaveImage(image, overlay);
        overlay.Closed += (_, _) =>
        {
            _overlay = null;
            if (restore && !_exit) Reveal();
        };
        overlay.Show();
        overlay.Activate();
    }

    private async Task FinishCaptureAsync(BitmapSource image)
    {
        _lastImage = image;
        string copyResult;
        string historyResult = "Geçmiş kapalı";
        try { await ClipboardService.CopyImageAsync(image); copyResult = "Panoya kopyalandı"; }
        catch (Exception ex) { copyResult = "Pano başarısız: " + ex.Message; }
        if (_preferences.KeepHistory)
        {
            try { await _history.AddAsync(image); historyResult = "geçmişe eklendi"; }
            catch (Exception ex) { historyResult = "geçmiş kaydı başarısız: " + ex.Message; }
        }
        StatusLabel.Text = copyResult + " · " + historyResult;
        await RefreshHistoryAsync();
        if (!IsVisible) _tray?.ShowBalloonTip(2200, "BroidShot", StatusLabel.Text, Forms.ToolTipIcon.Info);
    }

    private async Task RefreshHistoryAsync()
    {
        try { _items = await _history.LoadAsync(); FilterHistory(); }
        catch (Exception ex) { StatusLabel.Text = "Geçmiş açılamadı: " + ex.Message; }
    }

    private void FilterHistory()
    {
        string query = SearchBox.Text.Trim();
        var filtered = _items.Where(item => query.Length == 0 || (item.OcrText ?? "").Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        HistoryList.ItemsSource = filtered;
        CountLabel.Text = $"{filtered.Length} alıntı";
        EmptyState.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = query.Length > 0 ? "Bu aramada alıntı bulunamadı." : "İlk alıntın burada yerini bekliyor.";
        EmptyText.Text = query.Length > 0 ? "Başka bir tarih veya alıntıdaki bir kelimeyi dene." : "Alanını seç, düzenle ve kopyala. Geçmişin burada kalsın.";
        EmptyCaptureButton.Visibility = query.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelectionLabel();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) { if (_ready) FilterHistory(); }
    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionLabel();
    private void UpdateSelectionLabel() =>
        SelectionLabel.Text = Selected is { } item ? item.DisplayName + " · " + item.Dimensions : "Bir alıntı seç";
    private void HistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(HistoryList, source) is ListBoxItem)
            EditSelected(sender, e);
    }
    private HistoryItem? Selected => HistoryList.SelectedItem as HistoryItem;
    private BitmapSource? SelectedImage()
    {
        try { return Selected is { } item ? _history.LoadImage(item) : _lastImage; }
        catch (Exception ex) { Report("Görüntü açılamadı", ex); return null; }
    }

    private async void CopySelected(object sender, RoutedEventArgs e)
    {
        if (SelectedImage() is not { } image) { StatusLabel.Text = "Önce bir alıntı seç."; return; }
        try { await ClipboardService.CopyImageAsync(image); StatusLabel.Text = "Seçili alıntı panoya kopyalandı."; }
        catch (Exception ex) { Report("Panoya kopyalanamadı", ex); }
    }

    private void EditSelected(object sender, RoutedEventArgs e)
    {
        if (_overlay != null || SelectedImage() is not { } image) return;
        // Editing a saved capture operates on its flattened pixels, without recovering redacted data.
        var bounds = new PixelRect(0, 0, image.PixelWidth, image.PixelHeight);
        var snapshot = new CaptureSnapshot(image, bounds, Array.Empty<MonitorInfo>());
        var overlay = new CaptureOverlayWindow(snapshot, bounds) { WindowState = WindowState.Maximized };
        _overlay = overlay;
        overlay.Completed += output => _ = FinishCaptureAsync(output);
        overlay.SaveRequested += output => SaveImage(output, overlay);
        overlay.Closed += (_, _) => { _overlay = null; if (!_exit) Reveal(); };
        Hide();
        overlay.Show();
    }

    private void SaveSelected(object sender, RoutedEventArgs e) { if (SelectedImage() is { } image) SaveImage(image, this); }
    private void SaveImage(BitmapSource image, Window owner)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Alıntıyı farklı kaydet", Filter = "PNG görüntüsü|*.png|JPEG görüntüsü|*.jpg",
                FileName = $"BroidShot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png", DefaultExt = ".png",
                InitialDirectory = Directory.Exists(_preferences.ExportFolder) ? _preferences.ExportFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AddExtension = true, OverwritePrompt = true
            };
            if (dialog.ShowDialog(owner) != true) return;
            ImageExporter.Save(image, dialog.FileName);
            _preferences.ExportFolder = Path.GetDirectoryName(dialog.FileName)!;
            _preferences.Save(); FolderBox.Text = _preferences.ExportFolder;
            StatusLabel.Text = "Kaydedildi: " + dialog.FileName;
        }
        catch (Exception ex) { Report("Dosya kaydedilemedi", ex, owner); }
    }

    private async void OcrSelected(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (SelectedImage() is { } image) await RecognizeAsync(image, item, this);
    }

    private async Task RecognizeAsync(BitmapSource image, HistoryItem? item, Window owner)
    {
        if (!await _ocrLock.WaitAsync(0)) { StatusLabel.Text = "Bir metin tanıma işlemi zaten sürüyor."; return; }
        try
        {
            StatusLabel.Text = _ocr.LanguageLabel + " metin tanınıyor…";
            string text = await _ocr.RecognizeAsync(image);
            if (_exit) return;
            if (item != null) { await _history.UpdateOcrAsync(item, text); await RefreshHistoryAsync(); }
            StatusLabel.Text = text.Length > 0 ? "Metin tanıma tamamlandı." : "Bu görüntüde metin bulunamadı.";
            if (!owner.IsLoaded) owner = this;
            ShowOcrText(text, owner);
        }
        catch (Exception ex) { if (!_exit) Report("Metin çıkarılamadı", ex, owner); }
        finally { _ocrLock.Release(); }
    }

    private void ShowOcrText(string text, Window owner)
    {
        var dialog = CreateDialog("Çıkarılan metin · BroidShot", 680, 480);
        dialog.Owner = owner;
        dialog.Topmost = owner.Topmost;
        var panel = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        var heading = new TextBlock { Text = "Çıkarılan metin", Margin = new Thickness(0, 0, 0, 14) };
        heading.SetResourceReference(StyleProperty, "SubtitleText");
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var copy = new Button { Content = "Metni kopyala", Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        copy.SetResourceReference(StyleProperty, "PrimaryButton");
        Core.ThemeAssist.SetIcon(copy, ((char)0xE8C8).ToString());
        DockPanel.SetDock(copy, Dock.Bottom); panel.Children.Add(copy);
        var box = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 15,
            VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(12, 10, 12, 10) };
        panel.Children.Add(box);
        copy.Click += (_, _) => { try { Clipboard.SetText(box.Text); copy.Content = "Kopyalandı"; } catch (Exception ex) { Report("Metin kopyalanamadı", ex, dialog); } };
        dialog.Content = panel; dialog.ShowDialog();
    }

    /// <summary>Small themed dialog window (dark frame, theme background).</summary>
    private static Window CreateDialog(string title, double width, double height)
    {
        var dialog = new Window { Title = title, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        dialog.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        dialog.SetResourceReference(ForegroundProperty, "TextBrush");
        dialog.SetResourceReference(FontFamilyProperty, "UiFont");
        dialog.FontSize = 14;
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
        dialog.UseLayoutRounding = true;
        dialog.SourceInitialized += (_, _) => ApplyWindowChrome(dialog);
        return dialog;
    }

    private async void DeleteSelected(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        try { await _history.DeleteAsync(item); await RefreshHistoryAsync(); StatusLabel.Text = "Alıntı yerel geçmişten silindi."; }
        catch (Exception ex) { Report("Alıntı silinemedi", ex); }
    }

    private void ShowSettings(object sender, RoutedEventArgs e) => NavigateToModule(SettingsPageId);
    private void RefreshLifecycleSettings()
    {
        _updatingLifecycleControls = true;
        try
        {
            bool enabled = _startup.IsEnabled;
            StartWithWindowsCheck.IsChecked = enabled;
            StartWithWindowsCheck.IsEnabled = true;
            StartupStatusLabel.Text = enabled ? "Başlangıç kaydı açık. Windows'un Başlangıç uygulamaları ayarlarından da yönetebilirsin." : "Windows başlangıcı kapalı.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StartWithWindowsCheck.IsChecked = false;
            StartWithWindowsCheck.IsEnabled = false;
            StartupStatusLabel.Text = "Windows başlangıç ayarı okunamadı: " + ex.Message;
        }
        finally { _updatingLifecycleControls = false; }
        StartInTrayCheck.IsEnabled = _preferences.RunInBackground && StartWithWindowsCheck.IsChecked == true;
        BackgroundStatusLabel.Text = _preferences.RunInBackground
            ? "Pencereyi kapatınca sistem tepsisinde çalışır; F12 ve kısayollar hazır kalır. Tam çıkış için tepsi menüsünü kullan."
            : "Pencereyi kapatınca uygulama tamamen kapanır; F12 ve diğer kısayollar durur.";
    }
    private void RunInBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updatingLifecycleControls) return;
        bool previous = _preferences.RunInBackground;
        try
        {
            _preferences.RunInBackground = RunInBackgroundCheck.IsChecked == true;
            _preferences.Save();
            RefreshLifecycleSettings();
            StatusLabel.Text = _preferences.RunInBackground ? "Arka planda çalışma açık." : "Arka planda çalışma kapalı. Pencereyi kapattığında uygulama kapanacak.";
        }
        catch (Exception ex)
        {
            _preferences.RunInBackground = previous;
            _updatingLifecycleControls = true;
            RunInBackgroundCheck.IsChecked = previous;
            _updatingLifecycleControls = false;
            Report("Arka plan ayarı kaydedilemedi", ex);
        }
    }
    private void StartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updatingLifecycleControls) return;
        try
        {
            bool enabled = StartWithWindowsCheck.IsChecked == true;
            _startup.SetEnabled(enabled);
            RefreshLifecycleSettings();
            StatusLabel.Text = enabled ? "Windows'ta oturum açtığında BroidShot başlayacak." : "Windows ile otomatik başlatma kapatıldı.";
        }
        catch (Exception ex) { RefreshLifecycleSettings(); Report("Windows başlangıcı değiştirilemedi", ex); }
    }
    private void StartInTrayChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updatingLifecycleControls) return;
        bool previous = _preferences.StartInTrayOnWindowsStartup;
        try
        {
            _preferences.StartInTrayOnWindowsStartup = StartInTrayCheck.IsChecked == true;
            _preferences.Save();
        }
        catch (Exception ex)
        {
            _preferences.StartInTrayOnWindowsStartup = previous;
            _updatingLifecycleControls = true;
            StartInTrayCheck.IsChecked = previous;
            _updatingLifecycleControls = false;
            Report("Başlangıç görünümü kaydedilemedi", ex);
        }
    }
    private void ConfigureHotkey()
    {
        _hotkey?.Dispose();
        int index = Math.Clamp(_preferences.HotkeyPreset, 0, 2);
        uint modifiers = index == 1 ? 0x0003u : 0x0006u;
        uint key = index == 2 ? 0x78u : 0x53u;
        string label = new[] { "Ctrl + Shift + S", "Ctrl + Alt + S", "Ctrl + Shift + F9" }[index];
        _hotkey = new GlobalHotkey(this, () => _ = BeginCaptureAsync(CaptureMode.Region), modifiers, key);
        bool registered = _hotkey.Register();
        _f12Hotkey?.Dispose();
        _f12Hotkey = null;
        bool f12Registered = false;
        if (_preferences.F12Enabled)
        {
            _f12Hotkey = new F12CaptureHotkey(this, () => _ = BeginCaptureAsync(CaptureMode.Region));
            f12Registered = _f12Hotkey.Register();
        }
        _shortcutText = f12Registered ? "F12" : registered ? label : "Yeni alıntı";
        HotkeyLabel.Text = f12Registered || registered ? _shortcutText : "Kısayol yok";
        RegionShortcutLabel.Text = f12Registered || registered ? _shortcutText : "—";
        NewCaptureButton.ToolTip = f12Registered || registered ? "Alan seç (" + _shortcutText + ")" : "Alan seç";
        HotkeyStatus.Text = f12Registered
            ? (registered ? "Ek kısayol: " + label : "F12 hazır. Ek kısayol başka bir uygulamada kullanımda.")
            : registered ? (_preferences.F12Enabled ? "F12 başlatılamadı. Ek kısayol hazır." : "Alan seçimini başlatır")
            : "Kısayol başlatılamadı. Yeni alıntı düğmesini kullan.";
        StatusLabel.Text = f12Registered || registered ? "Hazır · Alan seçmek için " + _shortcutText : "Kısayol kullanılamıyor · Yeni alıntı düğmesini kullanabilirsin.";
        if (_tray != null) _tray.Text = "BroidShot · " + _shortcutText;
        if (_trayCaptureItem != null) _trayCaptureItem.Text = "Alan seç  ·  " + _shortcutText;
    }
    private void F12SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _preferences.F12Enabled = F12EnabledCheck.IsChecked == true;
        try { _preferences.Save(); ConfigureHotkey(); } catch (Exception ex) { Report("F12 ayarı kaydedilemedi", ex); }
    }
    private void HotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || sender is not ComboBox box) return;
        _preferences.HotkeyPreset = box.SelectedIndex;
        try { _preferences.Save(); ConfigureHotkey(); } catch (Exception ex) { Report("Kısayol kaydedilemedi", ex); }
    }
    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        try { _preferences.KeepHistory = KeepHistoryCheck.IsChecked == true; _preferences.Save(); }
        catch (Exception ex) { Report("Ayar kaydedilemedi", ex); }
    }
    private void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Kayıt klasörü seç", InitialDirectory = _preferences.ExportFolder };
        if (dialog.ShowDialog(this) != true) return;
        try { _preferences.ExportFolder = dialog.FolderName; _preferences.Save(); FolderBox.Text = dialog.FolderName; }
        catch (Exception ex) { Report("Klasör ayarı kaydedilemedi", ex); }
    }
    // ===== Tool modules, navigation and command palette =====

    /// <summary>Id of the visible page: a module id or "settings".</summary>
    public string CurrentPageId => _currentPageId;
    /// <summary>The visible page element (module view, error page, screenshot page or settings).</summary>
    public FrameworkElement? CurrentPage { get; private set; }
    /// <summary>Notifications shown through IToolHost.ShowToast.</summary>
    public ToastPresenter Toasts => _services.Toasts;
    /// <summary>The background job list shown in the sidebar.</summary>
    public IJobTracker Jobs => _services.Jobs;
    /// <summary>Message bus shared by all modules.</summary>
    public IMessageBus Messages => _services.Messages;
    /// <summary>The open command palette, if any.</summary>
    public CommandPaletteWindow? Palette => _palette;

    private IToolModule CreateScreenshotModule() => new BuiltInModule(
        ScreenshotModuleId, "Ekran alıntısı", "Alan, pencere veya ekran yakala; geçmişten kopyala ve düzenle.", ToolCategories.Capture, ShellIcons.Screenshot,
        new[] { "ekran görüntüsü", "screenshot", "alıntı", "yakala", "capture", "geçmiş", "history", "ocr" },
        () => ScreenshotPage, ScreenshotCommands, headerTitle: "Alıntıların, elinin altında.");

    private IReadOnlyList<ToolCommand> ScreenshotCommands() => new[]
    {
        new ToolCommand("Alan seç", () => _ = BeginCaptureAsync(CaptureMode.Region))
            { Id = "screenshot.region", Description = "Ekranda bir bölge seçip düzenle · " + _shortcutText, Keywords = new[] { "ekran görüntüsü", "screenshot", "bölge", "kırp", "snip", "f12" } },
        new ToolCommand("Pencere yakala", () => { Reveal(); _ = BeginCaptureAsync(CaptureMode.Window); })
            { Id = "screenshot.window", Description = "Tek bir uygulama penceresini yakala", Keywords = new[] { "ekran görüntüsü", "window", "uygulama" } },
        new ToolCommand("Monitörü yakala", () => _ = BeginCaptureAsync(CaptureMode.Monitor))
            { Id = "screenshot.monitor", Description = "İmlecin olduğu ekranı yakala", Keywords = new[] { "ekran görüntüsü", "monitor", "display" } },
        new ToolCommand("Tüm ekranları yakala", () => _ = BeginCaptureAsync(CaptureMode.All))
            { Id = "screenshot.all", Description = "Masaüstünün tamamını yakala", Keywords = new[] { "ekran görüntüsü", "full screen", "masaüstü" } },
        ToolCommand.Open("Alıntılarımı aç", ScreenshotModuleId, "Geçmişteki alıntıları göster", new[] { "geçmiş", "history", "son alıntılar" }, "screenshot.history"),
    };

    private void RegisterModules(IEnumerable<IToolModule> modules)
    {
        AddSlot(CreateScreenshotModule(), builtIn: true);
        foreach (IToolModule module in modules) AddSlot(module, builtIn: false);
    }

    private void AddSlot(IToolModule module, bool builtIn)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(module);
            var slot = new ModuleSlot(module, builtIn) { Initialized = builtIn };
            if (!Core.DataPaths.IsValidId(slot.Id)) throw new InvalidOperationException($"Geçersiz modül kimliği: '{slot.Id}'.");
            if (slot.Id == SettingsPageId || _slots.Any(s => s.Id == slot.Id)) throw new InvalidOperationException($"'{slot.Id}' kimliği birden fazla kez kullanılıyor.");
            if (string.IsNullOrWhiteSpace(slot.Title)) throw new InvalidOperationException($"'{slot.Id}' modülünün başlığı boş.");
            _slots.Add(slot);
        }
        catch (Exception ex) { ErrorLog.Write("modules", "Modül kaydedilemedi: " + module?.GetType().FullName, ex); }
    }

    private void InitializeModules()
    {
        if (_modulesInitialized) return;
        _modulesInitialized = true;
        foreach (ModuleSlot slot in _slots.Where(s => !s.IsBuiltIn)) InitializeModule(slot);
        RefreshModuleStatus();
    }

    private void InitializeModule(ModuleSlot slot)
    {
        if (slot.Initialized || slot.Error is not null || _exit) return;
        ModuleHost host;
        try
        {
            host = slot.Host ??= new ModuleHost(slot.Id, _services);
            slot.Module.Initialize(host);
            slot.Initialized = true;
        }
        catch (Exception ex)
        {
            slot.Error = ex.Message.Length > 0 ? ex.Message : ex.GetType().Name;
            ErrorLog.Write(slot.Id, "Modül başlatılamadı", ex);
            MarkFailed(slot);
            return;
        }
        try
        {
            foreach (ToolHotkey hotkey in slot.Module.Hotkeys ?? Array.Empty<ToolHotkey>())
            {
                HotkeyRegistration registration = host.RegisterHotkey(hotkey.DefaultGesture, hotkey.Callback);
                slot.HotkeyNotes.Add(registration.IsRegistered
                    ? hotkey.Title + ": " + registration.Gesture.DisplayText
                    : hotkey.Title + ": " + registration.Error);
            }
        }
        catch (Exception ex) { ErrorLog.Write(slot.Id, "Modül kısayolları kaydedilemedi", ex); }
    }

    private void ShutdownModules()
    {
        if (_modulesShutDown) return;
        _modulesShutDown = true;
        foreach (ModuleSlot slot in Enumerable.Reverse(_slots))
        {
            slot.Host?.ReleaseHotkeys();
            if (slot.IsBuiltIn || !slot.Initialized) continue;
            try { slot.Module.Shutdown(); }
            catch (Exception ex) { ErrorLog.Write(slot.Id, "Modül kapatılırken hata oluştu", ex); }
        }
    }

    private void MarkFailed(ModuleSlot slot)
    {
        if (slot.NavButton is null) return;
        slot.NavButton.ToolTip = RailTooltip(slot.Title, "Başlatılamadı: " + slot.Error, null);
        slot.NavButton.Opacity = 0.5;
    }

    /// <summary>
    /// Fills the icon rail: one glyph button per module, in registry order, with a thin separator
    /// between categories. Every group starts with a separator element tagged "rail-group" whose
    /// automation name is the category (the first one is not drawn).
    /// </summary>
    private void BuildSidebar()
    {
        ModuleNav.Children.Clear();
        bool first = true;
        int index = 0;
        foreach (IGrouping<string, ModuleSlot> group in _slots.GroupBy(s => s.Category.Length > 0 ? s.Category : "Diğer"))
        {
            var separator = new Border { Tag = "rail-group", Focusable = false, Visibility = first ? Visibility.Collapsed : Visibility.Visible, ToolTip = group.Key };
            separator.SetResourceReference(StyleProperty, "RailGroupSeparator");
            AutomationProperties.SetName(separator, group.Key);
            ModuleNav.Children.Add(separator);
            first = false;
            foreach (ModuleSlot slot in group)
            {
                string id = slot.Id;
                index++;
                Button button = CreateNavButton(ShellIcons.Resolve(slot.Icon, slot.Id), slot.Title, slot.Category, slot.Description, index <= 9 ? "Ctrl + " + index : null);
                button.Click += (_, _) => NavigateToModule(id);
                slot.NavButton = button;
                if (slot.Error is not null) MarkFailed(slot);
                ModuleNav.Children.Add(button);
            }
        }
    }

    private static Button CreateNavButton(string glyph, string title, string category, string description, string? shortcut)
    {
        var button = new Button { Content = glyph, ToolTip = RailTooltip(title, description, shortcut) };
        button.SetResourceReference(StyleProperty, "RailButton");
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetHelpText(button, category.Length > 0 ? category + " · " + description : description);
        return button;
    }

    /// <summary>Shows a module page (or "settings") inside the window without revealing it. Returns false for an unknown id.</summary>
    public bool NavigateToModule(string id)
    {
        if (id == SettingsPageId)
        {
            ShowPage(SettingsPanel, SettingsNavButton, "Ayarlar", "Başlangıç, kısayollar ve kayıt tercihlerin.", ShellIcons.Settings, id);
            RefreshLifecycleSettings();
            RefreshModuleStatus();
            return true;
        }
        ModuleSlot? slot = _slots.FirstOrDefault(s => s.Id == id);
        if (slot is null) return false;
        FrameworkElement view = EnsureView(slot);
        ShowPage(view, slot.NavButton, slot.Title, slot.Description, ShellIcons.Resolve(slot.Icon, slot.Id), id);
        return true;
    }

    private void RevealModule(string id)
    {
        if (_exit) return;
        Reveal();
        if (!NavigateToModule(id)) ErrorLog.Write("shell", "Bilinmeyen modül sayfası istendi: " + id);
    }

    private FrameworkElement EnsureView(ModuleSlot slot)
    {
        if (slot.View is not null) return slot.View;
        if (!slot.Initialized && slot.Error is null) InitializeModule(slot);
        FrameworkElement view;
        if (slot.Error is not null) view = CreateErrorPage(slot);
        else
        {
            try
            {
                view = slot.Module.CreateView() ?? throw new InvalidOperationException("Modül sayfası oluşturulamadı (boş sonuç).");
                if (view.Parent is not null && !ReferenceEquals(view.Parent, PageHost))
                    throw new InvalidOperationException("Modül sayfası başka bir öğeye bağlı; CreateView her seferinde yeni ve bağımsız bir öğe döndürmeli.");
            }
            catch (Exception ex)
            {
                slot.Error = ex.Message.Length > 0 ? ex.Message : ex.GetType().Name;
                ErrorLog.Write(slot.Id, "Modül sayfası oluşturulamadı", ex);
                MarkFailed(slot);
                view = CreateErrorPage(slot);
            }
        }
        if (view.Parent is null) PageHost.Children.Add(CreatePageContainer(view));
        slot.View = view;
        return view;
    }

    /// <summary>
    /// Wraps a module view with the standard page padding. Views whose content is a plain vertical
    /// stack (StackPanel/WrapPanel) cannot scroll by themselves, so they also get a ScrollViewer;
    /// views that manage their own layout (Grid, DockPanel, ScrollViewer…) fill the page.
    /// </summary>
    private FrameworkElement CreatePageContainer(FrameworkElement view)
    {
        var padded = new Border { Child = view, Padding = TryFindResource("PagePadding") is Thickness padding ? padding : new Thickness(32, 24, 32, 28) };
        if (!NeedsScrollHost(view)) return padded;
        return new ScrollViewer
        {
            Content = padded,
            Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
    }

    private static bool NeedsScrollHost(FrameworkElement view)
    {
        if (view is ScrollViewer) return false;
        object? content = view is ContentControl control ? control.Content : view;
        return content is StackPanel || content is WrapPanel;
    }

    private FrameworkElement CreateErrorPage(ModuleSlot slot)
    {
        var panel = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        var bar = new Border { Margin = new Thickness(0, 0, 0, 16) };
        bar.SetResourceReference(StyleProperty, "ErrorBar");
        var barContent = new DockPanel();
        var glyph = new TextBlock { Text = ShellIcons.ErrorBadge, FontSize = 16, Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        glyph.SetResourceReference(StyleProperty, "IconText");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        DockPanel.SetDock(glyph, Dock.Left);
        barContent.Children.Add(glyph);
        var texts = new StackPanel();
        var title = new TextBlock { Text = "Bu araç başlatılamadı", FontWeight = FontWeights.SemiBold };
        var message = new TextBlock { Text = slot.Error ?? "", Margin = new Thickness(0, 4, 0, 0) };
        message.SetResourceReference(StyleProperty, "MutedText");
        texts.Children.Add(title);
        texts.Children.Add(message);
        barContent.Children.Add(texts);
        bar.Child = barContent;
        var hint = new TextBlock { Text = "Diğer araçlar çalışmaya devam ediyor. Ayrıntılar hata günlüğünde: " + Core.DataPaths.ErrorLog, FontSize = 13 };
        hint.SetResourceReference(StyleProperty, "MutedText");
        panel.Children.Add(bar);
        panel.Children.Add(hint);
        var page = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Tag = "module-error" };
        AutomationProperties.SetName(page, slot.Title + " · hata");
        return page;
    }

    private void ShowPage(FrameworkElement page, Button? navButton, string title, string subtitle, string glyph, string id)
    {
        FrameworkElement container = page;
        while (container.Parent is FrameworkElement parent && !ReferenceEquals(parent, PageHost)) container = parent;
        foreach (UIElement child in PageHost.Children) child.Visibility = ReferenceEquals(child, container) ? Visibility.Visible : Visibility.Collapsed;
        foreach (ModuleSlot slot in _slots) if (slot.NavButton is not null) slot.NavButton.Tag = null;
        SettingsNavButton.Tag = null;
        if (navButton is not null) navButton.Tag = "selected";
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
        PageSubtitle.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PageGlyph.Text = glyph;
        Title = id == ScreenshotModuleId ? "BroidShot" : title + " · BroidShot";
        _currentPageId = id;
        CurrentPage = page;
    }

    private IReadOnlyList<ToolCommand> SafeCommands(ModuleSlot slot)
    {
        try { return slot.Module.Commands ?? Array.Empty<ToolCommand>(); }
        catch (Exception ex) { ErrorLog.Write(slot.Id, "Komutlar okunamadı", ex); return Array.Empty<ToolCommand>(); }
    }

    /// <summary>Palette results for a query: module pages, module commands and Settings, best match first.</summary>
    public IReadOnlyList<PaletteEntry> SearchPalette(string query)
    {
        var results = new List<PaletteEntry>();
        int order = 0;
        void Add(PaletteEntry entry, IEnumerable<string> keywords)
        {
            entry.Score = SearchMatcher.Score(query ?? "", entry.Title, keywords, entry.Subtitle);
            entry.Order = order++;
            if (entry.Score > 0) results.Add(entry);
        }
        foreach (ModuleSlot slot in _slots)
        {
            string id = slot.Id;
            Add(new PaletteEntry(slot.Title, slot.Description, ShellIcons.Resolve(slot.Icon, slot.Id), slot.Category, true, () => { RevealModule(id); return Task.CompletedTask; }),
                slot.Keywords.Append(slot.Category));
            if (slot.Error is not null || !slot.Initialized) continue;
            foreach (ToolCommand command in SafeCommands(slot))
            {
                if (command is null) continue;
                ToolCommand captured = command;
                Add(new PaletteEntry(command.Title, command.Description ?? "", command.Icon is { Length: > 0 } commandIcon ? ShellIcons.Resolve(commandIcon) : ShellIcons.Resolve(slot.Icon, slot.Id), slot.Title, false, () => RunCommandAsync(captured)),
                    (command.Keywords ?? Array.Empty<string>()).Concat(slot.Keywords).Append(slot.Title));
            }
        }
        Add(new PaletteEntry("Ayarlar", "Başlangıç, kısayollar ve kayıt tercihleri", ShellIcons.Settings, "BroidShot", true, () => { RevealModule(SettingsPageId); return Task.CompletedTask; }),
            new[] { "ayar", "settings", "tercih", "kısayol", "başlangıç", "options" });
        return results.OrderByDescending(e => e.Score).ThenBy(e => e.Order).Take(60).ToList();
    }

    private async Task RunCommandAsync(ToolCommand command)
    {
        if (command.NavigateToModuleId is { } target) RevealModule(target);
        if (command.Execute is { } execute) await execute(_services.Lifetime);
    }

    /// <summary>Runs a palette entry; failures are logged and shown as an error toast.</summary>
    public async Task RunPaletteEntryAsync(PaletteEntry entry)
    {
        try { await entry.Run(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLog.Write("palette", "Komut çalıştırılamadı: " + entry.Title, ex);
            if (!_exit) _services.Toasts.Show(new ToastOptions("Komut çalıştırılamadı", entry.Title + ": " + ex.Message) { Kind = ToastKind.Error });
        }
    }

    private void ExecutePaletteEntry(PaletteEntry entry) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = RunPaletteEntryAsync(entry)));

    /// <summary>Opens the command palette, or closes it when it is already open.</summary>
    public void ShowCommandPalette() => ShowCommandPalette(null, anchorToWindow: false);

    /// <summary>
    /// Opens the palette with <paramref name="query"/> typed in. When <paramref name="anchorToWindow"/> is set and the
    /// main window is on screen, the palette drops down over the top bar search field instead of the cursor's monitor.
    /// </summary>
    public void ShowCommandPalette(string? query, bool anchorToWindow)
    {
        if (_exit) return;
        if (_palette is not null)
        {
            if (string.IsNullOrEmpty(query)) _palette.Close();
            else { _palette.Query += query; _palette.Activate(); }
            return;
        }
        if (_overlay is not null || _capturing) return;
        var palette = new CommandPaletteWindow(SearchPalette, ExecutePaletteEntry);
        if (anchorToWindow && IsVisible && WindowState != WindowState.Minimized && PaletteButton.IsVisible)
        {
            try
            {
                Point topCenter = PaletteButton.PointToScreen(new Point(PaletteButton.ActualWidth / 2, 0));
                palette.AnchorDevicePoint = new Point(topCenter.X, topCenter.Y - 6);
            }
            catch (InvalidOperationException) { }
        }
        if (!string.IsNullOrEmpty(query)) palette.Query = query;
        _palette = palette;
        palette.Closed += (_, _) => { if (ReferenceEquals(_palette, palette)) _palette = null; };
        palette.Show();
        palette.Activate();
    }

    private void OpenPalette(object sender, RoutedEventArgs e) => ShowCommandPalette(null, anchorToWindow: true);

    /// <summary>Typing while the top bar search field has focus opens the palette with that text.</summary>
    private void PaletteButtonTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(char.IsControl)) return;
        e.Handled = true;
        ShowCommandPalette(e.Text, anchorToWindow: true);
    }

    private void CancelJob(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToolJob job }) job.Cancel();
    }

    private void InitializePaletteHotkeyBox()
    {
        _updatingPaletteControls = true;
        try
        {
            PaletteHotkeyBox.Items.Clear();
            foreach ((string gesture, string label) in PalettePresets) PaletteHotkeyBox.Items.Add(new ComboBoxItem { Content = label, Tag = gesture });
            string current = _preferences.PaletteHotkey ?? "";
            string label2 = current;
            if (HotkeyGesture.TryParse(current, out HotkeyGesture parsed)) { current = parsed.ToString(); label2 = parsed.DisplayText; }
            int index = Array.FindIndex(PalettePresets, p => string.Equals(p.Gesture, current, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                PaletteHotkeyBox.Items.Insert(0, new ComboBoxItem { Content = label2, Tag = current });
                index = 0;
            }
            PaletteHotkeyBox.SelectedIndex = index;
        }
        finally { _updatingPaletteControls = false; }
    }

    private void PaletteHotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingPaletteControls || PaletteHotkeyBox.SelectedItem is not ComboBoxItem { Tag: string gesture }) return;
        string previous = _preferences.PaletteHotkey;
        try
        {
            _preferences.PaletteHotkey = gesture;
            _preferences.Save();
            if (_sessionInitialized) ConfigurePaletteHotkey();
        }
        catch (Exception ex)
        {
            _preferences.PaletteHotkey = previous;
            Report("Komut paleti kısayolu kaydedilemedi", ex);
        }
    }

    private void ConfigurePaletteHotkey()
    {
        _paletteHotkey?.Dispose();
        _paletteHotkey = null;
        string text = _preferences.PaletteHotkey ?? "";
        string hint = "Ctrl + K";
        if (text.Length == 0) PaletteHotkeyStatus.Text = "Genel kısayol kapalı. BroidShot penceresi açıkken Ctrl + K ile açabilirsin.";
        else if (!HotkeyGesture.TryParse(text, out HotkeyGesture gesture)) PaletteHotkeyStatus.Text = "Kısayol anlaşılamadı: " + text;
        else
        {
            _paletteHotkey = _services.Hotkeys.Register(gesture, ShowCommandPalette);
            if (_paletteHotkey.IsRegistered)
            {
                hint = gesture.DisplayText;
                PaletteHotkeyStatus.Text = gesture.DisplayText + " her yerden araç ve komut aramayı açar. Pencere açıkken Ctrl + K da çalışır.";
            }
            else PaletteHotkeyStatus.Text = _paletteHotkey.Error + " Başka bir kısayol seçebilirsin; pencere açıkken Ctrl + K çalışır.";
        }
        PaletteHintLabel.Text = hint;
        if (_trayPaletteItem != null) _trayPaletteItem.Text = "Araç ara  ·  " + hint;
    }

    private void RefreshModuleStatus()
    {
        ModuleStatusList.Children.Clear();
        foreach (ModuleSlot slot in _slots.Where(s => !s.IsBuiltIn))
        {
            var row = new Border { BorderThickness = new Thickness(0, ModuleStatusList.Children.Count == 0 ? 0 : 1, 0, 0), MinHeight = 0, Padding = new Thickness(20, 12, 20, 12) };
            row.SetResourceReference(StyleProperty, "SettingRow");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var glyph = new TextBlock { Text = ShellIcons.Resolve(slot.Icon, slot.Id), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            glyph.SetResourceReference(StyleProperty, "IconText");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            grid.Children.Add(glyph);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            texts.Children.Add(new TextBlock { Text = slot.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            var details = new List<string>();
            if (slot.Error is not null) details.Add(slot.Error);
            details.AddRange(slot.HotkeyNotes.Select(note => "Kısayol · " + note));
            if (details.Count > 0)
            {
                var detail = new TextBlock { Text = string.Join("\n", details), FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                texts.Children.Add(detail);
            }
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            (string state, string chip) = slot.Error is not null ? ("Başlatılamadı", "DangerChip") : slot.Initialized ? ("Hazır", "SuccessChip") : ("Bekliyor", "Chip");
            var badge = new Border { Child = new TextBlock { Text = state }, VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(StyleProperty, chip);
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
            row.Child = grid;
            AutomationProperties.SetName(row, slot.Title + " · " + state);
            ModuleStatusList.Children.Add(row);
        }
        if (ModuleStatusList.Children.Count == 0)
        {
            var none = new TextBlock { Text = "Ek araç yüklü değil.", Margin = new Thickness(20, 14, 20, 14) };
            none.SetResourceReference(StyleProperty, "MutedText");
            ModuleStatusList.Children.Add(none);
        }
        DataFolderLabel.Text = "Araç verileri: " + Core.DataPaths.ModulesRoot + "\nHarici araçlar (yt-dlp, ffmpeg): " + _services.Tools.ToolsRoot;
    }

    private void Report(string title, Exception exception, Window? owner = null)
    {
        StatusLabel.Text = title + ": " + exception.Message;
        if (_exit) return;
        if (owner is { IsLoaded: true }) MessageBox.Show(owner, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
