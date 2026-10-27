using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace BroidShot.Capture;

public readonly record struct PixelRect(int X, int Y, int Width, int Height);
public sealed record MonitorInfo(string Name, PixelRect Bounds, bool IsPrimary);
public sealed record CaptureSnapshot(BitmapSource Image, PixelRect Bounds, IReadOnlyList<MonitorInfo> Monitors);
