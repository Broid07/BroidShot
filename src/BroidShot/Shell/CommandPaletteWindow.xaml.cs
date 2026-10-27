using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace BroidShot.Shell;

/// <summary>
/// Borderless, topmost search window. Arrow keys move, Enter runs, Escape or losing focus closes.
/// The chosen entry is handed to <c>execute</c> after the window has closed.
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private readonly Func<string, IReadOnlyList<PaletteEntry>> _search;
    private readonly Action<PaletteEntry> _execute;
    private bool _closing;

    public CommandPaletteWindow(Func<string, IReadOnlyList<PaletteEntry>> search, Action<PaletteEntry> execute)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => CloseOnce();
        Loaded += (_, _) =>
        {
            PlaceOnCursorMonitor();
            Activate();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.CaretIndex = SearchBox.Text.Length;
        };
        Refresh();
    }

    public IReadOnlyList<PaletteEntry> Results => (IReadOnlyList<PaletteEntry>?)ResultList.ItemsSource ?? Array.Empty<PaletteEntry>();

    public string Query
    {
        get => SearchBox.Text;
        set
        {
            SearchBox.Text = value ?? "";
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        IReadOnlyList<PaletteEntry> results = _search(SearchBox.Text);
        ResultList.ItemsSource = results;
        ResultList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = results.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (results.Count > 0)
        {
            ResultList.SelectedIndex = 0;
            ResultList.ScrollIntoView(results[0]);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        int count = ResultList.Items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseOnce();
                break;
            case Key.Down when count > 0:
                e.Handled = true;
                ResultList.SelectedIndex = (ResultList.SelectedIndex + 1) % count;
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                break;
            case Key.Up when count > 0:
                e.Handled = true;
                ResultList.SelectedIndex = (ResultList.SelectedIndex - 1 + count) % count;
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                break;
            case Key.PageDown when count > 0:
                e.Handled = true;
                ResultList.SelectedIndex = Math.Min(count - 1, ResultList.SelectedIndex + 6);
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                break;
            case Key.PageUp when count > 0:
                e.Handled = true;
                ResultList.SelectedIndex = Math.Max(0, ResultList.SelectedIndex - 6);
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                break;
            case Key.Enter:
                e.Handled = true;
                RunSelected();
                break;
        }
    }

    private void ResultClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ResultList, source) is ListBoxItem item)
        {
            ResultList.SelectedItem = item.DataContext;
            RunSelected();
        }
    }

    /// <summary>Runs the selected entry (also used by tests).</summary>
    public void RunSelected()
    {
        if (ResultList.SelectedItem is not PaletteEntry entry) return;
        CloseOnce();
        _execute(entry);
    }

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    /// <summary>
    /// Optional anchor in screen pixels: the palette's search row is centred horizontally on X and
    /// its top edge placed at Y (used when it opens from the main window's search field).
    /// Without an anchor it opens on the monitor under the cursor.
    /// </summary>
    public Point? AnchorDevicePoint { get; set; }

    private void PlaceOnCursorMonitor()
    {
        System.Drawing.Point devicePoint = AnchorDevicePoint is { } anchor
            ? new System.Drawing.Point((int)anchor.X, (int)anchor.Y)
            : Forms.Cursor.Position;
        System.Drawing.Rectangle area = Forms.Screen.FromPoint(devicePoint).WorkingArea;
        Matrix fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        Point topLeft = fromDevice.Transform(new Point(area.Left, area.Top));
        Point size = fromDevice.Transform(new Point(area.Width, area.Height));
        double margin = Surface.Margin.Left;
        if (AnchorDevicePoint is { } a)
        {
            Point dip = fromDevice.Transform(a);
            Left = Math.Clamp(dip.X - ActualWidth / 2, topLeft.X, Math.Max(topLeft.X, topLeft.X + size.X - ActualWidth));
            Top = Math.Clamp(dip.Y - margin, topLeft.Y, Math.Max(topLeft.Y, topLeft.Y + size.Y - ActualHeight));
            return;
        }
        Left = topLeft.X + (size.X - ActualWidth) / 2;
        Top = topLeft.Y + size.Y * 0.18;
    }
}
