using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BroidShot.Core;
using Microsoft.Win32;

namespace BroidShot.Modules.Convert;

/// <summary>One line of the result card (combined outputs and batch-level messages).</summary>
public sealed class ResultRow
{
    public ResultRow(string glyph, string title, string detail, Brush brush)
    {
        Glyph = glyph;
        Title = title;
        Detail = detail;
        Brush = brush;
    }

    public string Glyph { get; }
    public string Title { get; }
    public string Detail { get; }
    public Brush Brush { get; }
    public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>An operation as shown on the tiles: Segoe Fluent glyph, short title for the tile, full title, one-line description.</summary>
public sealed class OperationChoice
{
    private OperationChoice(ConvertOperation operation, string glyph, string title, string caption)
    {
        Operation = operation;
        Glyph = glyph;
        Title = title;
        Caption = caption;
    }

    public ConvertOperation Operation { get; }
    public string Glyph { get; }
    public string Title { get; }
    public string FullTitle => FileTypes.Describe(Operation);
    public string Caption { get; }

    /// <summary>Every operation in <see cref="ConvertOperation"/> order.</summary>
    public static IReadOnlyList<OperationChoice> All { get; } = new[]
    {
        new OperationChoice(ConvertOperation.PdfToImages, "", "Görsele çevir", "Sayfaları PNG ya da JPG yapar"),
        new OperationChoice(ConvertOperation.ImagesToPdf, "", "PDF yap", "Görselleri sırayla tek PDF'te toplar"),
        new OperationChoice(ConvertOperation.MergePdf, "", "Birleştir", "PDF'leri liste sırasıyla tek dosya yapar"),
        new OperationChoice(ConvertOperation.SplitPdf, "", "Böl", "Her sayfayı ya da aralıkları ayırır"),
        new OperationChoice(ConvertOperation.ExtractPages, "", "Sayfa çıkar", "Seçili sayfalardan yeni PDF"),
        new OperationChoice(ConvertOperation.DeletePages, "", "Sayfa sil", "Seçili sayfaları kaldırır"),
        new OperationChoice(ConvertOperation.RotatePages, "", "Döndür", "Sayfaları 90° ya da 180° çevirir"),
        new OperationChoice(ConvertOperation.ConvertImages, "", "Biçim ve boyut", "PNG, JPG, TIFF, ICO…; boyutlandırır"),
    };

    public static OperationChoice For(ConvertOperation operation) => All.First(c => c.Operation == operation);
}

public partial class ConvertView : UserControl
{
    private readonly ConvertModule _module;
    private readonly ObservableCollection<ConvertItem> _items = new();
    private readonly ObservableCollection<ResultRow> _results = new();
    private bool _loading = true;
    private bool _running;
    private bool _syncingTiles;
    private ToolJob? _job;
    private string? _lastOutput;

    internal ConvertView(ConvertModule module)
    {
        _module = module;
        InitializeComponent();
        FileList.ItemsSource = _items;
        ResultList.ItemsSource = _results;
        QuickOperations.ItemsSource = OperationChoice.All;
        foreach (ListBox segmented in new[] { DpiBox, RasterFormatBox, FitBox, SplitModeBox, RotateBox, TargetBox, PngCompressionBox, ResizeModeBox, LocationBox })
            segmented.SelectionChanged += KeepSelection;
        _items.CollectionChanged += (_, _) => OnItemsChanged();
        ApplyOptionsToControls(module.Settings.Options);
        RefreshOperations(module.Settings.LastOperation);
        _loading = false;
        OnItemsChanged();
    }

    private ConvertOperation? SelectedOperation => (OperationBox.SelectedItem as ComboBoxItem)?.Tag as ConvertOperation?;

    // ---------- public entry points (palette, messages) ----------

    /// <summary>Palette command: preselect an operation, drop incompatible files from the list and open the file dialog.</summary>
    internal void StartOperation(ConvertOperation operation)
    {
        if (!SelectOperation(operation)) return;
        BrowseForOperation(operation);
    }

    /// <summary>Preselects an operation (without a file dialog). Returns false while a batch is running.</summary>
    internal bool SelectOperation(ConvertOperation operation)
    {
        if (_running) { SetStatus("Bir dönüştürme sürüyor; bitmesini bekle ya da iptal et.", false); return false; }
        FileKind wanted = FileTypes.InputKind(operation);
        if (_items.Any(i => i.Kind != wanted)) _items.Clear();
        RefreshOperations(operation);
        return true;
    }

    /// <summary>Files sent by other modules: queued in the list; an incompatible old list is replaced.</summary>
    internal void AddFilesFromOutside(IReadOnlyList<string> paths) => AddFiles(paths, replaceOnKindMismatch: true);

    internal void Shutdown()
    {
        try { _job?.Cancel(); } catch (Exception ex) { _module.Host.Log("İş iptal edilemedi.", ex); }
    }

    // ---------- file list ----------

