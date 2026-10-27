using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BroidShot.Core;

namespace BroidShot.Modules.Utilities;

/// <summary>Base class for the utility modules: shared category, host storage and a no-op shutdown.</summary>
public abstract class UtilityModuleBase : IToolModule
{
    private IToolHost? _host;

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    public string Category => ToolCategories.Utilities;
    public abstract string Icon { get; }
    public abstract IReadOnlyList<string> Keywords { get; }
    public abstract IReadOnlyList<ToolCommand> Commands { get; }
    public virtual IReadOnlyList<ToolHotkey> Hotkeys => Array.Empty<ToolHotkey>();

    internal IToolHost Host => _host ?? throw new InvalidOperationException("Modül henüz başlatılmadı.");
    internal bool IsInitialized => _host is not null;

    public virtual void Initialize(IToolHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public abstract FrameworkElement CreateView();

    public virtual void Shutdown() { }

    internal void LogError(string message, Exception ex)
    {
        try { _host?.Log(message, ex); } catch { }
    }

    internal void Toast(string title, string message, ToastKind kind = ToastKind.Info, string? actionLabel = null, Action? action = null)
    {
        try { _host?.ShowToast(new ToastOptions(title, message) { Kind = kind, ActionLabel = actionLabel, Action = action }); } catch { }
    }
}

/// <summary>Clipboard access that can be replaced in tests.</summary>
internal interface IClipboardService
{
    string? GetText();
    void SetText(string text);
    BitmapSource? GetImage();
    void SetImage(BitmapSource image);
}

/// <summary>The system clipboard with retries (another process may hold it open for a few milliseconds).</summary>
internal sealed class SystemClipboard : IClipboardService
{
    public static SystemClipboard Instance { get; } = new();

    public string? GetText() => Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    public void SetText(string text) => Retry<object?>(() => { Clipboard.SetDataObject(text, true); return null; });

    public BitmapSource? GetImage() => Retry(() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null);

    public void SetImage(BitmapSource image) => Retry<object?>(() => { Clipboard.SetImage(image); return null; });

    private static T Retry<T>(Func<T> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (Exception ex) when (ex is COMException or ExternalException && attempt < 6)
            {
                System.Threading.Thread.Sleep(40);
            }
        }
    }
}

/// <summary>Theme/resource and dialog helpers for code-behind.</summary>
internal static class Ui
{
    private static ResourceDictionary? _theme;

    public static T? Resource<T>(string key) where T : class
    {
        if (Application.Current?.TryFindResource(key) is T found) return found;
        try { _theme ??= ThemeResources.Load(); } catch { return null; }
        return _theme.Contains(key) ? _theme[key] as T : null;
    }

    public static Brush Brush(string key, Color fallback) => Resource<Brush>(key) ?? new SolidColorBrush(fallback);

    /// <summary>Theme colour by key (e.g. "OverlayColor"), or the fallback outside the app.</summary>
    public static Color ThemeColor(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Color found) return found;
        try { _theme ??= ThemeResources.Load(); } catch { return fallback; }
        return _theme.Contains(key) && _theme[key] is Color c ? c : fallback;
    }

    /// <summary>
    /// A ListBox laid out at full height inside the page's ScrollViewer still swallows the mouse wheel; pass the wheel on
    /// to the parent so the page keeps scrolling.
    /// </summary>
    public static void BubbleMouseWheel(UIElement element)
    {
        element.PreviewMouseWheel += (sender, e) =>
        {
            if (e.Handled || sender is not UIElement source || VisualTreeHelper.GetParent(source) is not UIElement parent) return;
            e.Handled = true;
            parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = source });
        };
    }

    public static bool Confirm(DependencyObject? owner, string title, string message) =>
        MessageBox.Show(Window.GetWindow(owner) ?? Application.Current?.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static void Error(DependencyObject? owner, string title, string message) =>
        MessageBox.Show(Window.GetWindow(owner) ?? Application.Current?.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>
/// Hides BroidShot's main window while the screen is captured (the way the app's own capture does) and brings it back afterwards.
/// </summary>
internal sealed class MainWindowHider : IDisposable
{
    private Window? _window;

    public static async Task<MainWindowHider> HideAsync()
    {
        var hider = new MainWindowHider();
        Window? main = Application.Current?.MainWindow;
        if (main is { IsVisible: true } && main.WindowState != WindowState.Minimized)
        {
            hider._window = main;
            main.Hide();
            // Let the compositor drop the window before the screen is read.
            await Task.Delay(220);
        }
        return hider;
    }

    public void Dispose()
    {
        Window? window = _window;
        _window = null;
        if (window is null) return;
        try
        {
            window.Show();
            window.Activate();
        }
        catch { }
    }
}

/// <summary>Remembers the last foreground window that is not ours, so a snippet can be pasted back into it.</summary>
internal sealed class ForegroundTracker : IDisposable
{
    private readonly Native.WinEventProc _callback;
    private IntPtr _hook;

    public ForegroundTracker()
    {
        _callback = OnForeground;
        _hook = Native.HookForeground(_callback);
        Remember(Native.GetForeground());
    }

    public IntPtr LastExternal { get; private set; }

    private void OnForeground(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) => Remember(hwnd);

    private void Remember(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && !Native.IsOwnProcess(hwnd)) LastExternal = hwnd;
    }

    public void Dispose()
    {
        Native.Unhook(_hook);
        _hook = IntPtr.Zero;
    }
}

