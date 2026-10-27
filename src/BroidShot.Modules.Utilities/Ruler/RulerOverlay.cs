using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BroidShot.Modules.Utilities;

/// <summary>Dimmed desktop where a drag measures physical pixels. Shift snaps to horizontal/vertical, Enter copies the result.</summary>
internal sealed class RulerOverlay : ScreenOverlayWindow
{
    private readonly TaskCompletionSource<RulerMeasurement?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Rectangle _box = new();
    private readonly Line _halo = new(), _line = new(), _startTick = new(), _endTick = new();
    private readonly Ellipse _startDot = new(), _endDot = new();
    private Border _label = new();
    private TextBlock _labelText = new();
    private Border? _hint;
    private Point _start, _end;
    private bool _dragging, _hasMeasurement;
    private double _dpiScale = 1;
    private double _chromeScale = 1;

    public RulerOverlay(ScreenSnapshot snapshot) : base(snapshot, 0.28)
    {
        _box.Stroke = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
        _box.StrokeDashArray = new DoubleCollection { 4, 3 };
        _box.StrokeThickness = 1;
        _halo.Stroke = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
        _line.Stroke = OverlayChrome.Accent;
        foreach (Line line in new[] { _halo, _line })
        {
            line.StrokeStartLineCap = line.StrokeEndLineCap = PenLineCap.Round;
        }
        foreach (Line tick in new[] { _startTick, _endTick }) tick.Stroke = Brushes.White;
        foreach (Ellipse dot in new[] { _startDot, _endDot })
        {
            dot.Fill = Brushes.White;
            dot.Stroke = OverlayChrome.Accent;
        }
        foreach (UIElement element in Measured())
        {
            element.IsHitTestVisible = false;
            element.Visibility = Visibility.Collapsed;
            Surface.Children.Add(element);
        }
        BuildChrome(1);
        MouseLeftButtonDown += OnDown;
        MouseMove += (_, _) => OnMove();
        MouseLeftButtonUp += OnUp;
    }

    /// <summary>The measurement the user confirmed with Enter, or null.</summary>
    public Task<RulerMeasurement?> Result => _result.Task;

    protected override void OnFinished() => _result.TrySetResult(null);

    protected override void OnShown()
    {
        (int x, int y) = CursorOnDesktop();
        BuildChrome(UiScale, x, y);
    }

    private UIElement[] Measured() => new UIElement[] { _box, _halo, _line, _startTick, _endTick, _startDot, _endDot };

    /// <summary>(Re)creates the hint bar and the measurement label at the given DPI scale.</summary>
    private void BuildChrome(double scale, int? cursorX = null, int? cursorY = null)
    {
        _chromeScale = scale;
        if (_hint is not null) Surface.Children.Remove(_hint);
        Surface.Children.Remove(_label);

        _hint = OverlayChrome.Hint(scale, "Ölçmek için sürükle", ("Shift", "yatay / dikey"), ("Enter", "kopyala"), ("Esc", "kapat"));
        Surface.Children.Add(_hint);
        OverlayChrome.PlaceTopCentre(_hint, Snapshot, cursorX ?? Snapshot.Bounds.X, cursorY ?? Snapshot.Bounds.Y, scale);

        _label = OverlayChrome.Pill(scale, accent: true);
        _labelText = OverlayChrome.Label("", scale, strong: true);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_labelText);
        row.Children.Add(new Border { Width = 1, Background = OverlayChrome.Edge, Margin = new Thickness(Math.Round(10 * scale), Math.Round(2 * scale), Math.Round(10 * scale), Math.Round(2 * scale)) });
        row.Children.Add(OverlayChrome.Keycap("Enter", scale));
        TextBlock copy = OverlayChrome.Label("kopyala", scale, brush: OverlayChrome.Muted);
        copy.Margin = new Thickness(Math.Round(6 * scale), 0, 0, 0);
        row.Children.Add(copy);
        _label.Child = row;
        _label.Visibility = Visibility.Collapsed;
        Surface.Children.Add(_label);

