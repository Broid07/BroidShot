using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using BroidShot.Core;
using BroidShot.Modules.Clipboard.Native;

namespace BroidShot.Modules.Clipboard;

/// <summary>
/// Small topmost popup near the cursor listing recent items. Enter copies the selection and pastes it into the
/// window that was focused before the panel opened; Ctrl+Enter only copies; Escape or clicking elsewhere closes.
/// </summary>
public partial class QuickPanelWindow : Window
{
    private const int MaxItems = 60;
    private readonly ClipboardModule _module;
    private readonly IntPtr _target;
    private bool _closing;

    internal QuickPanelWindow(ClipboardModule module, IntPtr target)
    {
        _module = module;
        _target = target;
        if (TryFindResource("Card") is null) Resources.MergedDictionaries.Add(ThemeResources.Load());
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => CloseOnce();
        _module.HistoryChanged += OnHistoryChanged;
        Closed += (_, _) => _module.HistoryChanged -= OnHistoryChanged;
        Fill();
    }

    /// <summary>Shows the panel next to the mouse pointer, kept inside the monitor's work area.</summary>
    internal void ShowNearCursor()
    {
        Show();
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor) && NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            NativeMethods.RECT work = NativeMethods.GetMonitorInfo(monitor, ref info) ? info.rcWork : new NativeMethods.RECT { Right = 1920, Bottom = 1080 };
            int x = cursor.X + 12, y = cursor.Y + 12;
            if (x + width > work.Right) x = Math.Max(work.Left, cursor.X - width - 12);
            if (y + height > work.Bottom) y = Math.Max(work.Top, work.Bottom - height);
            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER);
        }
        Activate();
        SearchBox.Focus();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Fill();

    private void Fill()
    {
        long? selected = (List.SelectedItem as ClipItemViewModel)?.Id;
        ClipboardHistory? history = _module.History;
        if (history is null)
        {
            ShowEmpty("Pano geçmişi yükleniyor…");
            return;
        }
        IReadOnlyList<ClipTag> tags = history.Tags;
        Dictionary<long, string> names = tags.ToDictionary(t => t.Id, t => t.Name);
        List<ClipItemViewModel> items = ClipFilter.Apply(history.Items, SearchBox.Text, ClipFilterKind.All, null, tags)
            .Take(MaxItems)
            .Select(e => new ClipItemViewModel(_module, e, string.Join(" #", e.TagIds.Where(names.ContainsKey).Select(id => names[id]))))
            .ToList();
        List.ItemsSource = items;
        List.SelectedItem = items.FirstOrDefault(i => i.Id == selected) ?? items.FirstOrDefault();
        if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem);
        if (items.Count > 0) EmptyState.Visibility = Visibility.Collapsed;
        else ShowEmpty(history.Items.Count == 0 ? "Geçmiş boş. Kopyaladıkların burada görünecek." : "Eşleşen öğe yok.");
        CountText.Text = items.Count == 0 ? "" : items.Count + " öğe";
    }

    private void ShowEmpty(string text)
    {
        EmptyText.Text = text;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        Fill();
        if (List.Items.Count > 0) List.SelectedIndex = 0;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseOnce();
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Up:
            case Key.PageDown:
            case Key.PageUp:
                Move(e.Key switch { Key.Down => 1, Key.Up => -1, Key.PageDown => 6, _ => -6 });
                e.Handled = true;
                break;
            case Key.Enter:
                Choose(pasteIntoTarget: (Keyboard.Modifiers & ModifierKeys.Control) == 0);
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (List.Items.Count == 0) return;
        int index = Math.Clamp((List.SelectedIndex < 0 ? 0 : List.SelectedIndex) + delta, 0, List.Items.Count - 1);
        List.SelectedIndex = index;
        List.ScrollIntoView(List.SelectedItem);
    }

    private void ListDoubleClick(object sender, MouseButtonEventArgs e) => Choose(pasteIntoTarget: true);

    private async void Choose(bool pasteIntoTarget)
    {
        if (List.SelectedItem is not ClipItemViewModel item) return;
        long id = item.Id;
        CloseOnce(); // close first so the target window can take the foreground back
        if (pasteIntoTarget) await _module.PasteAsync(id, _target);
        else _module.CopyToClipboard(id);
    }

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }
}
