using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BroidShot.Core;

/// <summary>Result of a global shortcut registration. Dispose to unregister.</summary>
public sealed class HotkeyRegistration : IDisposable
{
    private Action? _release;

    internal HotkeyRegistration(HotkeyGesture gesture, bool registered, string? error, Action? release)
    {
        Gesture = gesture;
        IsRegistered = registered;
        Error = error;
        _release = release;
    }

    public HotkeyGesture Gesture { get; }
    public bool IsRegistered { get; private set; }
    /// <summary>Turkish reason when <see cref="IsRegistered"/> is false.</summary>
    public string? Error { get; }

    public static HotkeyRegistration Failed(HotkeyGesture gesture, string error) => new(gesture, false, error, null);

    public void Dispose()
    {
        Action? release = _release;
        _release = null;
        IsRegistered = false;
        release?.Invoke();
    }
}

/// <summary>
/// Registers any number of RegisterHotKey shortcuts on one private message-only window.
/// Create and use on the UI thread. Callbacks run on that dispatcher, outside the window procedure.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, Action> _callbacks = new();
    private HwndSource? _source;
    private int _nextId = 0x5100;
    private bool _disposed;

    public GlobalHotkeyService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        var parameters = new HwndSourceParameters("BroidShot.Hotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WindowMessage);
    }

    public HotkeyRegistration Register(HotkeyGesture gesture, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
        if (gesture.IsEmpty) return HotkeyRegistration.Failed(gesture, "Kısayol tanımlı değil.");
        uint key = gesture.VirtualKey;
        if (key == 0 || key > 0xFE) return HotkeyRegistration.Failed(gesture, "Bu tuş genel kısayol olarak kullanılamaz.");
        int id = _nextId++;
        if (!RegisterHotKey(_source!.Handle, id, gesture.NativeModifiers | ModNoRepeat, key))
        {
            int error = Marshal.GetLastWin32Error();
            string message = error == 1409
                ? gesture.DisplayText + " başka bir uygulama tarafından kullanılıyor."
                : gesture.DisplayText + " kaydedilemedi (Windows hata kodu " + error + ").";
            return HotkeyRegistration.Failed(gesture, message);
        }
        _callbacks[id] = callback;
        return new HotkeyRegistration(gesture, true, null, () => Unregister(id));
    }

    private void Unregister(int id)
    {
        if (!_dispatcher.CheckAccess())
        {
            if (!_dispatcher.HasShutdownStarted) _dispatcher.Invoke(() => Unregister(id));
            return;
        }
        if (_callbacks.Remove(id) && _source is { IsDisposed: false }) UnregisterHotKey(_source.Handle, id);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey || _disposed) return IntPtr.Zero;
        if (_callbacks.TryGetValue(wParam.ToInt32(), out Action? callback))
        {
            handled = true;
            int id = wParam.ToInt32();
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || !_callbacks.ContainsKey(id)) return;
                try { callback(); }
                catch (Exception ex) { ErrorLog.Write("hotkey", "Kısayol işlenemedi", ex); }
            }));
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            if (!_dispatcher.HasShutdownStarted) _dispatcher.Invoke(Dispose);
            return;
        }
        _disposed = true;
        if (_source is { IsDisposed: false } source)
        {
            foreach (int id in _callbacks.Keys) UnregisterHotKey(source.Handle, id);
            source.RemoveHook(WindowMessage);
            source.Dispose();
        }
        _callbacks.Clear();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
