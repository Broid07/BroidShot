using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BroidShot.Capture;
using Forms = System.Windows.Forms;

namespace BroidShot;

internal static class WindowCaptureCanvas
{
    // A captured HWND is placed at its real screen position on a neutral canvas. This avoids
    // stretching the window to a monitor's aspect ratio or exposing any unrelated desktop pixels.
    internal static CaptureSnapshot Place(CaptureSnapshot window)
    {
        var desktop = Forms.SystemInformation.VirtualScreen;
        if (desktop.Width <= 0 || desktop.Height <= 0 || (long)desktop.Width * desktop.Height > 128L * 1024 * 1024)
            throw new InvalidOperationException("Ekran düzeni pencere düzenleyicisi için uygun değil.");
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(9, 9, 9)), null, new Rect(0, 0, desktop.Width, desktop.Height));
            dc.DrawImage(window.Image, new Rect(window.Bounds.X - desktop.X, window.Bounds.Y - desktop.Y, window.Bounds.Width, window.Bounds.Height));
        }
        var image = new RenderTargetBitmap(desktop.Width, desktop.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        var monitors = Forms.Screen.AllScreens.Select(screen => new MonitorInfo(screen.DeviceName,
            new PixelRect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height), screen.Primary)).ToArray();
        return new CaptureSnapshot(image, new PixelRect(desktop.X, desktop.Y, desktop.Width, desktop.Height), monitors);
    }
}