    // An empty page offers every file type; once files are listed the dialog filters for the chosen operation.
    private void BrowseFiles(object sender, RoutedEventArgs e) => BrowseForOperation(_items.Count == 0 ? null : SelectedOperation);

    /// <summary>Quick start tile on the empty page: preselect the operation and pick files for it.</summary>
    private void QuickOperation(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ConvertOperation operation) StartOperation(operation);
    }

    private void BrowseForOperation(ConvertOperation? operation)
    {
        var dialog = new OpenFileDialog
        {
            Title = operation is { } op ? FileTypes.Describe(op) + " · dosya seç" : "Dosya seç",
            Filter = operation is { } o ? FileTypes.FilterFor(o) : FileTypes.AnyDialogFilter,
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddFiles(dialog.FileNames, replaceOnKindMismatch: false);
    }

    private void AddFiles(IEnumerable<string> paths, bool replaceOnKindMismatch)
    {
        ConvertOperation? previousOperation = SelectedOperation;
        var incoming = new List<string>();
        var skipped = new List<string>();
        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                if (Directory.Exists(raw))
                {
                    foreach (string file in Directory.EnumerateFiles(raw).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
                        (FileTypes.GetKind(file) is null ? skipped : incoming).Add(file);
                }
                else if (File.Exists(raw)) (FileTypes.GetKind(raw) is null ? skipped : incoming).Add(raw);
                else skipped.Add(raw);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(raw);
            }
        }

        var known = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        var created = new List<ConvertItem>();
        foreach (string path in incoming)
        {
            string full = Path.GetFullPath(path);
            if (!known.Add(full)) continue;
            created.Add(new ConvertItem(full));
        }

        if (replaceOnKindMismatch && created.Count > 0 && _items.Any(i => created.Any(c => c.Kind != i.Kind))) _items.Clear();
        foreach (ConvertItem item in created)
        {
            _items.Add(item);
            _ = ProbeAsync(item);
        }

        if (created.Count > 0) PickOperationForList(previousOperation);
        if (skipped.Count > 0)
        {
            string names = string.Join(", ", skipped.Take(3).Select(Path.GetFileName));
            SetStatus($"{skipped.Count} dosya eklenmedi (desteklenmeyen tür ya da bulunamadı): {names}{(skipped.Count > 3 ? "…" : "")}", true);
        }
        else if (created.Count > 0) SetStatus("", false);
    }

    private async Task ProbeAsync(ConvertItem item)
    {
        bool needsPassword = false;
        string info = await Task.Run(() =>
        {
            try
            {
                item.SizeBytes = new FileInfo(item.Path).Length;
                string size = FormatSize(item.SizeBytes);
                if (item.Kind == FileKind.Pdf)
                {
                    (int pages, needsPassword) = PdfEditing.Probe(item.Path, item.Password);
                    if (needsPassword) return "Parola korumalı · " + size;
                    if (pages == 0) return "Okunamadı · " + size;
                    item.PageCount = pages;
                    return $"{pages} sayfa · {size}";
                }
                var probe = ImageProcessing.Probe(item.Path);
                return probe is { } p ? $"{p.Width}×{p.Height} piksel · {size}" : "Önizleme yok (biçim açılamayabilir) · " + size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "Okunamadı";
            }
        }).ConfigureAwait(true);
        item.Info = info;
        item.IsProtected = needsPassword;
        UpdateListSummary();
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return (unit == 0 ? value.ToString("0", CultureInfo.CurrentCulture) : value.ToString("0.0", CultureInfo.CurrentCulture)) + " " + units[unit];
    }

    private void OnItemsChanged()
    {
        bool any = _items.Count > 0;
        EmptyPanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        FilesPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ListCard.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ListTitle.Text = _items.Count == 1 ? "1 dosya" : $"{_items.Count} dosya";
        DropTitle.Text = "Daha fazla dosya bırak";
        if (!any) ResultCard.Visibility = Visibility.Collapsed;
        UpdateListSummary();
        if (!_loading) RefreshOperations(SelectedOperation);
    }

