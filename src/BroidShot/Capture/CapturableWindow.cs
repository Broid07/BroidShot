using System;

namespace BroidShot.Capture;

public sealed record CapturableWindow(IntPtr Handle, string Title, PixelRect Bounds)
{
    public override string ToString() => Title;
}
