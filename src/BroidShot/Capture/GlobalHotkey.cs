using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BroidShot.Capture;

/// <summary>Registers a global shortcut without replacing another application's registration.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x4253;
    private const int WmHotkey = 0x0312;
    private readonly Window window;
    private readonly Action callback;
    private readonly uint modifiers;
    private readonly uint virtualKey;
    private HwndSource? source;
    private IntPtr handle;
    private bool registered;
    private bool disposed;

    public int LastError { get; private set; }
    public bool IsRegistered => registered;

    public GlobalHotkey(Window window, Action callback, uint modifiers = 0x0006, uint virtualKey = 0x53)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
        if (virtualKey == 0 || virtualKey > 0xFE) throw new ArgumentOutOfRangeException(nameof(virtualKey));
        if ((modifiers & ~0x400Fu) != 0) throw new ArgumentOutOfRangeException(nameof(modifiers));
        this.modifiers = modifiers | 0x4000; // MOD_NOREPEAT
        this.virtualKey = virtualKey;
        window.Closed += OnClosed;
    }

    public bool Register()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        window.Dispatcher.VerifyAccess();
        if (registered) return true;

        handle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("Kısayol için uygulama penceresi hazırlanamadı.");
        if (!RegisterHotKey(handle, HotkeyId, modifiers, virtualKey))
        {
            LastError = Marshal.GetLastWin32Error();
            source = null;
            return false;
        }
        LastError = 0;
        registered = true;
        source.AddHook(WindowMessage);
        return true;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId && registered && !disposed)
        {
            handled = true;
            // Keep exceptions and UI work out of the native window-message callback.
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!disposed && registered) callback();
            }));
        }
        return IntPtr.Zero;
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        if (!window.Dispatcher.CheckAccess())
        {
            if (!window.Dispatcher.HasShutdownStarted)
                window.Dispatcher.Invoke(Dispose);
            return;
        }
        disposed = true;
        window.Closed -= OnClosed;
        if (registered && handle != IntPtr.Zero) UnregisterHotKey(handle, HotkeyId);
        registered = false;
        if (source is not null && !source.IsDisposed) source.RemoveHook(WindowMessage);
        source = null;
        handle = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
