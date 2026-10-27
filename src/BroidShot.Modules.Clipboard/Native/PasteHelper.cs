using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace BroidShot.Modules.Clipboard.Native;

/// <summary>Restores a window to the foreground and sends Ctrl+V to it.</summary>
internal static class PasteHelper
{
    public static bool IsOwnWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && NativeMethods.WindowProcessId(hwnd) == (uint)Environment.ProcessId;

    /// <summary>Returns false when the target could not be brought to the foreground (nothing is sent then).</summary>
    public static async Task<bool> PasteIntoAsync(IntPtr target)
    {
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target) || IsOwnWindow(target)) return false;
        if (NativeMethods.IsIconic(target)) NativeMethods.ShowWindow(target, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(target);
        for (int i = 0; i < 15 && NativeMethods.GetForegroundWindow() != target; i++)
        {
            await Task.Delay(20);
            if (i == 5) NativeMethods.SetForegroundWindow(target);
        }
        if (NativeMethods.GetForegroundWindow() != target) return false;
        await Task.Delay(50); // let the target finish activation before the keystrokes arrive
        SendCtrlV();
        return true;
    }

    private static void SendCtrlV()
    {
        var inputs = new List<NativeMethods.INPUT>();
        // Release modifiers that are still held (e.g. Shift/Win from a shortcut) so the target sees plain Ctrl+V.
        foreach (ushort vk in new[] { NativeMethods.VK_SHIFT, NativeMethods.VK_LWIN, NativeMethods.VK_RWIN })
            if (NativeMethods.GetAsyncKeyState(vk) < 0) inputs.Add(Key(vk, up: true));
        inputs.Add(Key(NativeMethods.VK_CONTROL, up: false));
        inputs.Add(Key(NativeMethods.VK_V, up: false));
        inputs.Add(Key(NativeMethods.VK_V, up: true));
        inputs.Add(Key(NativeMethods.VK_CONTROL, up: true));
        NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 } },
    };
}

/// <summary>Remembers the last foreground window of another process, so "Yapıştır" from the page can go back to it.</summary>
internal sealed class ForegroundTracker : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _callback; // kept alive while the hook exists
    private IntPtr _hook;

    public ForegroundTracker()
    {
        _callback = OnForegroundChanged;
        IntPtr current = NativeMethods.GetForegroundWindow();
        if (!PasteHelper.IsOwnWindow(current)) LastExternalWindow = current;
        _hook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    public IntPtr LastExternalWindow { get; private set; }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != IntPtr.Zero && !PasteHelper.IsOwnWindow(hwnd)) LastExternalWindow = hwnd;
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
}