    /// <summary>"2 PDF · 1 görsel · 14 sayfa · 3,2 MB" under the list title.</summary>
    private void UpdateListSummary()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateListSummary)); return; }
        int pdfs = _items.Count(i => i.Kind == FileKind.Pdf), images = _items.Count - pdfs;
        var parts = new List<string>();
        if (pdfs > 0) parts.Add($"{pdfs} PDF");
        if (images > 0) parts.Add($"{images} görsel");
        int pages = _items.Sum(i => i.PageCount);
        if (pages > 0) parts.Add($"{pages} sayfa");
        long bytes = _items.Sum(i => i.SizeBytes);
        if (bytes > 0) parts.Add(FormatSize(bytes));
        ListSummary.Text = string.Join(" · ", parts);
    }

    private void KeepPdfs(object sender, RoutedEventArgs e) => KeepOnly(FileKind.Pdf);
    private void KeepImages(object sender, RoutedEventArgs e) => KeepOnly(FileKind.Image);

    private void KeepOnly(FileKind kind)
    {
        if (_running) return;
        ConvertOperation? previous = SelectedOperation;
        foreach (ConvertItem item in _items.Where(i => i.Kind != kind).ToList()) _items.Remove(item);
        PickOperationForList(previous);
    }

    private void MoveUp(object sender, RoutedEventArgs e) => Move(sender, -1);
    private void MoveDown(object sender, RoutedEventArgs e) => Move(sender, 1);

    private void Move(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.Tag is not ConvertItem item || _running) return;
        int index = _items.IndexOf(item), target = index + delta;
        if (index < 0 || target < 0 || target >= _items.Count) return;
        _items.Move(index, target);
    }

    private void RemoveItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ConvertItem item && !_running) _items.Remove(item);
    }

    private void ClearList(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _items.Clear();
        SetStatus("", false);
    }

    private void SortByName(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        var sorted = _items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int current = _items.IndexOf(sorted[i]);
            if (current != i) _items.Move(current, i);
        }
    }

    // ---------- drag and drop ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files && !_running ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(files && !_running);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (_running || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        AddFiles(paths, replaceOnKindMismatch: false);
        e.Handled = true;
    }

    /// <summary>Red dashed outline and tint on the drop zone (empty page) and the drop strip under the list.</summary>
    private void SetDropHighlight(bool on)
    {
        string stroke = on ? "AccentBrush" : "StrokeStrongBrush";
        DropDash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, stroke);
        DropStripDash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, stroke);
        DropZone.SetResourceReference(Border.BackgroundProperty, on ? "AccentSubtleBrush" : "SurfaceBrush");
        if (on) DropStrip.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBrush");
        else DropStrip.Background = Brushes.Transparent;
    }

    // ---------- operation picker ----------

    private void RefreshOperations(ConvertOperation? preferred)
    {
        IReadOnlyList<ConvertOperation> valid = FileTypes.ValidOperations(_items.Select(i => i.Kind).ToList());
        bool wasLoading = _loading;
        _loading = true;
        OperationBox.Items.Clear();
        foreach (ConvertOperation operation in valid)
            OperationBox.Items.Add(new ComboBoxItem { Content = FileTypes.Describe(operation), Tag = operation });
        ConvertOperation? choice = preferred is { } p && valid.Contains(p) ? p : valid.Count > 0 ? valid[0] : null;
        if (choice is { } c) OperationBox.SelectedIndex = valid.ToList().IndexOf(c);
        OperationBox.IsEnabled = valid.Count > 0;

        // Visible picker: one tile per valid operation, in the same order as OperationBox.
        _syncingTiles = true;
        OperationTiles.ItemsSource = valid.Select(OperationChoice.For).ToList();
        OperationTiles.Tag = Math.Max(4, valid.Count);
        _syncingTiles = false;
        bool mixed = _items.Count > 0 && valid.Count == 0;
        OperationTiles.Visibility = mixed ? Visibility.Collapsed : Visibility.Visible;
        MixedBar.Visibility = mixed ? Visibility.Visible : Visibility.Collapsed;
        _loading = wasLoading;
        UpdateRows();
    }

    private void OperationTileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTiles) return;
        int index = OperationTiles.SelectedIndex;
        if (index < 0)
        {
            // Ctrl+click can clear a ListBox selection; the operation stays selected.
            SyncTiles();
            return;
        }
        if (index != OperationBox.SelectedIndex) OperationBox.SelectedIndex = index;
    }

    private void SyncTiles()
    {
        _syncingTiles = true;
        OperationTiles.SelectedIndex = OperationBox.SelectedIndex;
        _syncingTiles = false;
    }

    /// <summary>A segmented choice always has a value: undo a Ctrl+click that cleared it.</summary>
    private static void KeepSelection(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: null } box && e.RemovedItems.Count > 0) box.SelectedItem = e.RemovedItems[0];
    }

    /// <summary>After adding files: keep the operation the user had when it still fits, otherwise a sensible default for the file kinds.</summary>
    private void PickOperationForList(ConvertOperation? previous)
    {
        IReadOnlyList<ConvertOperation> valid = FileTypes.ValidOperations(_items.Select(i => i.Kind).ToList());
        if (previous is { } keep && valid.Contains(keep)) { RefreshOperations(keep); return; }
        // A single image is most often converted; several images are most often turned into one PDF.
        ConvertOperation? preferred = _items.Count == 1 && _items[0].Kind == FileKind.Image ? ConvertOperation.ConvertImages : null;
        RefreshOperations(preferred);
    }

    private void OperationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // Results of the previous batch belong to the previous operation.
        if (!_running)
            foreach (ConvertItem item in _items.Where(i => i.State != ItemState.Ready)) item.SetState(ItemState.Ready);
        UpdateRows();
    }

    private void DpiChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) UpdateRows(); }
    private void OptionVisibilityChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) UpdateRows(); }
    private void LocationChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) UpdateRows(); }

    private static string TagOf(Selector box) => (box.SelectedItem as FrameworkElement)?.Tag?.ToString() ?? "";

    private void UpdateRows()
    {
        if (!IsInitialized) return;
        ConvertOperation? operation = SelectedOperation;

        bool pdfToImages = operation == ConvertOperation.PdfToImages;
        bool split = operation == ConvertOperation.SplitPdf;
        bool extract = operation == ConvertOperation.ExtractPages;
        bool delete = operation == ConvertOperation.DeletePages;
        bool rotate = operation == ConvertOperation.RotatePages;
        bool imagesToPdf = operation == ConvertOperation.ImagesToPdf;
        bool merge = operation == ConvertOperation.MergePdf;
        bool convertImages = operation == ConvertOperation.ConvertImages;

        string target = TagOf(TargetBox);
        bool ico = convertImages && target == "Ico";
        bool jpgTarget = convertImages && target == "Jpg";
        bool pngTarget = convertImages && target == "Png";

        Show(RowPageRange, pdfToImages || extract || delete || rotate);
        PageRangeLabel.Text = extract ? "Çıkarılacak sayfalar" : delete ? "Silinecek sayfalar" : rotate ? "Sayfalar (boş = hepsi)" : "Sayfalar (boş = hepsi)";
        Show(RowDpi, pdfToImages);
        Show(RowRasterFormat, pdfToImages);
        Show(RowTemplate, pdfToImages || split);
        TemplateLabel.Text = split ? "Parça adı şablonu" : "Dosya adı şablonu";
        Show(RowFit, imagesToPdf);
        Show(RowOutName, imagesToPdf || merge);
        Show(RowSplitMode, split);
        Show(RowSplitRanges, split && TagOf(SplitModeBox) == "Ranges");
        Show(RowRotate, rotate);
        Show(RowTarget, convertImages);
        Show(RowJpgQuality, jpgTarget || (pdfToImages && TagOf(RasterFormatBox) == "Jpg"));
        Show(RowPngCompression, pngTarget);
        Show(RowResizeMode, convertImages && !ico);
        string resize = TagOf(ResizeModeBox);
        Show(RowPercent, convertImages && !ico && resize == "Percent");
        Show(RowMaxSize, convertImages && !ico && resize == "MaxSize");
        Show(RowIcoSizes, ico);
        Show(RowStrip, convertImages && target is "Png" or "Jpg" or "Tiff");
        CustomDpiBox.Visibility = pdfToImages && TagOf(DpiBox) == "0" ? Visibility.Visible : Visibility.Collapsed;
        FolderRow.Visibility = TagOf(LocationBox) == "CustomFolder" ? Visibility.Visible : Visibility.Collapsed;

        OperationHint.Text = BuildHint(operation);
        OperationHint.Visibility = OperationHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OptionsTitle.Text = operation is { } selected ? FileTypes.Describe(selected) : "Ayarlar";
        RunSummary.Text = operation is { } op && _items.Count > 0 ? $"{(_items.Count == 1 ? "1 dosya" : _items.Count + " dosya")} · {FileTypes.Describe(op)}" : "";
        RunSummary.Visibility = RunSummary.Text.Length > 0 && !_running ? Visibility.Visible : Visibility.Collapsed;
        OptionsPanel.Visibility = operation is null ? Visibility.Hidden : Visibility.Visible;
        ConvertButton.IsEnabled = !_running && operation is not null && _items.Count > 0;
        SyncTiles();
    }

    private string BuildHint(ConvertOperation? operation)
    {
        if (_items.Count > 0 && operation is null)
            return "Listede hem PDF hem görsel var. Birini listeden çıkar; her işlem tek tür dosyayla çalışır.";
        return operation switch
        {
            ConvertOperation.MergePdf => "PDF'ler listedeki sırayla birleşir. Sırayı ok düğmeleriyle değiştirebilirsin.",
            ConvertOperation.ImagesToPdf => "Her görsel bir sayfa olur; sayfa sırası listedeki sıradır.",
            ConvertOperation.SplitPdf => "Her PDF için ayrı parçalar üretilir.",
            ConvertOperation.ConvertImages when TagOf(TargetBox) == "Ico" => "Simge, seçili boyutların hepsini tek .ico dosyasında toplar (kare olmayan görsel ortalanır).",
            ConvertOperation.ConvertImages => "Çok kareli GIF/TIFF dosyalarında yalnız ilk kare dönüştürülür.",
            _ => ""
        };
    }

    private static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    // ---------- options <-> controls ----------

    private static void Select(Selector box, string tag)
    {
        foreach (FrameworkElement item in box.Items.OfType<FrameworkElement>())
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal)) { box.SelectedItem = item; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private void ApplyOptionsToControls(ConvertOptions o)
    {
        Select(LocationBox, o.Location.ToString());
        FolderBox.Text = o.OutputFolder;
        PageRangeBox.Text = o.PageRange;
        Select(DpiBox, o.Dpi.ToString(CultureInfo.InvariantCulture));
        if (!(DpiBox.SelectedItem is FrameworkElement { Tag: string t } && t == o.Dpi.ToString(CultureInfo.InvariantCulture)))
        {
            Select(DpiBox, "0");
            CustomDpiBox.Text = o.Dpi.ToString(CultureInfo.InvariantCulture);
        }
        Select(RasterFormatBox, o.RasterFormat.ToString());
        JpgQualityBox.Text = o.JpgQuality.ToString(CultureInfo.InvariantCulture);
        TemplateBox.Text = o.NameTemplate;
        Select(FitBox, o.PageFit.ToString());
        OutNameBox.Text = o.OutputName;
        Select(SplitModeBox, o.SplitMode.ToString());
        SplitRangesBox.Text = o.SplitRanges;
        Select(RotateBox, o.RotateDegrees.ToString(CultureInfo.InvariantCulture));
        Select(TargetBox, o.TargetFormat.ToString());
        Select(ResizeModeBox, o.ResizeMode.ToString());
        PercentBox.Text = o.ResizePercent.ToString("0.##", CultureInfo.CurrentCulture);
        MaxWidthBox.Text = o.MaxWidth.ToString(CultureInfo.InvariantCulture);
        MaxHeightBox.Text = o.MaxHeight.ToString(CultureInfo.InvariantCulture);
        Select(PngCompressionBox, o.PngCompression.ToString());
        StripMetadataCheck.IsChecked = o.StripMetadata;
        foreach (CheckBox box in IcoSizesPanel.Children.OfType<CheckBox>())
            box.IsChecked = o.IconSizes.Contains(int.Parse(box.Tag.ToString()!, CultureInfo.InvariantCulture));
    }

    /// <summary>Validates the controls and fills <paramref name="options"/>. Returns a Turkish message on invalid input.</summary>
    private bool TryReadOptions(ConvertOperation operation, out ConvertOptions options, out string? error)
    {
        options = new ConvertOptions();
        error = null;
        ConvertOptions o = options;
        o.Location = Enum.Parse<OutputLocation>(TagOf(LocationBox));
        o.OutputFolder = FolderBox.Text.Trim();
        if (o.Location == OutputLocation.CustomFolder)
        {
            if (o.OutputFolder.Length == 0) { error = "Çıktı klasörünü seç."; return false; }
            try { o.OutputFolder = Path.GetFullPath(o.OutputFolder); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { error = "Çıktı klasörü geçersiz."; return false; }
        }

        o.PageRange = PageRangeBox.Text.Trim();
        o.NameTemplate = string.IsNullOrWhiteSpace(TemplateBox.Text) ? FileNaming.DefaultPageTemplate : TemplateBox.Text.Trim();
        o.OutputName = OutNameBox.Text.Trim();
        o.SplitRanges = SplitRangesBox.Text.Trim();
        o.SplitMode = Enum.Parse<SplitMode>(TagOf(SplitModeBox));
        o.RasterFormat = Enum.Parse<RasterFormat>(TagOf(RasterFormatBox));
        o.PageFit = Enum.Parse<PageFit>(TagOf(FitBox));
        o.RotateDegrees = int.Parse(TagOf(RotateBox), CultureInfo.InvariantCulture);
        o.TargetFormat = Enum.Parse<ImageTarget>(TagOf(TargetBox));
        o.ResizeMode = Enum.Parse<ResizeKind>(TagOf(ResizeModeBox));
        o.PngCompression = Enum.Parse<PngCompression>(TagOf(PngCompressionBox));
        o.StripMetadata = StripMetadataCheck.IsChecked == true;

        // Numbers: only validated for the visible, relevant options.
        string tag = TagOf(DpiBox);
        if (tag == "0")
        {
            if (!int.TryParse(CustomDpiBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int dpi) || dpi < 10 || dpi > 1200)
            { if (operation == ConvertOperation.PdfToImages) { error = "DPI 10 ile 1200 arasında bir sayı olmalı."; return false; } dpi = 150; }
            o.Dpi = dpi;
        }
        else o.Dpi = int.Parse(tag, CultureInfo.InvariantCulture);

        bool needsQuality = (operation == ConvertOperation.PdfToImages && o.RasterFormat == RasterFormat.Jpg) ||
                            (operation == ConvertOperation.ConvertImages && o.TargetFormat == ImageTarget.Jpg);
        if (!int.TryParse(JpgQualityBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int quality) || quality < 1 || quality > 100)
        { if (needsQuality) { error = "JPG kalitesi 1 ile 100 arasında olmalı."; return false; } quality = 90; }
        o.JpgQuality = quality;

        if (operation == ConvertOperation.ConvertImages && o.TargetFormat != ImageTarget.Ico)
        {
            if (o.ResizeMode == ResizeKind.Percent)
            {
                if (!TryParseNumber(PercentBox.Text, out double percent) || percent <= 0 || percent > 1000) { error = "Yüzde 0'dan büyük ve en çok 1000 olmalı."; return false; }
                o.ResizePercent = percent;
            }
            else if (o.ResizeMode == ResizeKind.MaxSize)
            {
                if (!int.TryParse(MaxWidthBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int mw) ||
                    !int.TryParse(MaxHeightBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int mh) || mw > 30000 || mh > 30000)
                { error = "En büyük genişlik ve yükseklik için 0 ile 30000 arası sayı yaz (0 = sınırsız)."; return false; }
                if (mw == 0 && mh == 0) { error = "Genişlik ya da yükseklikten en az biri 0'dan büyük olmalı."; return false; }
                o.MaxWidth = mw; o.MaxHeight = mh;
            }
        }
        if (operation == ConvertOperation.ConvertImages && o.TargetFormat == ImageTarget.Ico)
        {
            o.IconSizes = IcoSizesPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true)
                .Select(c => int.Parse(c.Tag.ToString()!, CultureInfo.InvariantCulture)).ToList();
            if (o.IconSizes.Count == 0) { error = "Simge için en az bir boyut seç."; return false; }
        }
        else o.IconSizes = _module.Settings.Options.IconSizes;

        if (operation is ConvertOperation.PdfToImages or ConvertOperation.ExtractPages or ConvertOperation.DeletePages or ConvertOperation.RotatePages)
        {
            if (PageRangeParser.ValidateSyntax(o.PageRange) is { } rangeError) { error = rangeError; return false; }
            if (operation is ConvertOperation.ExtractPages or ConvertOperation.DeletePages && o.PageRange.Length == 0)
            { error = operation == ConvertOperation.ExtractPages ? "Çıkarılacak sayfaları yaz. Örnek: 1-3,5" : "Silinecek sayfaları yaz. Örnek: 2,4-6"; return false; }
        }
        if (operation == ConvertOperation.SplitPdf && o.SplitMode == SplitMode.Ranges)
        {
            if (o.SplitRanges.Length == 0) { error = "Bölme aralıklarını yaz. Her virgülle ayrılan aralık ayrı bir dosya olur. Örnek: 1-3,4-6,7"; return false; }
            if (PageRangeParser.ValidateSyntax(o.SplitRanges) is { } splitError) { error = splitError; return false; }
        }
        return true;
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);

    private void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Çıktı klasörünü seç" };
        if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) FolderBox.Text = dialog.FolderName;
    }

    // ---------- running ----------

    private async void OnConvert(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (SelectedOperation is not { } operation || _items.Count == 0) return;

        if (operation == ConvertOperation.MergePdf && _items.Count < 2)
        {
            SetStatus("Birleştirmek için en az iki PDF ekle.", true);
            return;
        }
        if (!TryReadOptions(operation, out ConvertOptions options, out string? error))
        {
            SetStatus(error!, true);
            return;
        }

        _module.Settings.LastOperation = operation;
        _module.Settings.Options = options;
        _module.SaveSettings();

        List<ConvertItem> snapshot = _items.ToList();
        _results.Clear();
        ResultCard.Visibility = Visibility.Collapsed;
        _lastOutput = null;
        foreach (ConvertItem item in snapshot) item.SetState(ItemState.Queued);
        SetRunning(true);
        SetStatus("", false);

        string title = $"{FileTypes.Describe(operation)} · {(snapshot.Count == 1 ? snapshot[0].Name : snapshot.Count + " dosya")}";
        ToolJob job = _module.Host.Jobs.Start(title);
        _job = job;
        var engine = new ConvertEngine(AskPassword)
        {
            Report = (fraction, status) =>
            {
                job.Report(fraction, status);
                Dispatcher.BeginInvoke(new Action(() => ShowProgress(fraction, status)));
            },
            ItemProgress = (item, fraction) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_running) item.SetState(ItemState.Working, "", fraction * 100);
            })),
            ItemFinished = (item, fileOutcome) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_running) ShowItemOutcome(item, fileOutcome);
            }))
        };

        BatchOutcome outcome;
        try
        {
            outcome = await Task.Run(() => engine.RunAsync(operation, snapshot, options, job.Token)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _module.Host.Log("Dönüştürme başarısız.", ex);
            job.Fail(ex.Message);
            SetRunning(false);
            foreach (ConvertItem item in snapshot.Where(i => i.IsBusy)) item.SetState(ItemState.Ready);
            SetStatus("Dönüştürme başarısız: " + ConvertEngine.Describe(ex), true);
            _job = null;
            return;
        }

        _job = null;
        SetRunning(false);
        ApplyItemStates(snapshot, outcome);
        ShowResults(outcome);
        FinishJob(job, outcome);
        ShowCompletionToast(outcome);
    }

    // ---------- per-file states ----------

    /// <summary>Live update when the engine finishes one file. Merge / images → PDF keep it "working" until the combined file is written.</summary>
    private static void ShowItemOutcome(ConvertItem item, FileOutcome outcome)
    {
        if (outcome.Error is { } error) item.SetState(ItemState.Failed, error);
        else if (outcome.Outputs.Count > 0) item.SetState(outcome.Warnings.Count > 0 ? ItemState.Warning : ItemState.Done, DescribeOutputs(outcome));
        else item.SetState(ItemState.Working, "", 100);
    }

    /// <summary>Final state of every file of the batch, from the outcome (authoritative over the live updates).</summary>
    private static void ApplyItemStates(IReadOnlyList<ConvertItem> snapshot, BatchOutcome outcome)
    {
        var own = new Dictionary<string, FileOutcome>(StringComparer.OrdinalIgnoreCase);
        FileOutcome? combined = null;
        foreach (FileOutcome file in outcome.Files)
        {
            if (file.Label is not null) combined ??= file;
            else own.TryAdd(file.Source, file);
        }

        foreach (ConvertItem item in snapshot)
        {
            own.TryGetValue(item.Path, out FileOutcome? mine);
            if (mine?.Error is { } error) item.SetState(ItemState.Failed, error);
            else if (outcome.WasCancelled && item.IsBusy)
            {
                int written = mine?.Outputs.Count ?? 0;
                item.SetState(ItemState.Cancelled, written > 0 ? $"İptal edildi · {written} dosya iptalden önce yazıldı" : "İptal edildi; bu dosya işlenmedi");
            }
            else if (mine is { Outputs.Count: > 0 }) item.SetState(mine.Warnings.Count > 0 ? ItemState.Warning : ItemState.Done, DescribeOutputs(mine));
            else if (combined is not null)
            {
                if (combined.Error is { } combinedError) item.SetState(ItemState.Failed, combinedError);
                else if (combined.Outputs.Count > 0) item.SetState(ItemState.Done, "→ " + Path.GetFileName(combined.Outputs[0]));
                else item.SetState(outcome.WasCancelled ? ItemState.Cancelled : ItemState.Ready, outcome.WasCancelled ? "İptal edildi" : "");
            }
            else if (mine is not null) item.SetState(ItemState.Done, "Tamamlandı");
            else item.SetState(outcome.WasCancelled ? ItemState.Cancelled : ItemState.Ready, outcome.WasCancelled ? "İptal edildi; bu dosya işlenmedi" : "");
        }
    }

    private static string DescribeOutputs(FileOutcome outcome)
    {
        string text = outcome.Outputs.Count == 1
            ? "→ " + Path.GetFileName(outcome.Outputs[0])
            : $"→ {outcome.Outputs.Count} dosya ({Path.GetFileName(outcome.Outputs[0])} …)";
        if (outcome.Warnings.Count > 0) text += " · " + string.Join(" ", outcome.Warnings);
        return text;
    }

    private static void FinishJob(ToolJob job, BatchOutcome outcome)
    {
        if (outcome.WasCancelled) job.Cancelled();
        else if (outcome.Files.Count > 0 && outcome.Succeeded == 0) job.Fail("Hiçbir dosya dönüştürülemedi.");
        else if (outcome.Failed > 0) job.Complete($"{outcome.Succeeded} tamam, {outcome.Failed} hatalı");
        else job.Complete(outcome.Outputs.Count() == 1 ? "1 dosya yazıldı" : $"{outcome.Outputs.Count()} dosya yazıldı");
    }

    private void ShowCompletionToast(BatchOutcome outcome)
    {
        string? first = outcome.Outputs.FirstOrDefault();
        int written = outcome.Outputs.Count();
        if (outcome.WasCancelled)
        {
            _module.Host.ShowToast("Dönüştürme iptal edildi", written > 0 ? $"{written} dosya iptalden önce yazıldı." : "Hiç dosya yazılmadı.", kind: ToastKind.Warning);
            return;
        }
        if (outcome.Failed > 0 && written == 0)
        {
            _module.Host.ShowToast("Dönüştürme başarısız", "Dosyalar işlenemedi; ayrıntılar sayfada.", "Aç", () => _module.Host.NavigateToSelf(), ToastKind.Error);
            return;
        }
        string message = written == 1 ? Path.GetFileName(first!) : $"{written} dosya yazıldı";
        if (outcome.Failed > 0) message += $" · {outcome.Failed} dosyada sorun var";
        _module.Host.ShowToast(new ToastOptions("Dönüştürme tamamlandı", message)
        {
            Kind = outcome.Failed > 0 ? ToastKind.Warning : ToastKind.Success,
            ActionLabel = first is null ? null : "Klasörü aç",
            Action = first is null ? null : () => RevealInExplorer(first)
        });
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _job?.Cancel();
        CancelButton.IsEnabled = false;
        RunProgressStatus("İptal ediliyor…");
    }

    private void SetRunning(bool running)
    {
        _running = running;
        ConvertButton.IsEnabled = !running && SelectedOperation is not null && _items.Count > 0;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = running;
        RunProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        RunProgress.IsIndeterminate = running;
        OperationBox.IsEnabled = !running;
        OperationTiles.IsEnabled = !running;
        OptionsPanel.IsEnabled = !running;
        ListActions.IsEnabled = !running;
        DropStrip.IsEnabled = !running;
        MixedBar.IsEnabled = !running;
        FileList.Tag = running ? "running" : null;
        RunSummary.Visibility = !running && RunSummary.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.Content = running ? "Dönüştürülüyor…" : "Dönüştür";
    }

    private void ShowProgress(double? fraction, string status)
    {
        if (!_running) return;
        if (fraction is { } f) { RunProgress.IsIndeterminate = false; RunProgress.Value = f * 100; }
        else RunProgress.IsIndeterminate = true;
        RunProgressStatus(status);
    }

    private void RunProgressStatus(string text) => SetStatus(text, false);

    /// <summary>Called on the worker thread; shows the modal prompt on the UI thread.</summary>
    private string? AskPassword(string fileName, int attempt)
    {
        try
        {
            return Dispatcher.Invoke(() => PasswordWindow.Ask(fileName, attempt > 0));
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "DangerBrush" : "TextSecondaryBrush");
    }

    // ---------- results ----------

    private Brush ThemeBrush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>
    /// Result card: overall title and counts, the combined output (merge / images → PDF) and batch-level errors.
    /// Per-file results are shown on the file rows themselves.
    /// </summary>
    private void ShowResults(BatchOutcome outcome)
    {
        _results.Clear();
        foreach (FileOutcome file in outcome.Files.Where(f => f.Label is not null))
        {
            if (file.Error is { } error)
                _results.Add(new ResultRow("", file.SourceName, error, ThemeBrush("DangerBrush")));
            else if (file.Outputs.Count > 0)
            {
                string detail = Path.GetDirectoryName(file.Outputs[0]) ?? "";
                if (file.Warnings.Count > 0) detail += (detail.Length > 0 ? "\n" : "") + string.Join(" ", file.Warnings);
                _results.Add(new ResultRow(file.Warnings.Count > 0 ? "" : "", Path.GetFileName(file.Outputs[0]) + " · " + file.SourceName, detail,
                    ThemeBrush(file.Warnings.Count > 0 ? "WarningBrush" : "SuccessBrush")));
            }
        }

        _lastOutput = outcome.Outputs.LastOrDefault();
        int written = outcome.Outputs.Count();
        bool failedAll = outcome.Failed > 0 && outcome.Succeeded == 0;
        ResultTitle.Text = outcome.WasCancelled ? "İptal edildi" : outcome.Failed == 0 ? "Tamamlandı" : failedAll ? "Dönüştürülemedi" : "Kısmen tamamlandı";
        string summary = written == 1 ? "1 dosya yazıldı" : $"{written} dosya yazıldı";
        if (outcome.Failed > 0) summary += $" · {outcome.Failed} hatalı";
        if (outcome.WasCancelled) summary += " · iptal edilmeden önce";
        ResultSummary.Text = summary;

        (string glyph, string background, string foreground) = outcome.WasCancelled ? ("", "SurfaceActiveBrush", "TextSecondaryBrush")
            : outcome.Failed == 0 ? ("", "SuccessSubtleBrush", "SuccessBrush")
            : failedAll ? ("", "DangerSubtleBrush", "DangerBrush")
            : ("", "WarningSubtleBrush", "WarningBrush");
        ResultGlyph.Text = glyph;
        ResultIconTile.SetResourceReference(Border.BackgroundProperty, background);
        ResultGlyph.SetResourceReference(TextBlock.ForegroundProperty, foreground);

        SetStatus("", false);
        ResultScroll.Visibility = _results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderButton.Visibility = _lastOutput is null ? Visibility.Collapsed : Visibility.Visible;
        ResultCard.Visibility = outcome.Files.Count > 0 || outcome.WasCancelled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseResults(object sender, RoutedEventArgs e) => ResultCard.Visibility = Visibility.Collapsed;

    private void OpenResultFolder(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is not null) RevealInExplorer(_lastOutput);
    }

    // Open the parent folder through the shell. Explorer's /select syntax has its own quoting
    // rules and ArgumentList can quote the switch together with a path containing spaces.
    internal static ProcessStartInfo? OutputFolderStartInfo(string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        return folder is not null && Directory.Exists(folder)
            ? new ProcessStartInfo(folder) { UseShellExecute = true }
            : null;
    }

    private void RevealInExplorer(string path)
    {
        try
        {
            if (OutputFolderStartInfo(path) is { } info) Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _module.Host.Log("Klasör açılamadı.", ex);
        }
    }
}