        _halo.StrokeThickness = Math.Round(4 * scale);
        _line.StrokeThickness = Math.Round(2 * scale);
        _startTick.StrokeThickness = _endTick.StrokeThickness = Math.Max(1, Math.Round(1.5 * scale));
        foreach (Ellipse dot in new[] { _startDot, _endDot })
        {
            dot.Width = dot.Height = Math.Round(7 * scale);
            dot.StrokeThickness = Math.Max(1, Math.Round(1.5 * scale));
        }
        Redraw();
    }

    /// <summary>Shows a measurement without user input (verification renders the overlay this way).</summary>
    internal void ShowMeasurement(int startX, int startY, int endX, int endY, double chromeScale)
    {
        _start = new Point(startX, startY);
        _end = new Point(endX, endY);
        _hasMeasurement = true;
        BuildChrome(chromeScale, startX, startY);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        (int x, int y) = CursorOnDesktop();
        _start = _end = new Point(x, y);
        _dpiScale = Native.GetScaleAt(x, y);
        _dragging = _hasMeasurement = true;
        CaptureMouse();
        Redraw();
    }

    private void OnMove()
    {
        if (!_dragging) return;
        (int x, int y) = CursorOnDesktop();
        _end = new Point(x, y);
        Redraw();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        e.Handled = true;
        OnMove();
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnKey(KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            if (_dragging) OnMove();
            Redraw();
        }
        else if (e.Key == Key.Enter && _hasMeasurement)
        {
            e.Handled = true;
            _result.TrySetResult(Current());
            FinishOverlay();
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key is Key.LeftShift or Key.RightShift) Redraw();
    }

    private bool ShiftDown => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    private RulerMeasurement Current() =>
        RulerMath.Measure((int)_start.X, (int)_start.Y, (int)_end.X, (int)_end.Y, ShiftDown, _dpiScale);

    private void Redraw()
    {
        if (!_hasMeasurement) return;
        double scale = _chromeScale;
        (int ex, int ey) = RulerMath.Snap((int)_start.X, (int)_start.Y, (int)_end.X, (int)_end.Y, ShiftDown);
        double ox = Snapshot.Bounds.X, oy = Snapshot.Bounds.Y;
        // Pixel centres, so a one-pixel measurement is drawn between two distinct pixels.
        Point a = new(_start.X - ox + 0.5, _start.Y - oy + 0.5), b = new(ex - ox + 0.5, ey - oy + 0.5);

        foreach (Line line in new[] { _halo, _line }) { line.X1 = a.X; line.Y1 = a.Y; line.X2 = b.X; line.Y2 = b.Y; }
        // Dashed guide box only when the measurement is diagonal.
        Canvas.SetLeft(_box, Math.Min(a.X, b.X)); Canvas.SetTop(_box, Math.Min(a.Y, b.Y));
        _box.Width = Math.Abs(b.X - a.X); _box.Height = Math.Abs(b.Y - a.Y);

        Vector along = b - a;
        Vector normal = along.Length > .01 ? new Vector(-along.Y / along.Length, along.X / along.Length) : new Vector(0, -1);
        double tick = 7 * scale;
        SetLine(_startTick, a - normal * tick, a + normal * tick);
        SetLine(_endTick, b - normal * tick, b + normal * tick);
        double r = _startDot.Width / 2;
        Canvas.SetLeft(_startDot, a.X - r); Canvas.SetTop(_startDot, a.Y - r);
        Canvas.SetLeft(_endDot, b.X - r); Canvas.SetTop(_endDot, b.Y - r);

        RulerMeasurement m = Current();
        _labelText.Text = m.ToLabel() + (ShiftDown ? "   ⇧ kilitli" : "");
        _label.Visibility = Visibility.Visible;   // a collapsed element measures as 0 × 0
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Beside the middle of the line, on the upper side, far enough that no corner of the label touches the line.
        if (normal.Y > 0 || (normal.Y == 0 && normal.X > 0)) normal = -normal;
        Size size = _label.DesiredSize;
        double clearance = 14 * scale + Math.Abs(normal.X) * size.Width / 2 + Math.Abs(normal.Y) * size.Height / 2;
        Point centre = a + along / 2 + normal * clearance;
        Place(_label, new Point(centre.X - size.Width / 2, centre.Y - size.Height / 2));

        foreach (UIElement element in Measured()) element.Visibility = Visibility.Visible;
        _box.Visibility = _box.Width >= 1 && _box.Height >= 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetLine(Line line, Point from, Point to)
    {
        line.X1 = from.X; line.Y1 = from.Y; line.X2 = to.X; line.Y2 = to.Y;
    }

    private void Place(Border label, Point at)
    {
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = Math.Clamp(at.X, 4, Math.Max(4, Snapshot.Bounds.Width - label.DesiredSize.Width - 4));
        double y = Math.Clamp(at.Y, 4, Math.Max(4, Snapshot.Bounds.Height - label.DesiredSize.Height - 4));
        Canvas.SetLeft(label, Math.Round(x));
        Canvas.SetTop(label, Math.Round(y));
    }
}
