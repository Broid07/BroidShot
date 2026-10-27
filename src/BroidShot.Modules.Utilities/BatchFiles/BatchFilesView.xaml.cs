using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BroidShot.Core;
using Microsoft.Win32;

namespace BroidShot.Modules.Utilities;

public partial class BatchFilesView : UserControl
{
    private sealed record PreviewRow(string OldName, string NewName, string StatusText, Brush StatusBrush);

    private sealed record OrganizeRow(string FileName, string Destination);

    private sealed record ZipRow(string Path, string Name, string Folder, string Glyph);

    private sealed record CaseChoice(RenameCase Case, string Name)
    {
        public override string ToString() => Name;
    }

    private static readonly Brush Good = Ui.Brush("SuccessBrush", Color.FromRgb(74, 201, 141));
    private static readonly Brush Bad = Ui.Brush("DangerBrush", Color.FromRgb(255, 98, 104));
    private static readonly Brush Quiet = Ui.Brush("TextTertiaryBrush", Color.FromRgb(120, 120, 130));

    private readonly BatchFilesModule _module;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly List<string> _renameFiles = new();
    private readonly List<string> _zipItems = new();
    private List<RenameEntry> _entries = new();
    private List<OrganizeEntry> _organize = new();
    private string? _organizeFolder;
    private BatchTab _tab = BatchTab.Rename;
    private bool _busy;

    internal BatchFilesView(BatchFilesModule module)
    {
        _module = module;
        InitializeComponent();
        CaseBox.ItemsSource = new[]
        {
            new CaseChoice(RenameCase.None, "Değiştirme"), new CaseChoice(RenameCase.Lower, "küçük harf (Türkçe)"),
            new CaseChoice(RenameCase.Upper, "BÜYÜK HARF (Türkçe)"), new CaseChoice(RenameCase.Title, "Başlık Düzeni (Türkçe)")
        };
        CaseBox.DisplayMemberPath = nameof(CaseChoice.Name);
        CaseBox.SelectedIndex = 0;
        _debounce.Tick += (_, _) => { _debounce.Stop(); RefreshRenamePreview(); };
        _module.TabRequested += SelectTab;
        _module.JournalChanged += RefreshUndo;
        SelectTab(_module.PendingTab ?? BatchTab.Rename);
        RefreshUndo();
        RefreshRenamePreview();
        RefreshZipList();
        ShowOrganizeEmpty("Önce bir klasör seç");
    }

    // ---- tabs, status, undo ---------------------------------------------------------------------------------------

