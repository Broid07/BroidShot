using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BroidShot.Shell;

/// <summary>
/// Keeps the native Windows frame (snap layouts, maximize, per-monitor DPI, accessibility all stay
/// native) and makes it dark: immersive dark mode on Windows 10 2004+ and, on Windows 11, a caption
/// and border colour that match the icon rail. Unsupported attributes are ignored by DWM.
/// </summary>
internal static class DarkWindowChrome
{
    private const int DwmwaUseImmersiveDarkModeOld = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    /// <summary>Call from SourceInitialized (the window handle must exist).</summary>
    public static void Apply(Window window, bool tintCaption = true)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        window.Icon ??= BitmapFrame.Create(new Uri("pack://application:,,,/BroidShot;component/Branding/BroidShot.ico"));
        if (handle == IntPtr.Zero) return;
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeOld, ref on, sizeof(int));
            if (!tintCaption) return;
            int caption = ColorRef(window, "RailColor", Color.FromRgb(0x0A, 0x0A, 0x0C));
            int border = ColorRef(window, "StrokeSubtleColor", Color.FromRgb(0x23, 0x23, 0x29));
            int text = ColorRef(window, "TextColor", Color.FromRgb(0xF3, 0xF3, 0xF4));
            DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
            DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private static int ColorRef(FrameworkElement scope, string key, Color fallback)
    {
        Color color = scope.TryFindResource(key) is Color found ? found : fallback;
        return color.R | (color.G << 8) | (color.B << 16);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
