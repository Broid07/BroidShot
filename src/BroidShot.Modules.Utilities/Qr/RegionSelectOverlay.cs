using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BroidShot.Modules.Utilities;

/// <summary>Dimmed desktop; drag a rectangle. Result is the selection in absolute desktop pixels, or null when cancelled.</summary>
internal sealed class RegionSelectOverlay : ScreenOverlayWindow
{
    private readonly TaskCompletionSource<PixelRect?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Path _dim = new();
    private readonly Rectangle _frame = new();
    private readonly string _hintTitle;
    private Border? _hint;
    private Border _size = new();
    private TextBlock _sizeText = new();
    private double _chromeScale = 1;
    private Point _start;
    private bool _dragging;

    /// <param name="hint">What to select, e.g. "QR kodunu içine alacak şekilde sürükle". "Esc: iptal" is added by the overlay.</param>
    public RegionSelectOverlay(ScreenSnapshot snapshot, string hint) : base(snapshot, 0)
    {
        _hintTitle = hint;
        _dim.Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
        _dim.IsHitTestVisible = false;
        _frame.Stroke = OverlayChrome.Accent;
        _frame.StrokeThickness = 2;
        _frame.IsHitTestVisible = false;
        _frame.Visibility = Visibility.Collapsed;
        Surface.Children.Add(_dim);
        Surface.Children.Add(_frame);
        BuildChrome(1);
        SetDim(null);
        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _start = CursorInSnapshot();
            _dragging = true;
            CaptureMouse();
            Update();
        };
        MouseMove += (_, _) => { if (_dragging) Update(); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            e.Handled = true;
            _dragging = false;
            ReleaseMouseCapture();
            Rect rect = Selection();
            if (rect.Width < 8 || rect.Height < 8) { SetDim(null); _frame.Visibility = _size.Visibility = Visibility.Collapsed; return; }
            _result.TrySetResult(new PixelRect(
                Snapshot.Bounds.X + (int)rect.X, Snapshot.Bounds.Y + (int)rect.Y, (int)rect.Width, (int)rect.Height));
            FinishOverlay();
        };
    }

    public Task<PixelRect?> Result => _result.Task;

    protected override void OnShown()
    {
        (int x, int y) = CursorOnDesktop();
        BuildChrome(UiScale, x, y);
    }

    protected override void OnFinished() => _result.TrySetResult(null);

    private void BuildChrome(double scale, int? cursorX = null, int? cursorY = null)
    {
        _chromeScale = scale;
        if (_hint is not null) Surface.Children.Remove(_hint);
        Surface.Children.Remove(_size);
        _hint = OverlayChrome.Hint(scale, _hintTitle, ("Esc", "iptal"));
        Surface.Children.Add(_hint);
        OverlayChrome.PlaceTopCentre(_hint, Snapshot, cursorX ?? Snapshot.Bounds.X, cursorY ?? Snapshot.Bounds.Y, scale);
        _size = OverlayChrome.Pill(scale);
        _sizeText = OverlayChrome.Label("", scale, strong: true);
        _size.Child = _sizeText;
        _size.Visibility = Visibility.Collapsed;
        Surface.Children.Add(_size);
        _frame.StrokeThickness = Math.Round(2 * scale);
    }

    /// <summary>Shows a selection without user input (verification renders the overlay this way).</summary>
    internal void ShowSelection(Rect rect, double chromeScale)
    {
        BuildChrome(chromeScale, Snapshot.Bounds.X + (int)rect.X, Snapshot.Bounds.Y + (int)rect.Y);
        ShowRect(rect);
    }

    private Rect Selection()
    {
        Point end = CursorInSnapshot();
        double left = Math.Clamp(Math.Min(_start.X, end.X), 0, Snapshot.Bounds.Width);
        double top = Math.Clamp(Math.Min(_start.Y, end.Y), 0, Snapshot.Bounds.Height);
        double right = Math.Clamp(Math.Max(_start.X, end.X), 0, Snapshot.Bounds.Width);
        double bottom = Math.Clamp(Math.Max(_start.Y, end.Y), 0, Snapshot.Bounds.Height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private void Update() => ShowRect(Selection());

    private void ShowRect(Rect rect)
    {
        SetDim(rect);
        Canvas.SetLeft(_frame, rect.X);
        Canvas.SetTop(_frame, rect.Y);
        _frame.Width = rect.Width;
        _frame.Height = rect.Height;
        _frame.Visibility = Visibility.Visible;

        // Size read-out above the selection (inside it when there is no room), like the capture overlay.
        _sizeText.Text = string.Create(CultureInfo.InvariantCulture, $"{(int)rect.Width} × {(int)rect.Height}");
        _size.Visibility = Visibility.Visible;   // a collapsed element measures as 0 × 0
        _size.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double gap = Math.Round(8 * _chromeScale);
        double top = rect.Y - _size.DesiredSize.Height - gap;
        if (top < 4) top = rect.Y + gap;
        Canvas.SetLeft(_size, Math.Round(Math.Max(4, rect.X)));
        Canvas.SetTop(_size, Math.Round(top));
        _size.Visibility = rect.Width >= 1 && rect.Height >= 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetDim(Rect? hole)
    {
        var full = new RectangleGeometry(new Rect(0, 0, Snapshot.Bounds.Width, Snapshot.Bounds.Height));
        _dim.Data = hole is Rect r ? new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(r)) : full;
    }
}
