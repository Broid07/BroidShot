using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace BroidShot.Capture;

/// <summary>
/// Captures plain F12 while BroidShot runs. F12 is reserved by RegisterHotKey, so this
/// narrowly scoped low-level hook consumes only its own F12 press/release sequence.
/// </summary>
public sealed class F12CaptureHotkey : IDisposable
{
    private readonly Window window;
    private readonly Action callback;
    private readonly KeyboardHook hookDelegate;
    private readonly F12KeyState state = new();
    private IntPtr hook;
    private bool disposed;

    public int LastError { get; private set; }
    public bool IsRegistered => hook != IntPtr.Zero;

    public F12CaptureHotkey(Window window, Action callback)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
        hookDelegate = HookCallback; // Keep the native callback rooted until unhooking.
        window.Closed += OnClosed;
    }

    public bool Register()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        window.Dispatcher.VerifyAccess();
        if (hook != IntPtr.Zero) return true;
        state.Reset();
        hook = SetWindowsHookEx(13, hookDelegate, GetModuleHandle(null), 0);
        LastError = hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        return hook != IntPtr.Zero;
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || disposed) return CallNextHookEx(hook, code, message, data);
        // Read just vkCode first. Other key events are passed on without processing.
        if (unchecked((uint)Marshal.ReadInt32(data)) != 0x7B)
            return CallNextHookEx(hook, code, message, data);
        int kind = message.ToInt32();
        bool down = kind == 0x0100 || kind == 0x0104;
        bool up = kind == 0x0101 || kind == 0x0105;
        if (!down && !up) return CallNextHookEx(hook, code, message, data);
        int flags = Marshal.ReadInt32(data, 8);
        bool modifiers = (flags & 0x20) != 0 || IsHeld(0x10) || IsHeld(0x11) || IsHeld(0x12) || IsHeld(0x5B) || IsHeld(0x5C);
        var decision = state.Process(down, modifiers);
        if (decision.Trigger && !window.Dispatcher.HasShutdownStarted)
        {
            // No capture, file I/O, text processing or logging happens inside the hook.
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!disposed && hook != IntPtr.Zero) callback();
            }));
        }
        return decision.Consume ? new IntPtr(1) : CallNextHookEx(hook, code, message, data);
    }

    private static bool IsHeld(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        if (!window.Dispatcher.CheckAccess())
        {
            if (!window.Dispatcher.HasShutdownStarted) window.Dispatcher.Invoke(Dispose);
            return;
        }
        disposed = true;
        window.Closed -= OnClosed;
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
        state.Reset();
        GC.KeepAlive(hookDelegate);
    }

    private delegate IntPtr KeyboardHook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hookType, KeyboardHook callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
