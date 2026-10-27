using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using BroidShot.Core;

namespace BroidShot.Modules.Clipboard;

public partial class ClipboardView : UserControl
{
    private readonly ClipboardModule _module;
    private readonly ObservableCollection<ClipItemViewModel> _items = new();
    private readonly Dictionary<long, ClipItemViewModel> _cache = new();
    private readonly DispatcherTimer _searchDelay;
    private readonly DispatcherTimer _clock;
    private ClipFilterKind _kind = ClipFilterKind.All;
    private long? _tagId;
    private long? _renamingTagId;
    private Func<Task>? _confirmAction;

    internal ClipboardView(ClipboardModule module)
    {
        _module = module;
        if (TryFindResource("Card") is null) Resources.MergedDictionaries.Add(ThemeResources.Load());
        InitializeComponent();
        ItemList.ItemsSource = _items;
        BuildChips();
        LoadSettingsIntoForm();

        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Refresh(); };
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => { foreach (ClipItemViewModel item in _items) item.Tick(); };

        _module.HistoryChanged += (_, _) => Refresh();
        Loaded += (_, _) => { _clock.Start(); Refresh(); };
        Unloaded += (_, _) => _clock.Stop();
        ShowEmpty("", "Geçmiş yükleniyor…", "");
        _ = WaitForHistoryAsync();
    }

    private async Task WaitForHistoryAsync()
    {
        try { await _module.Ready; }
        catch (Exception) { ShowEmpty("", "Pano geçmişi açılamadı", "Ayrıntılar hata günlüğünde."); return; }
        Refresh();
    }

    private void ShowEmpty(string glyph, string title, string text)
    {
        EmptyIcon.Text = glyph;
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = Visibility.Visible;
    }

    // ---- list ----

    internal void Refresh()
    {
        ClipboardHistory? history = _module.History;
        if (history is null) return;
        IReadOnlyList<ClipTag> tags = history.Tags;
        if (_tagId is { } selected && tags.All(t => t.Id != selected)) _tagId = null;
        Dictionary<long, string> tagNames = tags.ToDictionary(t => t.Id, t => t.Name);
        IReadOnlyList<ClipEntry> all = history.Items;
        List<ClipEntry> visible = ClipFilter.PageOrder(ClipFilter.Apply(all, SearchBox.Text, _kind, _tagId, tags)).ToList();

        var wanted = new List<ClipItemViewModel>(visible.Count);
        var alive = new HashSet<long>();
        foreach (ClipEntry entry in visible)
        {
            string names = string.Join(" #", entry.TagIds.Where(tagNames.ContainsKey).Select(id => tagNames[id]));
            if (_cache.TryGetValue(entry.Id, out ClipItemViewModel? vm))
            {
                if (!ReferenceEquals(vm.Entry, entry) || vm.TagNames != names) vm.Update(entry, names);
            }
            else _cache[entry.Id] = vm = new ClipItemViewModel(_module, entry, names);
            wanted.Add(vm);
            alive.Add(entry.Id);
        }
        var existing = new HashSet<long>(all.Select(e => e.Id));
        foreach (long id in _cache.Keys.Where(id => !existing.Contains(id)).ToArray()) _cache.Remove(id);
        Sync(wanted);
        UpdateSections();

        if (_items.Count > 0) EmptyState.Visibility = Visibility.Collapsed;
        else if (all.Count > 0) ShowEmpty("", "Eşleşen öğe yok", "Aramaya ya da seçili filtreye uyan öğe yok. Filtreyi Tümü yap ya da aramayı temizle.");
        else if (_module.Settings.MonitoringEnabled) ShowEmpty("", "Henüz bir şey kopyalanmadı", "Kopyaladığın metin, bağlantı, görsel ve dosyalar burada görünecek. Win+V ile hızlı paneli açabilirsin.");
        else ShowEmpty("", "Pano izleme kapalı", "Ayarlar'dan \"Panoyu izle\"yi açınca kopyaladıkların burada görünür.");

        TagList.ItemsSource = tags.Select(t => new TagViewModel(t, all.Count(e => e.TagIds.Contains(t.Id)), _tagId == t.Id)).ToList();
        AllTagsButton.Tag = _tagId is null ? "selected" : null;
        AllCount.Text = all.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateHotkeyStatus();
    }

    /// <summary>Headers above the pinned block and the first unpinned item (only when both kinds are visible).</summary>
    private void UpdateSections()
    {
        bool anyPinned = _items.Any(i => i.IsPinned);
        bool anyOther = _items.Any(i => !i.IsPinned);
        for (int i = 0; i < _items.Count; i++)
        {
            ClipItemViewModel item = _items[i];
            bool first = i == 0 || _items[i - 1].IsPinned != item.IsPinned;
            item.SectionTitle = !anyPinned || !first ? null
                : item.IsPinned ? "Sabitlenenler"
                : anyOther ? "Son kopyalananlar" : null;
        }
    }

    /// <summary>Minimal moves so the list keeps its scroll position when one item changes.</summary>
    private void Sync(List<ClipItemViewModel> wanted)
    {
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < _items.Count && ReferenceEquals(_items[i], wanted[i])) continue;
            int existing = _items.IndexOf(wanted[i]);
            if (existing >= 0) _items.Move(existing, i);
            else _items.Insert(i, wanted[i]);
        }
        while (_items.Count > wanted.Count) _items.RemoveAt(_items.Count - 1);
    }

    private void BuildChips()
    {
        ChipPanel.Children.Clear();
        foreach (var (kind, label) in ClipFilter.Chips)
        {
            var chip = new ToggleButton { Content = label, Style = (Style)FindResource("FilterChip"), IsChecked = kind == _kind, Tag = kind };
            chip.Click += (_, _) =>
            {
                _kind = kind;
                foreach (ToggleButton other in ChipPanel.Children.OfType<ToggleButton>()) other.IsChecked = ReferenceEquals(other, chip);
                Refresh();
            };
            ChipPanel.Children.Add(chip);
        }
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    // ---- history / settings switch ----

    private void SettingsToggled(object sender, RoutedEventArgs e) => ShowSettings(SettingsToggle.IsChecked == true);

    internal void ShowSettings(bool show)
    {
        if (SettingsToggle.IsChecked != show) SettingsToggle.IsChecked = show; // raises SettingsToggled again, which is a no-op
        if (show) LoadSettingsIntoForm();
        SettingsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SettingsHeading.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SettingsToggle.Content = show ? "Geçmişe dön" : "Ayarlar";
        ThemeAssist.SetIcon(SettingsToggle, show ? "" : "");
        if (!show) SettingsStatus.Text = "";
    }


    private static ClipItemViewModel? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipItemViewModel;

    private void CopyItem(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && _module.CopyToClipboard(item.Id))
            _module.Host.ShowToast("Panoya kopyalandı", item.IsSensitive ? "Hassas öğe kopyalandı." : "Öğe panoya kopyalandı.", kind: ToastKind.Success);
    }

    private async void PasteItem(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await _module.PasteAsync(item.Id);
    }

    private async void TogglePin(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await _module.SetPinnedAsync(item.Id, !item.IsPinned);
    }

    private async void DeleteItem(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await _module.DeleteAsync(item.Id);
    }

    private void RevealItem(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) item.Reveal(!item.IsRevealed);
    }

    private void EditTags(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || sender is not Button button || _module.History is not { } history) return;
        var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 10), MinWidth = 200 };
        panel.Children.Add(new TextBlock { Text = "Etiketler", Style = (Style)FindResource("SectionLabel") });
        IReadOnlyList<ClipTag> tags = history.Tags;
        if (tags.Count == 0)
            panel.Children.Add(new TextBlock { Text = "Henüz etiket yok. Soldaki Etiketler bölümünden oluşturabilirsin.", TextWrapping = TextWrapping.Wrap, MaxWidth = 220, Style = (Style)FindResource("CaptionText"), Margin = new Thickness(0, 0, 0, 4) });
        foreach (ClipTag tag in tags)
        {
            var box = new CheckBox { Content = tag.Name, IsChecked = item.Entry.TagIds.Contains(tag.Id), Margin = new Thickness(0, 5, 0, 5) };
            long tagId = tag.Id;
            box.Checked += async (_, _) => await _module.SetTagAsync(item.Id, tagId, true);
            box.Unchecked += async (_, _) => await _module.SetTagAsync(item.Id, tagId, false);
            panel.Children.Add(box);
        }
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("OverlayBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("StrokeBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = (CornerRadius)FindResource("RadiusOverlay"),
                Child = panel,
            },
        };
        // The popup is not in the page's tree, so the theme font is not inherited; give it explicitly.
        TextElement.SetFontFamily(popup.Child, ThemeResources.UiFontFamily);
        TextElement.SetFontSize(popup.Child, ThemeResources.BodyFontSize);
        TextElement.SetForeground(popup.Child, (System.Windows.Media.Brush)FindResource("TextBrush"));
        popup.IsOpen = true;
    }

    // ---- clear + confirmation bar ----

    private void AskClear(object sender, RoutedEventArgs e) => ShowClearConfirmation();

    internal void ShowClearConfirmation()
    {
        int count = _module.History?.Items.Count(i => !i.Pinned) ?? 0;
        if (count == 0)
        {
            ShowConfirm("Silinecek sabitlenmemiş öğe yok.", null, null);
            return;
        }
        ShowConfirm($"Sabitlenmemiş {count} öğe kalıcı olarak silinecek. Sabitlenen öğeler kalır.", "Geçmişi temizle", async () =>
        {
            int removed = await _module.ClearHistoryAsync();
            _module.Host.ShowToast("Pano geçmişi temizlendi", $"{removed} öğe silindi.", kind: ToastKind.Success);
        });
    }

    private void ShowConfirm(string message, string? confirmLabel, Func<Task>? action)
    {
        ConfirmText.Text = message;
        _confirmAction = action;
        ConfirmButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = confirmLabel ?? "Tamam";
        ConfirmBar.Visibility = Visibility.Visible;
        (action is null ? null : ConfirmButton)?.Focus();
    }

    private async void ConfirmYes(object sender, RoutedEventArgs e)
    {
        Func<Task>? action = _confirmAction;
        HideConfirm();
        if (action is not null) await action();
    }

    private void ConfirmNo(object sender, RoutedEventArgs e) => HideConfirm();

    private void HideConfirm()
    {
        _confirmAction = null;
        ConfirmBar.Visibility = Visibility.Collapsed;
    }

    // ---- tags ----

    private void SelectAllTags(object sender, RoutedEventArgs e)
    {
        _tagId = null;
        Refresh();
    }

    private void SelectTag(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TagViewModel tag) return;
        _tagId = _tagId == tag.Id ? null : tag.Id;
        Refresh();
    }

    private void RenameTag(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TagViewModel tag) return;
        _renamingTagId = tag.Id;
        TagNameBox.Text = tag.Name;
        TagSaveButton.Content = "Kaydet";
        TagEditHint.Text = $"\"{tag.Name}\" etiketinin yeni adını yaz. Vazgeçmek için Esc.";
        TagNameBox.Focus();
        TagNameBox.SelectAll();
    }

    private void DeleteTag(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TagViewModel tag) return;
        long id = tag.Id;
        ShowConfirm($"\"{tag.Name}\" etiketi silinecek. Öğeler silinmez, yalnız etiket kaldırılır.", "Etiketi sil", () => _module.DeleteTagAsync(id));
    }

    private void TagNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SaveTag(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { ResetTagEditor(); e.Handled = true; }
    }

    private async void SaveTag(object sender, RoutedEventArgs e)
    {
        string name = TagNameBox.Text;
        bool ok = _renamingTagId is { } id ? await _module.RenameTagAsync(id, name) : await _module.CreateTagAsync(name);
        if (ok) ResetTagEditor();
    }

    private void ResetTagEditor()
    {
        _renamingTagId = null;
        TagNameBox.Text = "";
        TagSaveButton.Content = "Ekle";
        TagEditHint.Text = "Bir öğeye etiket vermek için öğedeki etiket düğmesini kullan.";
    }

    // ---- settings ----

    private void LoadSettingsIntoForm()
    {
        ClipboardSettings settings = _module.Settings;
        MonitoringBox.IsChecked = settings.MonitoringEnabled;
        QuickPanelBox.IsChecked = settings.QuickPanelEnabled;
        QuickPanelTitle.Text = settings.UsesWinVTakeover
            ? "Win+V ile hızlı paneli aç (Windows'un pano geçmişinin yerine)"
            : "Hızlı panel kısayolu (" + (HotkeyGesture.TryParse(settings.QuickPanelHotkey, out HotkeyGesture g) ? g.DisplayText : settings.QuickPanelHotkey) + ")";
        MaxItemsBox.Text = settings.MaxItems.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MaxAgeBox.Text = settings.MaxAgeDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        BlocklistBox.Text = string.Join(Environment.NewLine, settings.BlockedApps);
    }

    private void UpdateHotkeyStatus()
    {
        ClipboardSettings settings = _module.Settings;
        if (settings.UsesWinVTakeover)
        {
            HotkeyStatus.Text = !settings.QuickPanelEnabled ? "Kapalı: Win+V yine Windows'un kendi pano geçmişini açar."
                : _module.WinVTakeoverError is { } error ? "Win+V devralınamadı: " + error
                : _module.WinVTakeoverActive ? "Etkin: Win+V, Windows'un pano geçmişi yerine bu paneli açar. Yönetici olarak çalışan bir pencere öndeyken Windows'un kendi paneli açılır."
                : "Win+V, Windows'un pano geçmişi yerine bu paneli açar. Kapatınca Win+V yine Windows'un paneline gider.";
            return;
        }
        HotkeyRegistration? registration = _module.QuickPanelHotkey;
        HotkeyStatus.Text = !settings.QuickPanelEnabled ? "Kapalı"
            : registration is null ? ""
            : registration.IsRegistered ? "Etkin: " + registration.Gesture.DisplayText
            : "Kaydedilemedi: " + registration.Error;
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxItemsBox.Text.Trim(), out int maxItems) || maxItems < 10)
        {
            SettingsStatus.Text = "En fazla öğe en az 10 olmalı.";
            return;
        }
        if (!int.TryParse(MaxAgeBox.Text.Trim(), out int maxAge) || maxAge < 1)
        {
            SettingsStatus.Text = "Saklama süresi en az 1 gün olmalı.";
            return;
        }
        ClipboardSettings current = _module.Settings;
        var updated = new ClipboardSettings
        {
            MonitoringEnabled = MonitoringBox.IsChecked == true,
            QuickPanelEnabled = QuickPanelBox.IsChecked == true,
            QuickPanelHotkey = current.QuickPanelHotkey,
            MaxItems = maxItems,
            MaxAgeDays = maxAge,
            BlockedApps = BlocklistBox.Text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
        };
        try
        {
            _module.UpdateSettings(updated);
            LoadSettingsIntoForm();
            SettingsStatus.Text = "Kaydedildi.";
            Refresh();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            SettingsStatus.Text = "Ayarlar kaydedilemedi: " + ex.Message;
        }
    }
}
