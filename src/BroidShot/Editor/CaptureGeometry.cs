using System.Windows;
using BroidShot.Capture;

namespace BroidShot.Editor;

public static class CaptureGeometry
{
    /// <summary>Converts a physical desktop rectangle to snapshot-local pixels, including monitors left/above the origin.</summary>
    public static Rect ToSnapshotSelection(PixelRect desktopBounds, PixelRect selection)
    {
        if (desktopBounds.Width <= 0 || desktopBounds.Height <= 0 || selection.Width <= 0 || selection.Height <= 0) return Rect.Empty;
        return Rect.Intersect(new Rect(selection.X - desktopBounds.X, selection.Y - desktopBounds.Y, selection.Width, selection.Height), new Rect(0, 0, desktopBounds.Width, desktopBounds.Height));
    }
}
