using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace BroidShot.Modules.Clipboard.Native;

/// <summary>
/// WH_KEYBOARD_LL hook on its own thread with a message loop (so a busy UI thread can never make Windows drop the hook).
/// The handler runs inside the hook callback and must be tiny: it returns true to swallow the key.
/// </summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    public delegate bool KeyHandler(uint vkCode, bool keyDown, bool injected);

    private readonly KeyHandler _handler;
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // kept alive for as long as the hook exists
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private string? _error;

    public LowLevelKeyboardHook(KeyHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _proc = HookProc;
    }

    public bool IsRunning => _hook != IntPtr.Zero;

    /// <summary>Installs the hook; returns false (with a Turkish message) when Windows refuses.</summary>
    public bool Start(out string? error)
    {
        error = null;
        if (_thread is not null) return IsRunning;
        _thread = new Thread(Run) { IsBackground = true, Name = "BroidShot.Clipboard.KeyboardHook" };
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(5))) _error ??= "Klavye kancası zamanında başlatılamadı.";
        error = _hook == IntPtr.Zero ? _error ?? "Klavye kancası kurulamadı." : null;
        return error is null;
    }

    private void Run()
    {
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE); // creates this thread's message queue
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) _error = "Klavye kancası kurulamadı: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
        catch (Exception ex)
        {
            _error = "Klavye kancası kurulamadı: " + ex.Message;
        }
        finally
        {
            _started.Set();
        }
        if (_hook == IntPtr.Zero) return;
        try
        {
            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } // the hook is called from inside GetMessage
        }
        finally
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int message = (int)wParam;
                bool keyDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                if (keyDown || message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
                {
                    // KBDLLHOOKSTRUCT: DWORD vkCode, DWORD scanCode, DWORD flags, ...
                    uint vk = (uint)Marshal.ReadInt32(lParam, 0);
                    uint flags = (uint)Marshal.ReadInt32(lParam, 8);
                    bool injected = (flags & (NativeMethods.LLKHF_INJECTED | NativeMethods.LLKHF_LOWER_IL_INJECTED)) != 0;
                    if (_handler(vk, keyDown, injected)) return (IntPtr)1;
                }
            }
            catch (Exception)
            {
                // never let an exception escape into native code; fall through and pass the key on
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Thread? thread = _thread;
        if (thread is null) return;
        _thread = null;
        if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (thread.IsAlive) thread.Join(TimeSpan.FromSeconds(2));
    }
}

/// <summary>
/// Makes Win+V open BroidShot's quick panel instead of Windows' clipboard history. RegisterHotKey cannot claim Win+V (the
/// system owns it), so a low-level keyboard hook swallows V while the Win key is held. Windows opens the Start menu when a
/// Win key is released without any other key in between; since V is hidden from the system, a dummy key (VK 0xE8) is injected
/// while Win is still down, which counts as that "other key". Events from SendInput are ignored, so the dummy key cannot loop.
/// Windows running as administrator do not deliver keys to a non-elevated hook (UIPI); Win+V keeps its default there.
/// </summary>
internal sealed class WinVTakeover : IDisposable
{
    private readonly QuickPanelKeyFilter _filter = new();
    private readonly Action<IntPtr> _open;
    private readonly Func<HookModifiers> _modifiers;
    private readonly Action _suppressStartMenu;
    private readonly Func<IntPtr> _foreground;
    private LowLevelKeyboardHook? _hook;

    /// <param name="open">Called on the hook thread with the window that was in front; must only queue work to the UI thread.</param>
    public WinVTakeover(Action<IntPtr> open) : this(open, ReadModifiers, InjectMaskKey, NativeMethods.GetForegroundWindow) { }

    internal WinVTakeover(Action<IntPtr> open, Func<HookModifiers> modifiers, Action suppressStartMenu, Func<IntPtr> foreground)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _modifiers = modifiers ?? throw new ArgumentNullException(nameof(modifiers));
        _suppressStartMenu = suppressStartMenu ?? throw new ArgumentNullException(nameof(suppressStartMenu));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
    }

    public bool IsRunning => _hook?.IsRunning == true;

    public bool Start(out string? error)
    {
        if (_hook is null)
        {
            _filter.Reset();
            _hook = new LowLevelKeyboardHook(Handle);
        }
        if (_hook.Start(out error)) return true;
        _hook.Dispose();
        _hook = null;
        return false;
    }

    /// <summary>Hook callback body. Returns true to swallow the key.</summary>
    internal bool Handle(uint vkCode, bool keyDown, bool injected)
    {
        HookModifiers modifiers = keyDown && !injected && vkCode == NativeMethods.VK_V ? _modifiers() : HookModifiers.None;
        switch (_filter.Evaluate(vkCode, keyDown, injected, modifiers))
        {
            case HookAction.Swallow:
                return true;
            case HookAction.SwallowAndOpen:
                IntPtr target = _foreground();
                _suppressStartMenu();
                _open(target);
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
    }

    private static HookModifiers ReadModifiers()
    {
        HookModifiers result = HookModifiers.None;
        if (Down(NativeMethods.VK_LWIN) || Down(NativeMethods.VK_RWIN)) result |= HookModifiers.Win;
        if (Down(NativeMethods.VK_CONTROL)) result |= HookModifiers.Ctrl;
        if (Down(NativeMethods.VK_MENU)) result |= HookModifiers.Alt;
        if (Down(NativeMethods.VK_SHIFT)) result |= HookModifiers.Shift;
        return result;
    }

    private static bool Down(ushort vk) => NativeMethods.GetAsyncKeyState(vk) < 0;

    private static void InjectMaskKey()
    {
        var inputs = new NativeMethods.INPUT[2];
        inputs[0] = MaskKey(up: false);
        inputs[1] = MaskKey(up: true);
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT MaskKey(bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_MASK, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 } },
    };
}
