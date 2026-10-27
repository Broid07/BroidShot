using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BroidShot.Modules.Utilities;

/// <summary>A frozen copy of the whole virtual desktop in physical pixels (BGRA, top-down).</summary>
internal sealed class ScreenSnapshot
{
    private BitmapSource? _image;

    public ScreenSnapshot(PixelRect bounds, byte[] bgra)
    {
        if (bgra.Length < bounds.Width * bounds.Height * 4) throw new ArgumentException("Piksel verisi eksik.", nameof(bgra));
        Bounds = bounds;
        Bgra = bgra;
    }

    public PixelRect Bounds { get; }
    public byte[] Bgra { get; }
    public int Stride => Bounds.Width * 4;

    public static ScreenSnapshot Capture()
    {
        PixelRect bounds = Native.GetVirtualScreen();
        return new ScreenSnapshot(bounds, Native.CaptureBgra(bounds));
    }

    /// <summary>Colour at an absolute desktop pixel, or null outside the snapshot.</summary>
    public Rgb? GetPixel(int screenX, int screenY)
    {
        int x = screenX - Bounds.X, y = screenY - Bounds.Y;
        if (x < 0 || y < 0 || x >= Bounds.Width || y >= Bounds.Height) return null;
        int i = y * Stride + x * 4;
        return new Rgb(Bgra[i + 2], Bgra[i + 1], Bgra[i]);
    }

    public BitmapSource Image => _image ??= CreateImage();

    private BitmapSource CreateImage()
    {
        var bitmap = BitmapSource.Create(Bounds.Width, Bounds.Height, 96, 96, PixelFormats.Bgr32, null, Bgra, Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Copies an absolute desktop rectangle (clamped to the snapshot) into a new BGRA buffer.</summary>
    public byte[]? Crop(PixelRect area, out int width, out int height)
    {
        int left = Math.Max(area.X, Bounds.X), top = Math.Max(area.Y, Bounds.Y);
        int right = Math.Min(area.Right, Bounds.Right), bottom = Math.Min(area.Bottom, Bounds.Bottom);
        width = right - left;
        height = bottom - top;
        if (width <= 0 || height <= 0) return null;
        byte[] result = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(Bgra, (top - Bounds.Y + row) * Stride + (left - Bounds.X) * 4, result, row * width * 4, width * 4);
        return result;
    }
}

/// <summary>
/// Borderless full-desktop window showing a <see cref="ScreenSnapshot"/>. Its content is laid out in physical pixels
/// (the window's DPI scale is divided out), so a Canvas coordinate equals a snapshot pixel on every monitor mix.
/// Pointer positions come from GetCursorPos, which is physical as well.
/// </summary>
internal abstract class ScreenOverlayWindow : Window
{
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Grid _root;
    private int _placements;
    private bool _wasActive;
    private bool _finished;

    protected ScreenOverlayWindow(ScreenSnapshot snapshot, double dimOpacity)
    {
        Snapshot = snapshot;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = snapshot.Bounds.X;
        Top = snapshot.Bounds.Y;
        Width = snapshot.Bounds.Width;
        Height = snapshot.Bounds.Height;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = BroidShot.Core.ThemeResources.UiFontFamily;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        Surface = new Canvas { Width = snapshot.Bounds.Width, Height = snapshot.Bounds.Height, ClipToBounds = true };
        var image = new Image { Source = snapshot.Image, Stretch = Stretch.None, Width = snapshot.Bounds.Width, Height = snapshot.Bounds.Height };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Surface.Children.Add(image);
        if (dimOpacity > 0)
            Surface.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = snapshot.Bounds.Width, Height = snapshot.Bounds.Height,
                Fill = new SolidColorBrush(Color.FromArgb((byte)(dimOpacity * 255), 0, 0, 0)), IsHitTestVisible = false
            });
        _root = new Grid
        {
            Width = snapshot.Bounds.Width, Height = snapshot.Bounds.Height,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            LayoutTransform = _scale
        };
        _root.Children.Add(Surface);
        Content = _root;

        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) => { UpdateScale(); Place(); Activate(); Focus(); OnShown(); };
        DpiChanged += (_, _) => { UpdateScale(); Place(); };
        Activated += (_, _) => _wasActive = true;
        Deactivated += (_, _) => { if (_wasActive) CancelOverlay(); };
        PreviewKeyDown += OnPreviewKeyDown;
        MouseRightButtonDown += (_, e) => { e.Handled = true; CancelOverlay(); };
        Closed += (_, _) => OnClosedCore();
    }

    protected ScreenSnapshot Snapshot { get; }

    /// <summary>Layout surface in physical pixels; add overlay elements here.</summary>
    protected Canvas Surface { get; }

    /// <summary>DPI scale of the window (1.5 at 150 %). Use it to size on-screen furniture so it is legible on scaled monitors.</summary>
    protected double UiScale => Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);

    protected virtual void OnShown() { }
    protected virtual void OnKey(KeyEventArgs e) { }
    protected virtual void OnFinished() { }

    /// <summary>Cursor position in snapshot pixels (origin = top-left of the virtual desktop snapshot).</summary>
    protected Point CursorInSnapshot()
    {
        Native.TryGetCursor(out int x, out int y);
        return new Point(x - Snapshot.Bounds.X, y - Snapshot.Bounds.Y);
    }

    /// <summary>Cursor position in absolute desktop pixels.</summary>
    protected (int X, int Y) CursorOnDesktop()
    {
        Native.TryGetCursor(out int x, out int y);
        return (x, y);
    }

    protected void CancelOverlay() => FinishOverlay();

    /// <summary>Closes the overlay; subclasses complete their result before calling this.</summary>
    protected void FinishOverlay()
    {
        if (_finished) return;
        _finished = true;
        try { Close(); } catch (InvalidOperationException) { }
    }

    private void OnClosedCore()
    {
        _finished = true;
        OnFinished();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelOverlay();
            return;
        }
        OnKey(e);
    }

    private void UpdateScale()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _scale.ScaleX = 1.0 / dpi.DpiScaleX;
        _scale.ScaleY = 1.0 / dpi.DpiScaleY;
        // Keep WPF's idea of the window size equal to the physical desktop size at this DPI (otherwise its layout area can go stale).
        Width = Snapshot.Bounds.Width / dpi.DpiScaleX;
        Height = Snapshot.Bounds.Height / dpi.DpiScaleY;
    }

    private void Place()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || _placements++ > 10) return;
        Native.PlaceTopmost(handle, Snapshot.Bounds);
    }
}
