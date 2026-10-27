using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace BroidShot.Modules.Utilities;

public partial class QrView : UserControl
{
    private static readonly int[] Sizes = { 256, 384, 512, 768, 1024, 2048 };
    private readonly QrModule _module;
    private RawImage? _current;
    private bool _loading = true;

    internal QrView(QrModule module)
    {
        _module = module;
        InitializeComponent();
        SizeBox.ItemsSource = Sizes;
        SizeBox.SelectedItem = Sizes.OrderBy(s => Math.Abs(s - module.Settings.Size)).First();
        LevelBox.ItemsSource = Enum.GetValues<QrErrorLevel>().Select(l => new LevelChoice(l)).ToList();
        LevelBox.DisplayMemberPath = nameof(LevelChoice.Name);
        LevelBox.SelectedItem = ((System.Collections.Generic.IEnumerable<LevelChoice>)LevelBox.ItemsSource).First(c => c.Level == module.Settings.ErrorLevel);
        _loading = false;
        _module.Decoded += ShowResult;
        _module.InputRequested += SetInput;
        bool create = false;
        if (_module.PendingInput is { } pending) { InputBox.Text = pending; _module.PendingInput = null; create = true; }
        Generate();
        ShowResult(null, null);
        SelectTab(create);
    }

    private sealed record LevelChoice(QrErrorLevel Level)
    {
        public string Name => QrService.ErrorLevelName(Level);
        public override string ToString() => Name;
    }

    /// <summary>Shows the "Oluştur" tab (true) or the "Oku" tab (false).</summary>
    internal void SelectTab(bool create)
    {
        if (create) CreateTab.IsChecked = true; else ReadTab.IsChecked = true;
        ReadPanel.Visibility = create ? Visibility.Collapsed : Visibility.Visible;
        CreatePanel.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TabChanged(object sender, RoutedEventArgs e)
    {
        if (ReadPanel is null || CreatePanel is null) return;
        SelectTab(ReferenceEquals(sender, CreateTab));
    }

    private void SetInput(string text)
    {
        InputBox.Text = text;
        SelectTab(true);
    }

    private void InputChanged(object sender, TextChangedEventArgs e) => Generate();

    private void OptionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SizeBox.SelectedItem is int size) _module.Settings.Size = size;
        if (LevelBox.SelectedItem is LevelChoice level) _module.Settings.ErrorLevel = level.Level;
        _module.SaveSettings();
        Generate();
    }

    private void Generate()
    {
        if (_loading) return;
        string text = InputBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            _current = null;
            ShowPreview(null, "QR üretmek için bir metin veya bağlantı yaz.");
            return;
        }
        try
        {
            if (text.Length > QrService.MaxTextLength)
                throw new InvalidOperationException($"Metin çok uzun (en fazla {QrService.MaxTextLength} karakter).");
            int size = SizeBox.SelectedItem is int s ? s : 512;
            QrErrorLevel level = LevelBox.SelectedItem is LevelChoice c ? c.Level : QrErrorLevel.Medium;
            _current = QrService.Generate(text, size, level);
            ShowPreview(QrService.ToBitmap(_current), $"{_current.Width} × {_current.Height} px");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _current = null;
            ShowPreview(null, ex.Message);
        }
    }

    /// <summary>Shows the generated code with its size, or the empty frame with a hint or error.</summary>
    private void ShowPreview(BitmapSource? image, string text)
    {
        Preview.Source = image;
        PreviewFrame.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewEmpty.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        InfoText.Text = image is null ? "" : text;
        InfoChip.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyPreviewText.Text = image is null ? text : "";
        SetCreateStatus(null);
    }

    private void SetCreateStatus(string? text)
    {
        CreateStatus.Text = text ?? "";
        CreateStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SavePng(object sender, RoutedEventArgs e)
    {
        if (_current is null) { SetCreateStatus("Önce geçerli bir QR üret."); return; }
        var dialog = new SaveFileDialog { Filter = "PNG görsel (*.png)|*.png", FileName = "qr.png", DefaultExt = ".png", OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            QrService.SavePng(_current, dialog.FileName);
            SetCreateStatus("Kaydedildi: " + dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, "QR kaydedilemedi", ex.Message);
        }
    }

    private void CopyImage(object sender, RoutedEventArgs e)
    {
        if (_current is null) { SetCreateStatus("Önce geçerli bir QR üret."); return; }
        _module.CopyImage(QrService.ToBitmap(_current));
    }

    private void ShowResult(string? text, string? source)
    {
        ResultBox.Text = text ?? "";
        ResultInfo.Text = source is null ? "" : "Kaynak: " + source;
        ResultChip.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        bool has = !string.IsNullOrEmpty(text);
        OpenButton.Visibility = has && IsLink(text!) ? Visibility.Visible : Visibility.Collapsed;
        CopyResultButton.IsEnabled = has;
        ToInputButton.IsEnabled = has;
        if (has && source is not null) SelectTab(false);
    }

    private static bool IsLink(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https";

    private async void ReadFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Görseller|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|Tüm dosyalar|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await _module.ReadFromFileAsync(dialog.FileName);
    }

    private async void ReadClipboard(object sender, RoutedEventArgs e) => await _module.ReadFromClipboardAsync();

    private async void ReadScreen(object sender, RoutedEventArgs e) => await _module.ReadFromScreenAsync(copy: false);

    private void CopyResult(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ResultBox.Text)) _module.CopyText(ResultBox.Text);
    }

    private void OpenResult(object sender, RoutedEventArgs e)
    {
        try { QrModule.TryOpenLink(ResultBox.Text); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Ui.Error(this, "Bağlantı açılamadı", ex.Message);
        }
    }

    private void ResultToInput(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultBox.Text)) return;
        InputBox.Text = ResultBox.Text;
        SelectTab(true);
    }

    private void FileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FileDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await _module.ReadFromFileAsync(files[0]);
    }
}
