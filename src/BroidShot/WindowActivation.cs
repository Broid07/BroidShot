using System;
using System.Runtime.InteropServices;

namespace BroidShot;

internal static class WindowActivation
{
    internal static readonly uint RestoreMessage = RegisterWindowMessage("BroidShot.Desktop.Restore.v1");

    internal static bool RequestExistingWindow()
    {
        IntPtr handle = FindWindow(null, "BroidShot");
        if (handle == IntPtr.Zero) return false;
        GetWindowThreadProcessId(handle, out uint processId);
        // The instance launched by the user may grant the existing instance foreground rights.
        // No input synthesis, topmost toggling or foreground-lock settings are used.
        AllowSetForegroundWindow(processId);
        return PostMessage(handle, RestoreMessage, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
