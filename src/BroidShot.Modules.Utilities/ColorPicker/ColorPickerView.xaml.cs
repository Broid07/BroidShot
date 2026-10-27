using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BroidShot.Modules.Utilities;

internal sealed class ColorHistoryItem
{
    public ColorHistoryItem(Rgb color)
    {
        Color = color;
        Hex = ColorFormats.ToHex(color);
        RgbText = ColorFormats.ToRgbText(color);
        HslText = ColorFormats.ToHslText(color);
        CSharpText = ColorFormats.ToCSharpText(color);
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        Swatch = brush;
    }

    public Rgb Color { get; }
    public string Hex { get; }
    public string RgbText { get; }
    public string HslText { get; }
    public string CSharpText { get; }
    public Brush Swatch { get; }
}

public partial class ColorPickerView : UserControl
{
    private readonly ColorPickerModule _module;
    private bool _loading;

    internal ColorPickerView(ColorPickerModule module)
    {
        _module = module;
        InitializeComponent();
        _loading = true;
        FormatBox.ItemsSource = ColorFormats.All.Select(f => new FormatChoice(f)).ToList();
        FormatBox.DisplayMemberPath = nameof(FormatChoice.Name);
        _loading = false;
        HotkeyText.Text = "her yerden";
        HotkeyText.ToolTip = "Genel kısayol: Ctrl + Shift + C (Ayarlar → Araçlar bölümünden değiştirilebilir).";
        Ui.BubbleMouseWheel(HistoryList);
        _module.Changed += Refresh;
        Refresh();
    }

    private sealed record FormatChoice(ColorFormat Format)
    {
        public string Name => ColorFormats.Name(Format);
        public override string ToString() => Name;
    }

    private void Refresh()
    {
        _loading = true;
        FormatBox.SelectedItem = ((IEnumerable<FormatChoice>)FormatBox.ItemsSource).First(c => c.Format == _module.Settings.DefaultFormat);
        _loading = false;
        string? previous = (HistoryList.SelectedItem as ColorHistoryItem)?.Hex;
        List<ColorHistoryItem> items = _module.Settings.History
            .Select(h => ColorFormats.TryParseHex(h, out Rgb c) ? new ColorHistoryItem(c) : null)
            .OfType<ColorHistoryItem>().ToList();
        HistoryList.ItemsSource = items;
        HistoryList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryCount.Text = $"{items.Count} / {ColorHistory.Limit}";
        ClearHistoryButton.IsEnabled = items.Count > 0;
        // A new pick becomes the current colour; otherwise the selection the user made is kept while it still exists.
        string? newest = items.FirstOrDefault()?.Hex;
        ColorHistoryItem? keep = items.FirstOrDefault(i => i.Hex == previous);
        HistoryList.SelectedItem = newest == _lastNewest && keep is not null ? keep : items.FirstOrDefault();
        _lastNewest = newest;
        ShowCurrent(HistoryList.SelectedItem as ColorHistoryItem);
    }

    private string? _lastNewest;

    /// <summary>Shows a colour in the large swatch and the copy rows (null = nothing picked yet).</summary>
    private void ShowCurrent(ColorHistoryItem? item)
    {
        DataContext = item;
        bool has = item is not null;
        CurrentSwatch.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoColorSwatch.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        FormatTable.IsEnabled = has;
        RemoveCurrentButton.Visibility = has ? Visibility.Visible : Visibility.Hidden;
    }

    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is ColorHistoryItem item) ShowCurrent(item);
    }

    private void HistoryDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ColorHistoryItem item })
            _module.CopyColor(item.Color, _module.Settings.DefaultFormat);
    }

    private void Pick(object sender, RoutedEventArgs e) => _ = _module.StartPick();

    private void FormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FormatBox.SelectedItem is not FormatChoice choice) return;
        _module.SetDefaultFormat(choice.Format);
    }

    private void ClearHistory(object sender, RoutedEventArgs e)
    {
        if (_module.Settings.History.Count == 0) return;
        if (Ui.Confirm(this, "Renk geçmişi", "Bütün renk geçmişi silinsin mi?")) _module.ClearHistory();
    }

    private void CopyFormat(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ColorHistoryItem item } element) return;
        ColorFormat format = Enum.TryParse(element.Tag as string, out ColorFormat parsed) ? parsed : ColorFormat.Hex;
        _module.CopyColor(item.Color, format);
    }

    private void RemoveItem(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ColorHistoryItem item }) _module.RemoveFromHistory(item.Hex);
    }
}
