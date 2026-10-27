using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BroidShot.Modules.Utilities;

public partial class RulerView : UserControl
{
    private const int RecentLimit = 10;
    private readonly RulerModule _module;

    internal RulerView(RulerModule module)
    {
        _module = module;
        InitializeComponent();
        _module.Changed += Refresh;
        Refresh();
    }

    /// <summary>A copied measurement split for display: "300 × 200 px" and "çapraz 360.56 px".</summary>
    private sealed record RecentRow(string Text, bool IsFirst)
    {
        public string Size => Split(Text).Size;
        public string Detail => Split(Text).Detail;
    }

    internal static (string Size, string Detail) Split(string text)
    {
        int comma = text.IndexOf(", ", System.StringComparison.Ordinal);
        return comma < 0 ? (text, "") : (text[..comma], text[(comma + 2)..]);
    }

    private void Refresh()
    {
        List<string> recent = _module.Settings.Recent;
        RecentList.ItemsSource = recent.Select((t, i) => new RecentRow(t, i == 0)).ToList();
        EmptyText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentCount.Text = $"{recent.Count} / {RecentLimit}";
        ClearButton.IsEnabled = recent.Count > 0;

        string? last = recent.FirstOrDefault();
        LastPanel.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
        LastEmpty.Visibility = last is null ? Visibility.Visible : Visibility.Collapsed;
        CopyLastButton.IsEnabled = last is not null;
        if (last is null) return;
        (string size, string detail) = Split(last);
        LastSize.Text = size.EndsWith(" px", System.StringComparison.Ordinal) ? size[..^3] : size;
        LastDiagonal.Text = detail.Length > 0 ? detail : size;
    }

    private void Start(object sender, RoutedEventArgs e) => _ = _module.StartMeasure();

    private void Clear(object sender, RoutedEventArgs e) => _module.ClearRecent();

    private void CopyLast(object sender, RoutedEventArgs e)
    {
        if (_module.Settings.Recent.FirstOrDefault() is { } last) _module.CopyText(last);
    }

    private void CopyRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentRow row }) _module.CopyText(row.Text);
    }
}