    private void TabClicked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, out BatchTab tab) && tab != _tab) SelectTab(tab);
    }

    private void SelectTab(BatchTab tab)
    {
        _tab = tab;
        _module.PendingTab = null;
        RenamePanel.Visibility = tab == BatchTab.Rename ? Visibility.Visible : Visibility.Collapsed;
        OrganizePanel.Visibility = tab == BatchTab.Organize ? Visibility.Visible : Visibility.Collapsed;
        ZipPanel.Visibility = tab == BatchTab.Zip ? Visibility.Visible : Visibility.Collapsed;
        TabRename.IsChecked = tab == BatchTab.Rename;
        TabOrganize.IsChecked = tab == BatchTab.Organize;
        TabZip.IsChecked = tab == BatchTab.Zip;
        SetStatus(null);
    }

    private void SetStatus(string? message, bool error = false)
    {
        StatusText.Text = message ?? "";
        StatusBar.Style = Ui.Resource<Style>(error ? "ErrorBar" : "InfoBar");
        StatusIcon.Text = error ? "" : "";
        StatusIcon.Foreground = error ? Bad : Ui.Brush("InfoBrush", Color.FromRgb(125, 168, 255));
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshUndo()
    {
        JournalBatch? last = _module.Journal.Last;
        UndoButton.IsEnabled = last is not null && !_busy;
        UndoText.Text = last is null ? "Geri alınacak işlem yok" : last.Describe();
    }

    private async void UndoLast(object sender, RoutedEventArgs e)
    {
        JournalBatch? last = _module.Journal.Last;
        if (last is null || _busy) return;
        if (!Ui.Confirm(this, "Son işlemi geri al", $"{last.Describe()}\n\nDosyalar eski yerlerine ve adlarına döndürülsün mü?")) return;
        _busy = true;
        try
        {
            UndoResult? result = await Task.Run(() => _module.Journal.UndoLast());
            if (result is null) return;
            string message = $"{result.Restored} dosya geri alındı.";
            if (result.Skipped.Count > 0) message += $" {result.Skipped.Count} dosya atlandı: " + string.Join(" ", result.Skipped.Take(3));
            SetStatus(message, result.Skipped.Count > 0);
            _module.Toast("Geri alındı", $"{result.Restored} dosya eski haline döndü.", result.Skipped.Count > 0 ? ToastKind.Warning : ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Geri alınamadı: " + ex.Message, true);
        }
        finally
        {
            _busy = false;
            RefreshUndo();
            // Paths in the lists may have changed back.
            _renameFiles.RemoveAll(p => !File.Exists(p));
            RefreshRenamePreview();
            if (_organizeFolder is not null) await RefreshOrganizeAsync();
        }
    }

    // ---- rename ---------------------------------------------------------------------------------------------------

    internal void AddRenameFiles(IEnumerable<string> paths)
    {
        var known = new HashSet<string>(_renameFiles, StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
            if (File.Exists(path) && known.Add(path)) _renameFiles.Add(path);
        RefreshRenamePreview();
    }

    private void RenameAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = "Yeniden adlandırılacak dosyaları seç" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddRenameFiles(dialog.FileNames);
    }

    private void RenameAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Dosyaları eklenecek klasörü seç" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            AddRenameFiles(Directory.EnumerateFiles(dialog.FolderName).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("Klasör okunamadı: " + ex.Message, true); }
    }

    private void RenameClear(object sender, RoutedEventArgs e)
    {
        _renameFiles.Clear();
        RefreshRenamePreview();
    }

    private void RenameDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
        var files = new List<string>();
        foreach (string path in dropped)
        {
            if (File.Exists(path)) files.Add(path);
            else if (Directory.Exists(path))
                try { files.AddRange(Directory.EnumerateFiles(path).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)); } catch (IOException) { }
        }
        AddRenameFiles(files);
    }

    private void FileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void RulesChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void RulesChangedSel(object sender, SelectionChangedEventArgs e) => RulesChanged(sender, e);

    private RenameOptions ReadOptions() => new()
    {
        Find = FindBox.Text, Replace = ReplaceBox.Text, UseRegex = RegexBox.IsChecked == true, IgnoreCase = IgnoreCaseBox.IsChecked == true,
        Prefix = PrefixBox.Text, Suffix = SuffixBox.Text, Pattern = PatternBox.Text,
        NumberStart = int.TryParse(StartBox.Text, out int start) ? start : 1,
        NumberStep = int.TryParse(StepBox.Text, out int step) ? step : 1,
        Case = (CaseBox.SelectedItem as CaseChoice)?.Case ?? RenameCase.None,
        NewExtension = ExtBox.Text
    };

    private void RefreshRenamePreview()
    {
        RenameCount.Text = $"{_renameFiles.Count} dosya";
        bool empty = _renameFiles.Count == 0;
        RenameEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RenameList.Visibility = RenameHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        RenameCountChip.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        RenameError.Visibility = Visibility.Collapsed;
        try
        {
            _entries = RenameEngine.Preview(_renameFiles, ReadOptions());
        }
        catch (RenameException ex)
        {
            _entries = new List<RenameEntry>();
            RenameError.Text = ex.Message;
            RenameError.Visibility = Visibility.Visible;
        }
        RenameList.ItemsSource = _entries.Select(ToRow).ToList();
        int ready = _entries.Count(x => x.Status == RenameStatus.Ready);
        int conflicts = _entries.Count(x => x.Status is RenameStatus.Conflict or RenameStatus.Invalid);
        int unchanged = _entries.Count(x => x.Status == RenameStatus.Unchanged);
        RenameSummary.Text = _entries.Count == 0
            ? (_renameFiles.Count == 0 ? "Henüz dosya eklenmedi." : "")
            : $"{ready} dosya yeniden adlandırılacak · {unchanged} değişmiyor · {conflicts} çakışma/hata";
        RenameApply.IsEnabled = ready > 0 && conflicts == 0 && !_busy;
        RenameApply.ToolTip = conflicts > 0 ? "Çakışan veya geçersiz adlar varken uygulanamaz." : null;
    }

    private static PreviewRow ToRow(RenameEntry e) => e.Status switch
    {
        RenameStatus.Ready => new PreviewRow(e.OldName, e.NewName, "Hazır", Good),
        RenameStatus.Unchanged => new PreviewRow(e.OldName, e.NewName, "Değişmiyor", Quiet),
        RenameStatus.Conflict => new PreviewRow(e.OldName, e.NewName, "Çakışma: " + e.Message, Bad),
        _ => new PreviewRow(e.OldName, e.NewName, "Geçersiz: " + e.Message, Bad)
    };

    private async void RenameApplyClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        int ready = _entries.Count(x => x.Status == RenameStatus.Ready);
        if (ready == 0) return;
        if (!Ui.Confirm(this, "Yeniden adlandır", $"{ready} dosyanın adı değiştirilecek. Son işlem daha sonra geri alınabilir.\n\nDevam edilsin mi?")) return;
        _busy = true;
        RenameApply.IsEnabled = false;
        try
        {
            List<RenameEntry> entries = _entries;
            BatchApplyResult result = await Task.Run(() => RenameEngine.Apply(entries, _module.Journal));
            if (result.Error is not null) { SetStatus(result.Error, true); return; }
            SetStatus($"{result.Applied} dosya yeniden adlandırıldı. Gerekirse “Son işlemi geri al” ile döndürebilirsin.");
            _module.Toast("Yeniden adlandırıldı", $"{result.Applied} dosya.", ToastKind.Success);
            // Keep working with the new names.
            for (int i = 0; i < _renameFiles.Count; i++)
            {
                RenameEntry? entry = entries.FirstOrDefault(x => string.Equals(x.SourcePath, _renameFiles[i], StringComparison.OrdinalIgnoreCase));
                if (entry is { Status: RenameStatus.Ready }) _renameFiles[i] = entry.TargetPath;
            }
        }
        finally
        {
            _busy = false;
            RefreshUndo();
            RefreshRenamePreview();
        }
    }

    // ---- organize -------------------------------------------------------------------------------------------------

    private async void OrganizeBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Düzenlenecek klasörü seç" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await LoadOrganizeFolderAsync(dialog.FolderName);
    }

    /// <summary>Selects the folder to organise and previews it.</summary>
    internal async Task LoadOrganizeFolderAsync(string folder)
    {
        _organizeFolder = folder;
        OrganizeFolderBox.Text = folder;
        await RefreshOrganizeAsync();
    }

    private void ShowOrganizeEmpty(string? title)
    {
        OrganizeEmpty.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        OrganizeList.Visibility = OrganizeHeader.Visibility = title is null ? Visibility.Visible : Visibility.Collapsed;
        if (title is not null) OrganizeEmptyTitle.Text = title;
    }

    private async void OrganizeRefresh(object sender, RoutedEventArgs e) => await RefreshOrganizeAsync();

    private async Task RefreshOrganizeAsync()
    {
        OrganizeApply.IsEnabled = false;
        if (_organizeFolder is null) { OrganizeSummary.Text = "Önce bir klasör seç."; ShowOrganizeEmpty("Önce bir klasör seç"); return; }
        try
        {
            string folder = _organizeFolder;
            _organize = await Task.Run(() => OrganizeEngine.Preview(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _organize = new List<OrganizeEntry>();
            SetStatus("Klasör okunamadı: " + ex.Message, true);
        }
        OrganizeList.ItemsSource = _organize
            .Select(x => new OrganizeRow(x.FileName, x.TargetFolderName + (x.Renamed ? "  (ad: " + Path.GetFileName(x.TargetPath) + ")" : "")))
            .ToList();
        if (_organize.Count == 0) OrganizeSummary.Text = "Taşınacak dosya yok (gizli/sistem dosyaları ve alt klasörler atlanır).";
        else
        {
            string parts = string.Join(" · ", FileCategories.All
                .Select(c => (Category: c, Count: _organize.Count(x => x.Category == c))).Where(x => x.Count > 0)
                .Select(x => $"{FileCategories.FolderName(x.Category)}: {x.Count}"));
            OrganizeSummary.Text = $"{_organize.Count} dosya taşınacak — {parts}";
        }
        ShowOrganizeEmpty(_organize.Count == 0 ? "Taşınacak dosya yok" : null);
        OrganizeApply.IsEnabled = _organize.Count > 0 && !_busy;
    }

    private async void OrganizeApplyClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _organizeFolder is null || _organize.Count == 0) return;
        if (!Ui.Confirm(this, "Klasörü düzenle", $"{_organize.Count} dosya “{_organizeFolder}” içindeki alt klasörlere taşınacak. Hiçbir dosya silinmez; son işlem geri alınabilir.\n\nDevam edilsin mi?"))
            return;
        _busy = true;
        OrganizeApply.IsEnabled = false;
        try
        {
            string folder = _organizeFolder;
            List<OrganizeEntry> entries = _organize;
            BatchApplyResult result = await Task.Run(() => OrganizeEngine.Apply(entries, folder, _module.Journal));
            if (result.Error is not null) SetStatus(result.Error, true);
            else
            {
                SetStatus($"{result.Applied} dosya taşındı. Gerekirse “Son işlemi geri al” ile döndürebilirsin.");
                _module.Toast("Klasör düzenlendi", $"{result.Applied} dosya taşındı.", ToastKind.Success);
            }
        }
        finally
        {
            _busy = false;
            RefreshUndo();
            await RefreshOrganizeAsync();
        }
    }

    // ---- zip ------------------------------------------------------------------------------------------------------

    private void RefreshZipList()
    {
        ZipList.ItemsSource = _zipItems.Select(ToZipRow).ToList();
        bool empty = _zipItems.Count == 0;
        ZipEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ZipList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ZipSummary.Text = empty ? "Henüz öğe eklenmedi." : $"{_zipItems.Count} öğe seçili";
        ZipCreate.IsEnabled = _zipItems.Count > 0;
    }

    private static ZipRow ToZipRow(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        bool folder = Directory.Exists(path);
        return new ZipRow(path, Path.GetFileName(trimmed), Path.GetDirectoryName(trimmed) ?? "", folder ? "" : "");
    }

    internal void AddZipItems(IEnumerable<string> paths)
    {
        var known = new HashSet<string>(_zipItems, StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
            if ((File.Exists(path) || Directory.Exists(path)) && known.Add(path)) _zipItems.Add(path);
        RefreshZipList();
    }

    private void ZipAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = "Zip'e eklenecek dosyaları seç" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddZipItems(dialog.FileNames);
    }

    private void ZipAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Zip'e eklenecek klasörü seç" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddZipItems(new[] { dialog.FolderName });
    }

    private void ZipClear(object sender, RoutedEventArgs e)
    {
        _zipItems.Clear();
        RefreshZipList();
    }

    private void ZipDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) AddZipItems(dropped);
    }

    private async void ZipCreateClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _zipItems.Count == 0) return;
        string first = _zipItems[0];
        var dialog = new SaveFileDialog
        {
            Title = "Zip dosyasını kaydet", Filter = "Zip arşivi (*.zip)|*.zip", DefaultExt = ".zip", OverwritePrompt = false,
            InitialDirectory = Path.GetDirectoryName(first) ?? "",
            FileName = (_zipItems.Count == 1 ? Path.GetFileNameWithoutExtension(first.TrimEnd('\\', '/')) : "arsiv") + ".zip"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        // Never overwrite an existing archive: pick a free name instead.
        string zipPath = ArchiveService.UniquePath(dialog.FileName);
        if (!Ui.Confirm(this, "Zip oluştur", $"{_zipItems.Count} öğe şu dosyaya sıkıştırılacak:\n{zipPath}\n\nDevam edilsin mi?")) return;

        List<string> items = _zipItems.ToList();
        await RunArchiveJob("Zip oluşturuluyor", (progress, token) =>
        {
            int count = ArchiveService.CreateZip(items, zipPath, progress, token);
            return $"{count} dosya zip'lendi: {Path.GetFileName(zipPath)}";
        });
    }

    private async void ZipExtractClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Filter = "Zip arşivi (*.zip)|*.zip", CheckFileExists = true, Title = "Açılacak zip dosyasını seç" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string zipPath = dialog.FileName;
        string destination = ArchiveService.UniquePath(Path.Combine(Path.GetDirectoryName(zipPath) ?? "", Path.GetFileNameWithoutExtension(zipPath)));
        if (!Ui.Confirm(this, "Zip aç", $"“{Path.GetFileName(zipPath)}” şu klasöre açılacak:\n{destination}\n\nDevam edilsin mi?")) return;

        await RunArchiveJob("Zip açılıyor", (progress, token) =>
        {
            ExtractResult result = ArchiveService.Extract(zipPath, destination, progress, token);
            return $"{result.Files} dosya açıldı: {result.Destination}";
        });
    }

    private async Task RunArchiveJob(string title, Func<IProgress<double>, System.Threading.CancellationToken, string> work)
    {
        _busy = true;
        RefreshUndo();
        string? message = null, error = null;
        try
        {
            ToolJobState state = await _module.Host.Jobs.RunAsync(title, async (job, token) =>
            {
                var progress = new Progress<double>(p => job.Report(p, $"%{(int)(p * 100)}"));
                try { message = await Task.Run(() => work(progress, token), token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { error = ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException ? ex.Message : "Beklenmeyen hata: " + ex.Message; throw; }
            });
            if (state == ToolJobState.Completed && message is not null)
            {
                SetStatus(message);
                _module.Toast(title, message, ToastKind.Success);
            }
            else if (state == ToolJobState.Cancelled) SetStatus("İptal edildi.");
            else
            {
                SetStatus(error ?? "İşlem başarısız oldu.", true);
                _module.Toast(title, error ?? "İşlem başarısız oldu.", ToastKind.Error);
            }
        }
        finally
        {
            _busy = false;
            RefreshUndo();
        }
    }
}
