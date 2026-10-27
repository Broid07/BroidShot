using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BroidShot.Modules.Utilities;

/// <summary>Integer rectangle in physical (device) pixels of the virtual desktop.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
}

/// <summary>Win32 helpers used by the overlays and the snippet paster. Copied/trimmed from the app's capture code (the app project is not referenced).</summary>
internal static class Native
{
    private static readonly IntPtr DpiPerMonitorV2 = new(-4);

    /// <summary>Switches the calling thread to per-monitor-v2 DPI awareness so coordinates are physical pixels.</summary>
    public sealed class PhysicalDpiScope : IDisposable
    {
        private readonly IntPtr _previous = SetThreadDpiAwarenessContext(DpiPerMonitorV2);
        public void Dispose() { if (_previous != IntPtr.Zero) SetThreadDpiAwarenessContext(_previous); }
    }

    public static PixelRect GetVirtualScreen()
    {
        using var scope = new PhysicalDpiScope();
        return new PixelRect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
    }

    public static bool TryGetCursor(out int x, out int y)
    {
        using var scope = new PhysicalDpiScope();
        bool ok = GetCursorPos(out NativePoint p);
        x = p.X; y = p.Y;
        return ok;
    }

    public static void SetCursor(int x, int y)
    {
        using var scope = new PhysicalDpiScope();
        SetCursorPos(x, y);
    }

    /// <summary>DPI scale (1.0 = 96 dpi) of the monitor that contains the physical point; 1.0 when unknown.</summary>
    public static double GetScaleAt(int x, int y)
    {
        try
        {
            using var scope = new PhysicalDpiScope();
            IntPtr monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, 2);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return 1.0;
    }

    /// <summary>Physical bounds of the monitor nearest to the point, or null when unknown.</summary>
    public static PixelRect? GetMonitorBounds(int x, int y)
    {
        try
        {
            using var scope = new PhysicalDpiScope();
            IntPtr monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                return new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    /// <summary>Positions a window with physical coordinates (WPF Left/Top are DIPs and cannot express them on scaled displays).</summary>
    public static bool PlaceTopmost(IntPtr hwnd, PixelRect bounds)
    {
        using var scope = new PhysicalDpiScope();
        // SWP_NOACTIVATE | SWP_SHOWWINDOW
        return SetWindowPos(hwnd, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0040);
    }

    /// <summary>Copies the physical-pixel desktop rectangle into a top-down BGRA32 buffer (alpha forced to 255).</summary>
    public static byte[] CaptureBgra(PixelRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("Yakalanabilecek bir ekran bulunamadı.");
        if ((long)bounds.Width * bounds.Height > 128L * 1024 * 1024)
            throw new InvalidOperationException("Ekranların toplam çözünürlüğü çok büyük.");
        using var scope = new PhysicalDpiScope();
        IntPtr screenDc = IntPtr.Zero, memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw Failure("Ekran görüntüsüne erişilemedi");
            memoryDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero) throw Failure("Yakalama belleği oluşturulamadı");
            previous = SelectObject(memoryDc, bitmap);
            // SRCCOPY | CAPTUREBLT: include layered windows.
            if (!BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, 0x40CC0020))
                throw Failure("Ekran görüntüsü alınamadı");
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = bounds.Width, Height = -bounds.Height,
                    Planes = 1, BitCount = 32, Compression = 0
                }
            };
            byte[] pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
            SelectObject(memoryDc, previous);
            previous = IntPtr.Zero;
            if (GetDIBits(memoryDc, bitmap, 0, (uint)bounds.Height, pixels, ref info, 0) == 0)
                throw Failure("Ekran verisi okunamadı");
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return pixels;
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static Exception Failure(string message) =>
        new InvalidOperationException(message + ".", new Win32Exception(Marshal.GetLastWin32Error()));

    // ---- foreground tracking and paste -------------------------------------------------------------------------

    public static IntPtr GetForeground() => GetForegroundWindow();

    public static bool IsOwnProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    public static bool IsUsableWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd);

    /// <summary>Brings a window of another process to the foreground, working around the foreground lock.</summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (!IsUsableWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        if (GetForegroundWindow() == hwnd) return true;
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            // A synthetic Alt press releases the foreground lock for this thread.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>Sends Ctrl+V to the focused window.</summary>
    public static bool SendPaste()
    {
        var inputs = new[]
        {
            Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input Key(ushort vk, bool up) => new()
    {
        Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, Flags = up ? 2u : 0u } }
    };

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    /// <summary>EVENT_SYSTEM_FOREGROUND hook (out of context; the callback runs on the installing thread's message loop).</summary>
    public static IntPtr HookForeground(WinEventProc callback) => SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, callback, 0, 0, 0x0000);
    public static void Unhook(IntPtr hook) { if (hook != IntPtr.Zero) UnhookWinEvent(hook); }

    // ---- structures and imports --------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[]? Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Union; }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint from, uint to, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
}
