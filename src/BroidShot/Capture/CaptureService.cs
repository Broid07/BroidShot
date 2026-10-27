using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace BroidShot.Capture;

/// <summary>Physical-pixel SDR desktop capture. No temporary image is written to disk.</summary>
public sealed class CaptureService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SrccopyCaptureblt = 0x40CC0020;
    private const uint MonitorDefaultToNearest = 2;
    private const int DwmExtendedFrameBounds = 9;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr DpiPerMonitorV2 = new(-4);

    /// <summary>Captures the selected window's own rendering without foreground activation.</summary>
    public CaptureSnapshot CaptureWindow(IntPtr handle) => WindowCaptureBackend.Capture(handle);

    public CaptureSnapshot CaptureDesktop()
    {
        using var dpi = new PhysicalDpiScope();
        var bounds = GetDesktopBounds();
        var monitors = GetMonitors();
        if (bounds.Width <= 0 || bounds.Height <= 0 || monitors.Count == 0)
            throw new InvalidOperationException("Yakalanabilecek bir ekran bulunamadı.");
        if ((long)bounds.Width * bounds.Height > 128L * 1024 * 1024)
            throw new InvalidOperationException("Ekranların toplam çözünürlüğü tek alıntı için çok büyük.");

        IntPtr desktopDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previousBitmap = IntPtr.Zero;
        try
        {
            desktopDc = GetDC(IntPtr.Zero);
            if (desktopDc == IntPtr.Zero) throw NativeFailure("Ekran görüntüsüne erişilemedi");
            memoryDc = CreateCompatibleDC(desktopDc);
            if (memoryDc == IntPtr.Zero) throw NativeFailure("Yakalama belleği oluşturulamadı");
            bitmap = CreateCompatibleBitmap(desktopDc, bounds.Width, bounds.Height);
            if (bitmap == IntPtr.Zero) throw NativeFailure("Yakalama görüntüsü oluşturulamadı");
            previousBitmap = SelectObject(memoryDc, bitmap);
            if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
                throw NativeFailure("Yakalama görüntüsü hazırlanamadı");
            if (!BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, desktopDc,
                    bounds.X, bounds.Y, SrccopyCaptureblt))
                throw NativeFailure("Ekran görüntüsü alınamadı");

            BitmapSource image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero,
                Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();

            // A display can be unplugged or rescaled while BitBlt is in progress.
            // Do not return a snapshot with a stale coordinate/monitor map.
            if (bounds != GetDesktopBounds() || !monitors.SequenceEqual(GetMonitors()))
                throw new InvalidOperationException("Ekran düzeni yakalama sırasında değişti. Lütfen yeniden dene.");
            return new CaptureSnapshot(image, bounds, monitors);
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && previousBitmap != IntPtr.Zero && previousBitmap != new IntPtr(-1))
                SelectObject(memoryDc, previousBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (desktopDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, desktopDc);
        }
    }

    public PixelRect? GetForegroundWindowBounds()
    {
        using var dpi = new PhysicalDpiScope();
        IntPtr handle = GetForegroundWindow();
        return GetWindowBounds(handle);
    }

    public IReadOnlyList<CapturableWindow> GetWindows()
    {
        using var dpi = new PhysicalDpiScope();
        var windows = new List<CapturableWindow>();
        EnumWindowsProc callback = (handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle)) return true;
            GetWindowThreadProcessId(handle, out uint processId);
            if (processId == (uint)Environment.ProcessId) return true;
            int titleLength = GetWindowTextLength(handle);
            if (titleLength <= 0) return true;
            // Virtual-desktop windows and shell surfaces may be visible according to user32
            // but not actually rendered by DWM.
            if (DwmGetWindowAttributeInt(handle, 14, out int cloaked, sizeof(int)) >= 0 && cloaked != 0)
                return true;
            PixelRect? bounds = GetWindowBounds(handle);
            if (bounds is null || bounds.Value.Width < 32 || bounds.Value.Height < 32) return true;
            var title = new StringBuilder(Math.Min(titleLength + 1, 4096));
            GetWindowText(handle, title, title.Capacity);
            if (!string.IsNullOrWhiteSpace(title.ToString()))
                windows.Add(new CapturableWindow(handle, title.ToString(), bounds.Value));
            return true;
        };
        if (!EnumWindows(callback, IntPtr.Zero)) throw NativeFailure("Pencere listesi okunamadı");
        return windows;
    }

    /// <summary>
    /// Brings the explicitly selected window forward before taking a desktop crop.
    /// Windows can reject foreground activation; callers must report failure rather than
    /// capture unrelated pixels. Other always-on-top windows can still overlap the target.
    /// Call after hiding BroidShot, then allow the compositor to update before capture.
    /// </summary>
    public bool TryActivateWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle)) return false;
        GetWindowThreadProcessId(handle, out uint processId);
        if (processId == (uint)Environment.ProcessId) return false;
        if (IsIconic(handle) && !ShowWindowAsync(handle, 9)) return false; // SW_RESTORE
        return GetForegroundWindow() == handle || SetForegroundWindow(handle);
    }

    public bool IsWindowForeground(IntPtr handle) => handle != IntPtr.Zero && GetForegroundWindow() == handle;

    public PixelRect? GetWindowBounds(IntPtr handle)
    {
        using var dpi = new PhysicalDpiScope();
        if (handle == IntPtr.Zero || handle == GetDesktopWindow() || handle == GetShellWindow()
            || !IsWindowVisible(handle) || IsIconic(handle)) return null;

        NativeRect rectangle;
        int result = DwmGetWindowAttribute(handle, DwmExtendedFrameBounds, out rectangle,
            Marshal.SizeOf<NativeRect>());
        if (result < 0 && !GetWindowRect(handle, out rectangle)) return null;

        var desktop = GetDesktopBounds();
        int left = Math.Max(rectangle.Left, desktop.X);
        int top = Math.Max(rectangle.Top, desktop.Y);
        int right = Math.Min(rectangle.Right, desktop.X + desktop.Width);
        int bottom = Math.Min(rectangle.Bottom, desktop.Y + desktop.Height);
        return right > left && bottom > top
            ? new PixelRect(left, top, right - left, bottom - top)
            : null;
    }

    public PixelRect GetCursorMonitorBounds()
    {
        using var dpi = new PhysicalDpiScope();
        if (GetCursorPos(out var point))
        {
            IntPtr monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
            var info = NewMonitorInfo();
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                return ToPixelRect(info.Monitor);
        }
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => m.IsPrimary)?.Bounds
            ?? (monitors.Count > 0 ? monitors[0].Bounds : GetDesktopBounds());
    }

    /// <summary>
    /// Places the overlay using native physical coordinates, including negative monitor origins.
    /// WPF Left/Top are DIPs and cannot be assigned these coordinates directly on a scaled display.
    /// Call before Show, or on an already created window.
    /// </summary>
    public static void PositionOverlay(Window window, PixelRect bounds)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
        window.Dispatcher.VerifyAccess();
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        void Position()
        {
            using var dpi = new PhysicalDpiScope();
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero && !SetWindowPos(handle, HwndTopmost, bounds.X, bounds.Y,
                    bounds.Width, bounds.Height, 0x0010 | 0x0200))
                throw NativeFailure("Seçim penceresi konumlandırılamadı");
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Position();
        else
        {
            EventHandler? initialized = null;
            initialized = (_, _) =>
            {
                window.SourceInitialized -= initialized;
                Position();
            };
            window.SourceInitialized += initialized;
        }

        if (!window.IsLoaded)
        {
            RoutedEventHandler? loaded = null;
            loaded = (_, _) =>
            {
                window.Loaded -= loaded;
                // WPF's initial layout can apply a suggested DPI size after SourceInitialized.
                Position();
            };
            window.Loaded += loaded;
        }
    }

    private static PixelRect GetDesktopBounds() => new(GetSystemMetrics(SmXVirtualScreen),
        GetSystemMetrics(SmYVirtualScreen), GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));

    private static List<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        bool failed = false;
        MonitorEnumProc callback = (IntPtr monitor, IntPtr _, ref NativeRect rectangle, IntPtr data) =>
        {
            var info = NewMonitorInfo();
            if (!GetMonitorInfo(monitor, ref info))
            {
                failed = true;
                return false;
            }
            result.Add(new MonitorInfo(info.Device, ToPixelRect(info.Monitor), (info.Flags & 1) != 0));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || failed)
            throw new InvalidOperationException("Monitör bilgileri okunamadı. Ekran bağlantısını kontrol edip yeniden dene.");
        // Native enumeration order is not part of the topology identity.
        return result.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    private static MonitorNativeInfo NewMonitorInfo() => new()
    {
        Size = Marshal.SizeOf<MonitorNativeInfo>(), Device = string.Empty
    };
    private static PixelRect ToPixelRect(NativeRect value) => new(value.Left, value.Top,
        value.Right - value.Left, value.Bottom - value.Top);
    private static Exception NativeFailure(string message) =>
        new InvalidOperationException(message + ".", new Win32Exception(Marshal.GetLastWin32Error()));

    private sealed class PhysicalDpiScope : IDisposable
    {
        private readonly IntPtr previous = SetThreadDpiAwarenessContext(DpiPerMonitorV2);
        public void Dispose()
        {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorNativeInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref NativeRect rectangle, IntPtr data);
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorNativeInfo info);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
