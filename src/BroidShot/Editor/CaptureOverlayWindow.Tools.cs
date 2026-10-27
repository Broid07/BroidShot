using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BroidShot.Capture;

namespace BroidShot.Editor;

/// <summary>Segoe Fluent Icons glyphs (Segoe MDL2 Assets fallback) used by the overlay tool bar.</summary>
public static class OverlayGlyphs
{
    public const string FontFamilyName = "Segoe Fluent Icons, Segoe MDL2 Assets";
    public const string Capture = "";     // Crop
    public const string ColorPicker = ""; // Color
    public const string Ruler = "";       // Ruler
    public const string Qr = "";          // QRCode
    public const string Ocr = "";         // Font (text)
    public const string Copy = "";
    public const string OpenLink = "";    // OpenInNewWindow
    public const string Close = "";
    public static readonly string[] All = { Capture, ColorPicker, Ruler, Qr, Ocr, Copy, OpenLink, Close };
}

/// <summary>Tool modes inside the capture overlay: colour picker, ruler, QR and OCR on the frozen screenshot.</summary>
public sealed partial class CaptureOverlayWindow
{
    private enum ToastKind { Info, Success, Warning, Error }
    private enum CopyFormat { Hex, Rgb, Hsl }

    private static readonly Color Accent = Color.FromRgb(217, 64, 70);
    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Accent));
    private static readonly Brush PanelBrush = Frozen(new SolidColorBrush(Color.FromArgb(246, 17, 17, 17)));
    private static readonly Brush EdgeBrush = Frozen(new SolidColorBrush(Color.FromRgb(52, 52, 52)));
    private static readonly Brush ButtonBrush = Frozen(new SolidColorBrush(Color.FromRgb(28, 28, 28)));
    private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromRgb(44, 44, 44)));
    private static readonly Brush PressedBrush = Frozen(new SolidColorBrush(Color.FromRgb(58, 30, 32)));
    private static readonly Brush ActiveBrush = Frozen(new SolidColorBrush(Color.FromRgb(62, 26, 29)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(236, 236, 234)));
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(168, 168, 166)));
    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromRgb(120, 120, 118)));
    private static readonly FontFamily IconFont = new(OverlayGlyphs.FontFamilyName);
    private static readonly FontFamily UiFont = BroidShot.Core.ThemeResources.UiFontFamily;
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    private static readonly ControlTemplate FlatButton = CreateFlatTemplate();

    private const string CaptureHint = "BroidShot   ·   Alanı sürükleyerek seç   ·   Esc: iptal";

    private OverlayMode _mode = OverlayMode.Capture;
    private FrozenFrame? _frame;
    private PaintLayer _toolLayer = null!;
    private TextBlock _hintText = null!;
    private Border _hint = null!;
    private Border _modeBar = null!;
    private readonly Dictionary<OverlayMode, Button> _modeButtons = new();
    private Border _toast = null!;
    private TextBlock _toastText = null!;
    private Border _toastBar = null!;
    private DispatcherTimer _toastTimer = null!;
    private Border _panel = null!;
    private Action? _panelPrimary;
    private int _activeMonitor = -1;

    private (int X, int Y)? _probe;
    private bool _loupeVisible;
    private (int X, int Y) _lastPixel;
    private (int X, int Y) _rulerStart;
    private RulerReading? _ruler;
    private bool _rulerDragging;
    private bool _toolRegionDrag;
    private bool _toolRegionStarted;
    private Point _toolDragOrigin;
    private IReadOnlyList<QrFinding> _qrFindings = Array.Empty<QrFinding>();
    private bool _qrBusy;
    private CancellationTokenSource? _ocrCts;
    private bool _ocrBusy;
    private bool _closed;
    private IOverlayTextRecognizer? _recognizer;

    /// <summary>Kept for source compatibility: OCR now runs inside the overlay (O key / OCR button) and this event is never raised.</summary>
    public event Action<System.Windows.Media.Imaging.BitmapSource>? OcrRequested { add { } remove { } }

    /// <summary>Text recogniser for the OCR mode; defaults to the app's offline <see cref="Services.OcrService"/>.</summary>
    public IOverlayTextRecognizer TextRecognizer
    {
        get => _recognizer ??= new OcrServiceRecognizer();
        set => _recognizer = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The active tool mode.</summary>
    public OverlayMode Mode => _mode;

    private FrozenFrame Frame => _frame ??= new FrozenFrame(_snapshot.Image, _snapshot.Bounds);
    private Size SurfaceSize => new(Math.Max(1, _surface.ActualWidth), Math.Max(1, _surface.ActualHeight));
    private bool HasSelection => !_document.Selection.IsEmpty && _document.Selection.Width > 2 && _document.Selection.Height > 2;
    private bool ToolRegionDragging => _toolRegionDrag && _toolRegionStarted;

    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    private void InitializeTools(Grid root, Border hint, TextBlock hintText)
    {
        _hint = hint;
        _hintText = hintText;
        _toolLayer = new PaintLayer(PaintTools) { IsHitTestVisible = false };
        root.Children.Add(_toolLayer);

        _modeBar = BuildModeBar();
        _chrome.Children.Add(_modeBar);

        _toastText = new TextBlock { Foreground = TextBrush, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, VerticalAlignment = VerticalAlignment.Center };
        _toastBar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0), Background = AccentBrush };
        var toastRow = new DockPanel();
        DockPanel.SetDock(_toastBar, Dock.Left);
        toastRow.Children.Add(_toastBar);
        toastRow.Children.Add(_toastText);
        _toast = new Border
        {
            Background = PanelBrush, BorderBrush = EdgeBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 9, 14, 9), Child = toastRow, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = .45 }
        };
        AutomationProperties.SetLiveSetting(_toastText, AutomationLiveSetting.Polite);
        _chrome.Children.Add(_toast);
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Visibility = Visibility.Collapsed; };

        _panel = new Border
        {
            Background = PanelBrush, BorderBrush = EdgeBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 12), Width = 400, Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = .45 }
        };
        _chrome.Children.Add(_panel);

        _surface.MouseLeave += (_, _) => { if (_loupeVisible) { _loupeVisible = false; _toolLayer.InvalidateVisual(); } };
        PreviewKeyUp += OnToolKeyUp;
        Loaded += (_, _) => { PickActiveMonitor(); Refresh(); };
        Closed += (_, _) => { _closed = true; _ocrCts?.Cancel(); _toastTimer.Stop(); };
        UpdateModeVisuals();
    }

    // ---- chrome -------------------------------------------------------------------------------------------------

    private static ControlTemplate CreateFlatTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "Bd" };
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, HoverBrush, "Bd"));
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, PressedBrush, "Bd"));
        template.Triggers.Add(hover);
        template.Triggers.Add(pressed);
        template.Seal();
        return template;
    }

    private static Button FlatButtonFor(object content, string tooltip, Action action, bool primary = false)
    {
        var button = new Button
        {
            Template = FlatButton, Content = content, ToolTip = tooltip, Focusable = false, Cursor = Cursors.Hand,
            Foreground = Brushes.White, Background = primary ? AccentBrush : ButtonBrush,
            BorderBrush = primary ? AccentBrush : EdgeBrush, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(2), MinHeight = 30, FontFamily = UiFont, FontSize = 13
        };
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private static UIElement IconLabel(string glyph, string? text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        if (text != null) row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(7, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private Border BuildModeBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void Add(OverlayMode mode, string glyph, string tooltip)
        {
            var button = FlatButtonFor(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 16 }, tooltip, () => OnModeButton(mode));
            button.Width = 40;
            button.Height = 36;
            button.Padding = new Thickness(0);
            _modeButtons[mode] = button;
            row.Children.Add(button);
        }
        Add(OverlayMode.Capture, OverlayGlyphs.Capture, "Alan seç ve düzenle (araçtan çıkmak için Esc)");
        row.Children.Add(new Border { Width = 1, Margin = new Thickness(5, 7, 5, 7), Background = EdgeBrush });
        Add(OverlayMode.ColorPicker, OverlayGlyphs.ColorPicker, "Renk seçici (I) · tık: HEX, Shift+tık: RGB, Ctrl+tık: HSL kopyalar");
        Add(OverlayMode.Ruler, OverlayGlyphs.Ruler, "Cetvel (M) · sürükleyerek ölç, Shift: yatay/dikey kilit, Enter: kopyala");
        Add(OverlayMode.Qr, OverlayGlyphs.Qr, "QR oku (Q) · seçili alanda, seçim yoksa tüm ekranda");
        Add(OverlayMode.Ocr, OverlayGlyphs.Ocr, "Metin tanı (O) · metni içeren alanı sürükle");
        return new Border
        {
            Background = PanelBrush, BorderBrush = EdgeBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4), Child = row,
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = .4 }
        };
    }

    private void UpdateModeVisuals()
    {
        foreach (var (mode, button) in _modeButtons)
        {
            bool active = mode == _mode;
            button.Background = active ? ActiveBrush : Brushes.Transparent;
            button.BorderBrush = active ? AccentBrush : Brushes.Transparent;
            button.Foreground = active ? Brushes.White : TextBrush;
        }
        _hintText.Text = _mode switch
        {
            OverlayMode.ColorPicker => "Renk seçici   ·   Tık: HEX   ·   Shift+tık: RGB   ·   Ctrl+tık: HSL   ·   Oklar: 1 px   ·   Esc: geri",
            OverlayMode.Ruler => "Cetvel   ·   Sürükleyerek ölç   ·   Shift: yatay/dikey   ·   Enter: kopyala   ·   Esc: geri",
            OverlayMode.Qr => "QR oku   ·   Alan sürükle ya da tıkla (seçim yoksa tüm ekran)   ·   Esc: geri",
            OverlayMode.Ocr => "Metin tanı   ·   Metni içeren alanı sürükle   ·   Esc: geri",
            _ => CaptureHint
        };
    }

    /// <summary>Monitor rectangles in overlay DIPs (whole overlay when the snapshot has no monitor map).</summary>
    private List<Rect> MonitorRects()
    {
        var list = new List<Rect>();
        Size surface = SurfaceSize;
        double sx = surface.Width / Math.Max(1, _snapshot.Image.PixelWidth), sy = surface.Height / Math.Max(1, _snapshot.Image.PixelHeight);
        foreach (MonitorInfo monitor in _snapshot.Monitors)
        {
            var rect = new Rect((monitor.Bounds.X - _snapshot.Bounds.X) * sx, (monitor.Bounds.Y - _snapshot.Bounds.Y) * sy, monitor.Bounds.Width * sx, monitor.Bounds.Height * sy);
            rect.Intersect(new Rect(surface));
            if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0) list.Add(rect);
        }
        return list;
    }

    private Rect MonitorRectAt(Point point)
    {
        var rects = MonitorRects();
        foreach (Rect rect in rects) if (rect.Contains(point)) return rect;
        return rects.Count > 0 ? rects.OrderBy(r => Distance(r, point)).First() : new Rect(SurfaceSize);
    }

    private static double Distance(Rect rect, Point p)
    {
        double dx = Math.Max(Math.Max(rect.Left - p.X, 0), p.X - rect.Right), dy = Math.Max(Math.Max(rect.Top - p.Y, 0), p.Y - rect.Bottom);
        return dx * dx + dy * dy;
    }

    private void PickActiveMonitor()
    {
        _activeMonitor = -1;
        if (_snapshot.Monitors.Count == 0) return;
        Point mouse = Mouse.GetPosition(_surface);
        var rects = MonitorRects();
        int index = rects.FindIndex(r => r.Contains(mouse));
        if (index < 0) index = _snapshot.Monitors.ToList().FindIndex(m => m.IsPrimary);
        _activeMonitor = index;
    }

    private Rect ActiveMonitorRect()
    {
        var rects = MonitorRects();
        if (_activeMonitor >= 0 && _activeMonitor < rects.Count) return rects[_activeMonitor];
        int primary = _snapshot.Monitors.ToList().FindIndex(m => m.IsPrimary);
        return primary >= 0 && primary < rects.Count ? rects[primary] : new Rect(SurfaceSize);
    }

    private void PositionToolChrome()
    {
        if (_modeBar == null) return;
        Rect monitor = ActiveMonitorRect();
        _modeBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size bar = _modeBar.DesiredSize;
        double left = Math.Round(monitor.Left + (monitor.Width - bar.Width) / 2), top = Math.Round(monitor.Top + 22);
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var hintRect = new Rect(Canvas.GetLeft(_hint), Canvas.GetTop(_hint), _hint.DesiredSize.Width, _hint.DesiredSize.Height);
        if (new Rect(left, top, bar.Width, bar.Height).IntersectsWith(hintRect)) top = Math.Round(hintRect.Bottom + 10);
        Canvas.SetLeft(_modeBar, left);
        Canvas.SetTop(_modeBar, top);

        if (_panel.Visibility == Visibility.Visible)
        {
            _panel.Width = Math.Min(400, Math.Max(240, monitor.Width - 24));
            _panel.Measure(new Size(_panel.Width, double.PositiveInfinity));
            double panelLeft = Math.Clamp(Math.Round(left + bar.Width / 2 - _panel.Width / 2), monitor.Left + 12, Math.Max(monitor.Left + 12, monitor.Right - _panel.Width - 12));
            Canvas.SetLeft(_panel, panelLeft);
            Canvas.SetTop(_panel, Math.Round(top + bar.Height + 10));
        }
        if (_toast.Visibility == Visibility.Visible)
        {
            _toast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = _toast.DesiredSize;
            Canvas.SetLeft(_toast, Math.Round(monitor.Left + (monitor.Width - size.Width) / 2));
            Canvas.SetTop(_toast, Math.Round(monitor.Bottom - size.Height - 36));
        }
    }

    private void ShowToast(string message, ToastKind kind = ToastKind.Info, bool sticky = false)
    {
        if (_closed) return;
        _toastText.Text = message;
        _toastBar.Background = kind switch
        {
            ToastKind.Success => new SolidColorBrush(Color.FromRgb(61, 214, 175)),
            ToastKind.Warning => Brushes.Gold,
            ToastKind.Error => AccentBrush,
            _ => MutedBrush
        };
        _toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        if (!sticky) _toastTimer.Start();
        PositionToolChrome();
    }

    private void ShowPanel(string title, UIElement content, Action? primary)
    {
        _panelPrimary = primary;
        var stack = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var close = FlatButtonFor(new TextBlock { Text = OverlayGlyphs.Close, FontFamily = IconFont, FontSize = 11 }, "Paneli kapat (Esc)", HidePanel);
        close.Background = Brushes.Transparent;
        close.BorderBrush = Brushes.Transparent;
        close.MinHeight = 26;
        close.Padding = new Thickness(7, 4, 7, 4);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = title, Foreground = TextBrush, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(header);
        stack.Children.Add(content);
        _panel.Child = stack;
        _panel.Visibility = Visibility.Visible;
        PositionToolChrome();
    }

    private void HidePanel()
    {
        _panel.Visibility = Visibility.Collapsed;
        _panel.Child = null;
        _panelPrimary = null;
        if (IsLoaded) Focus();
    }

    // ---- modes --------------------------------------------------------------------------------------------------

    private void OnModeButton(OverlayMode mode)
    {
        // Clicking the active QR/OCR button runs it again on the current selection.
        if (mode == _mode && mode is OverlayMode.Qr or OverlayMode.Ocr) { RunModeAction(); return; }
        SetMode(mode);
    }

    private void SetMode(OverlayMode mode)
    {
        if (mode == _mode) return;
        CancelToolDrag();
        if (_mode == OverlayMode.Ocr) { _ocrCts?.Cancel(); }
        _mode = mode;
        _ruler = null;
        _qrFindings = Array.Empty<QrFinding>();
        _loupeVisible = false;
        _probe = null;
        HidePanel();
        if (mode == OverlayMode.ColorPicker && _surface.IsMouseOver)
        {
            _probe = _lastPixel = PixelAt(Mouse.GetPosition(_surface));
            _loupeVisible = true;
        }
        if (mode != OverlayMode.Capture) _document.SelectedId = null;
        UpdateModeVisuals();
        Refresh();
        if (mode is OverlayMode.Qr or OverlayMode.Ocr) RunModeAction();
    }

    private void RunModeAction()
    {
        if (_mode == OverlayMode.Qr) _ = RunQrAsync();
        else if (_mode == OverlayMode.Ocr)
        {
            if (HasSelection) _ = RunOcrAsync();
            else ShowToast("Metni tanımak için metni içeren alanı sürükle.");
        }
    }

    private void CancelToolDrag()
    {
        if (_rulerDragging || _toolRegionDrag) _surface.ReleaseMouseCapture();
        _rulerDragging = false;
        _toolRegionDrag = false;
        _toolRegionStarted = false;
    }

    private (int X, int Y) PixelAt(Point surfacePoint) => FrozenFrame.SurfaceToPixel(surfacePoint, SurfaceSize, _snapshot.Image.PixelWidth, _snapshot.Image.PixelHeight);

    /// <summary>Called first by the pointer-down handler; true when a tool mode consumed the event.</summary>
    private bool ToolPointerDown(MouseButtonEventArgs e)
    {
        if (_mode == OverlayMode.Capture) return false;
        Focus();
        Point position = e.GetPosition(_surface);
        var pixel = _lastPixel = PixelAt(position);
        e.Handled = true;
        switch (_mode)
        {
            case OverlayMode.ColorPicker:
                _probe = pixel;
                _loupeVisible = true;
                var modifiers = Keyboard.Modifiers;
                PickColor((modifiers & ModifierKeys.Control) != 0 ? CopyFormat.Hsl : (modifiers & ModifierKeys.Shift) != 0 ? CopyFormat.Rgb : CopyFormat.Hex);
                break;
            case OverlayMode.Ruler:
                _rulerStart = pixel;
                _ruler = OverlayRuler.Measure(pixel.X, pixel.Y, pixel.X, pixel.Y, false);
                _rulerDragging = true;
                _surface.CaptureMouse();
                _toolLayer.InvalidateVisual();
                break;
            default:
                _toolDragOrigin = PixelPoint(e);
                _toolRegionDrag = true;
                _toolRegionStarted = false;
                _surface.CaptureMouse();
                break;
        }
        return true;
    }

    private bool ToolPointerMove(MouseEventArgs e)
    {
        if (_mode == OverlayMode.Capture) return false;
        var pixel = _lastPixel = PixelAt(e.GetPosition(_surface));
        switch (_mode)
        {
            case OverlayMode.ColorPicker:
                _probe = pixel;
                _loupeVisible = true;
                _toolLayer.InvalidateVisual();
                break;
            case OverlayMode.Ruler:
                if (_rulerDragging && e.LeftButton == MouseButtonState.Pressed) UpdateRuler();
                break;
            default:
                if (!_toolRegionDrag || e.LeftButton != MouseButtonState.Pressed) break;
                Point point = PixelPoint(e);
                if (!_toolRegionStarted)
                {
                    if ((point - _toolDragOrigin).Length < 3) break;
                    _document.Checkpoint();
                    _toolRegionStarted = true;
                    HidePanel();
                }
                _document.Selection = new Rect(_toolDragOrigin, point);
                Refresh();
                break;
        }
        return true;
    }

    private bool ToolPointerUp(MouseButtonEventArgs e)
    {
        if (_mode == OverlayMode.Capture) return false;
        e.Handled = true;
        if (_rulerDragging)
        {
            _rulerDragging = false;
            _surface.ReleaseMouseCapture();
            UpdateRuler();
            if (_ruler is RulerReading r && r.Dx == 0 && r.Dy == 0) _ruler = null;
            else if (_ruler is RulerReading reading) ShowToast(reading.Label + "   ·   Enter: kopyala");
            _toolLayer.InvalidateVisual();
        }
        else if (_toolRegionDrag)
        {
            bool started = _toolRegionStarted;
            _toolRegionDrag = false;
            _toolRegionStarted = false;
            _surface.ReleaseMouseCapture();
            if (started)
                _document.Selection = AnnotationRenderer.PixelSelection(_document.Selection, _snapshot.Image.PixelWidth, _snapshot.Image.PixelHeight);
            Refresh();
            if (started && !HasSelection) ShowToast("Seçim çok küçük.", ToastKind.Warning);
            else RunModeAction();
        }
        return true;
    }

    private void UpdateRuler()
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        _ruler = OverlayRuler.Measure(_rulerStart.X, _rulerStart.Y, _lastPixel.X, _lastPixel.Y, shift);
        _toolLayer.InvalidateVisual();
    }

    private void OnToolKeyUp(object sender, KeyEventArgs e)
    {
        if (_rulerDragging && e.Key is Key.LeftShift or Key.RightShift) UpdateRuler();
    }

    /// <summary>Called first by the key handler; true when the key belongs to the tool modes (or to a focused text box).</summary>
    private bool ToolKeyDown(KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            // The OCR result box keeps its own Ctrl+C / Ctrl+A / arrows; Esc closes the panel.
            if (e.Key == Key.Escape) { HidePanel(); e.Handled = true; }
            return true;
        }
        if (e.Key == Key.Escape)
        {
            if (_panel.Visibility == Visibility.Visible) { HidePanel(); e.Handled = true; return true; }
            if (_mode != OverlayMode.Capture) { SetMode(OverlayMode.Capture); e.Handled = true; return true; }
            return false;
        }
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (OverlayShortcuts.ModeForKey(e.Key, modifiers) is OverlayMode requested)
        {
            SetMode(OverlayShortcuts.Toggle(_mode, requested));
            e.Handled = true;
            return true;
        }
        if ((modifiers & ModifierKeys.Control) != 0) return false;
        if (_rulerDragging && e.Key is Key.LeftShift or Key.RightShift) { UpdateRuler(); return true; }
        if (_mode == OverlayMode.ColorPicker)
        {
            int step = (modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            (int dx, int dy) = e.Key switch { Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0) };
            if (dx != 0 || dy != 0)
            {
                var current = _probe ?? (_snapshot.Image.PixelWidth / 2, _snapshot.Image.PixelHeight / 2);
                _probe = (Math.Clamp(current.X + dx, 0, _snapshot.Image.PixelWidth - 1), Math.Clamp(current.Y + dy, 0, _snapshot.Image.PixelHeight - 1));
                _loupeVisible = true;
                _toolLayer.InvalidateVisual();
                e.Handled = true;
                return true;
            }
            CopyFormat? format = e.Key switch
            {
                Key.Enter or Key.Space or Key.D1 or Key.NumPad1 => CopyFormat.Hex,
                Key.D2 or Key.NumPad2 => CopyFormat.Rgb,
                Key.D3 or Key.NumPad3 => CopyFormat.Hsl,
                _ => null
            };
            if (format is CopyFormat f && _probe != null) { PickColor(f); e.Handled = true; return true; }
        }
        if (e.Key == Key.Enter)
        {
            if (_panelPrimary != null) { _panelPrimary(); e.Handled = true; return true; }
            if (_mode == OverlayMode.Ruler && _ruler is RulerReading reading)
            {
                CopyText(reading.ClipboardText, "Ölçüm kopyalandı: " + reading.ClipboardText);
                e.Handled = true;
                return true;
            }
        }
        return false;
    }

    // ---- actions ------------------------------------------------------------------------------------------------

    private bool CopyText(string text, string successMessage)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                ShowToast(successMessage, ToastKind.Success);
                return true;
            }
            catch (ExternalException) { Thread.Sleep(30 * (attempt + 1)); }
        }
        ShowToast("Pano başka bir uygulama tarafından kullanılıyor. Tekrar dene.", ToastKind.Error);
        return false;
    }

    private void PickColor(CopyFormat format)
    {
        if (_probe is not (int x, int y) || Frame.GetPixel(x, y) is not PixelColor color) return;
        string text = format switch { CopyFormat.Rgb => color.RgbText, CopyFormat.Hsl => color.HslText, _ => color.Hex };
        bool history = color.AddToColorHistory();
        ShowColorPanel(color, x, y);
        CopyText(text, text + " kopyalandı" + (history ? "   ·   renk geçmişine eklendi" : ""));
        _toolLayer.InvalidateVisual();
    }

    private void ShowColorPanel(PixelColor color, int x, int y)
    {
        var content = new StackPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var swatch = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(color.ToColor()), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(swatch, Dock.Left);
        top.Children.Add(swatch);
        (int dx, int dy) = Frame.ToDesktop(x, y);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = color.Hex, Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold, FontFamily = MonoFont });
        info.Children.Add(new TextBlock { Text = string.Create(CultureInfo.InvariantCulture, $"Konum  X {dx}   Y {dy}  (fiziksel piksel)"), Foreground = MutedBrush, FontSize = 12 });
        top.Children.Add(info);
        content.Children.Add(top);
        foreach (var (name, value) in new[] { ("HEX", color.Hex), ("RGB", color.RgbText), ("HSL", color.HslText) })
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var copy = FlatButtonFor(IconLabel(OverlayGlyphs.Copy, "Kopyala"), name + " değerini kopyala", () => CopyText(value, value + " kopyalandı"));
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(copy);
            row.Children.Add(new TextBlock { Text = name, Width = 42, Foreground = DimBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = value, Foreground = TextBrush, FontFamily = MonoFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(row);
        }
        ShowPanel("Seçilen renk", content, () => CopyText(color.Hex, color.Hex + " kopyalandı"));
    }

    private async Task RunQrAsync()
    {
        if (_qrBusy) return;
        Rect? region = HasSelection ? _document.Selection : null;
        OverlayQr.Input input;
        try { input = OverlayQr.Prepare(Frame, region, _document.Items); }
        catch (Exception ex) { ShowToast("QR okunamadı: " + ex.Message, ToastKind.Error); return; }
        _qrBusy = true;
        ShowToast(region is null ? "Tüm ekranda QR kodu aranıyor…" : "Seçili alanda QR kodu aranıyor…", sticky: true);
        try
        {
            IReadOnlyList<QrFinding> findings = await Task.Run(() => OverlayQr.Decode(input));
            if (_closed || _mode != OverlayMode.Qr) return;
            _qrFindings = findings;
            _toolLayer.InvalidateVisual();
            if (findings.Count == 0)
            {
                HidePanel();
                ShowToast(region is null ? "Ekranda QR kodu bulunamadı." : "Seçili alanda QR kodu bulunamadı.", ToastKind.Warning);
                return;
            }
            ShowQrPanel(findings);
            ShowToast(findings.Count == 1 ? "QR kodu okundu   ·   Enter: kopyala" : $"{findings.Count} QR kodu okundu   ·   Enter: ilkini kopyala", ToastKind.Success);
        }
        catch (Exception ex) { if (!_closed) ShowToast("QR okunamadı: " + ex.Message, ToastKind.Error); }
        finally { _qrBusy = false; }
    }

    private void ShowQrPanel(IReadOnlyList<QrFinding> findings)
    {
        var content = new StackPanel();
        for (int i = 0; i < findings.Count; i++)
        {
            QrFinding finding = findings[i];
            var card = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 0) };
            if (findings.Count > 1) card.Children.Add(new TextBlock { Text = $"QR {i + 1}", Foreground = DimBrush, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            card.Children.Add(new TextBlock
            {
                Text = finding.Text, Foreground = TextBrush, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxHeight = 76,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = finding.Text.Length > 120 ? finding.Text : null
            });
            var buttons = new WrapPanel { Margin = new Thickness(-2, 6, 0, 0) };
            string text = finding.Text;
            buttons.Children.Add(FlatButtonFor(IconLabel(OverlayGlyphs.Copy, "Kopyala"), "QR metnini kopyala", () => CopyText(text, "QR metni kopyalandı"), primary: i == 0));
            if (finding.Link is Uri link)
                buttons.Children.Add(FlatButtonFor(IconLabel(OverlayGlyphs.OpenLink, "Bağlantıyı aç"), "Tarayıcıda aç: " + link.AbsoluteUri, () => OpenLink(link)));
            card.Children.Add(buttons);
            content.Children.Add(card);
        }
        string first = findings[0].Text;
        ShowPanel(findings.Count == 1 ? "QR kodu" : $"{findings.Count} QR kodu", content, () => CopyText(first, "QR metni kopyalandı"));
    }

    private void OpenLink(Uri link)
    {
        // Only http/https links reach here (OverlayQr.TryGetWebLink) and only from an explicit button click.
        try
        {
            Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
            // The overlay is topmost; step aside so the browser is visible. The capture stays open in the taskbar.
            WindowState = WindowState.Minimized;
        }
        catch (Exception ex) { ShowToast("Bağlantı açılamadı: " + ex.Message, ToastKind.Error); }
    }

    private async Task RunOcrAsync()
    {
        if (_ocrBusy) { ShowToast("Metin tanıma sürüyor…", sticky: true); return; }
        IOverlayTextRecognizer recognizer = TextRecognizer;
        if (!recognizer.IsAvailable) { ShowToast("Metin tanıma kullanılamıyor: OCR dil dosyaları (tessdata) bulunamadı.", ToastKind.Error); return; }
        _ocrBusy = true;
        var cts = new CancellationTokenSource();
        _ocrCts = cts;
        HidePanel();
        ShowToast($"Metin tanınıyor… ({recognizer.LanguageLabel})", sticky: true);
        try
        {
            string text = await OverlayOcr.RecognizeAsync(recognizer, Frame, _document.Selection, _document.Items, cts.Token);
            if (_closed || cts.IsCancellationRequested) return;
            if (text.Length == 0) { ShowToast("Bu alanda metin bulunamadı.", ToastKind.Warning); return; }
            ShowOcrPanel(text);
            ShowToast("Metin tanındı   ·   Enter: kopyala", ToastKind.Success);
        }
        catch (OperationCanceledException) { if (!_closed) _toast.Visibility = Visibility.Collapsed; }
        catch (Exception ex) { if (!_closed) ShowToast("Metin tanınamadı: " + ex.Message, ToastKind.Error); }
        finally
        {
            _ocrBusy = false;
            if (ReferenceEquals(_ocrCts, cts)) _ocrCts = null;
            cts.Dispose();
        }
    }

    private void ShowOcrPanel(string text)
    {
        var content = new StackPanel();
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 64, MaxHeight = 260, FontSize = 13, FontFamily = UiFont, Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 24)), Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderBrush = EdgeBrush, BorderThickness = new Thickness(1), SelectionBrush = AccentBrush
        };
        AutomationProperties.SetName(box, "Tanınan metin");
        content.Children.Add(box);
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var copy = FlatButtonFor(IconLabel(OverlayGlyphs.Copy, "Kopyala"), "Tanınan metni kopyala (Enter)", () => CopyText(text, "Metin kopyalandı"), primary: true);
        DockPanel.SetDock(copy, Dock.Right);
        footer.Children.Add(copy);
        int lines = text.Split('\n').Length;
        footer.Children.Add(new TextBlock { Text = $"{text.Length} karakter · {lines} satır", Foreground = DimBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(footer);
        ShowPanel("Tanınan metin", content, () => CopyText(text, "Metin kopyalandı"));
    }

    // ---- painting -----------------------------------------------------------------------------------------------

    private sealed class PaintLayer : FrameworkElement
    {
        private readonly Action<DrawingContext> _paint;
        public PaintLayer(Action<DrawingContext> paint) { _paint = paint; }
        protected override void OnRender(DrawingContext drawingContext) => _paint(drawingContext);
    }

    private FormattedText Text(string text, double size, Brush brush, FontWeight? weight = null, FontFamily? family = null) =>
        new(text, CultureInfo.GetCultureInfo("tr-TR"), FlowDirection.LeftToRight, new Typeface(family ?? UiFont, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, null, TextFormattingMode.Display, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void Pill(DrawingContext dc, FormattedText text, Point topLeft, Brush? border = null)
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double Snap(double v) => Math.Round(v * dpi) / dpi;
        var rect = new Rect(Snap(topLeft.X), Snap(topLeft.Y), Snap(text.WidthIncludingTrailingWhitespace + 16), Snap(text.Height + 8));
        dc.DrawRoundedRectangle(PanelBrush, new Pen(border ?? EdgeBrush, 1 / dpi), rect, 6, 6);
        dc.DrawText(text, new Point(rect.X + 8, rect.Y + 4));
    }

    private void PaintTools(DrawingContext dc)
    {
        if (_snapshot.Image.PixelWidth <= 0) return;
        Size surface = SurfaceSize;
        int width = _snapshot.Image.PixelWidth, height = _snapshot.Image.PixelHeight;
        double sx = surface.Width / width, sy = surface.Height / height;

        bool regionDrag = (_mode == OverlayMode.Capture && _dragging && _tool == AnnotationTool.Region) || ToolRegionDragging;
        if ((regionDrag || _mode == OverlayMode.Ruler) && !_document.Selection.IsEmpty)
        {
            Rect selection = AnnotationRenderer.PixelSelection(_document.Selection, width, height);
            if (selection.Width >= 1 && selection.Height >= 1)
            {
                var label = Text(OverlayRuler.SelectionSize(selection), 12, Brushes.White, FontWeights.SemiBold);
                double top = selection.Top * sy - label.Height - 14;
                if (top < 4) top = selection.Top * sy + 6;
                Pill(dc, label, new Point(Math.Max(4, selection.Left * sx), top));
            }
        }

        if (_mode == OverlayMode.Qr)
        {
            var pen = new Pen(AccentBrush, 2);
            for (int i = 0; i < _qrFindings.Count; i++)
            {
                Rect b = _qrFindings[i].Bounds;
                if (b.IsEmpty) continue;
                var rect = new Rect(b.X * sx, b.Y * sy, b.Width * sx, b.Height * sy);
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 4), rect, 6, 6);
                dc.DrawRoundedRectangle(null, pen, rect, 6, 6);
                if (_qrFindings.Count > 1) Pill(dc, Text((i + 1).ToString(CultureInfo.InvariantCulture), 12, Brushes.White, FontWeights.SemiBold), new Point(rect.X, Math.Max(2, rect.Y - 26)), AccentBrush);
            }
        }

        if (_mode == OverlayMode.Ruler && _ruler is RulerReading r) PaintRuler(dc, r, surface, width, height);
        if (_mode == OverlayMode.ColorPicker && _loupeVisible && _probe is (int px, int py)) PaintLoupe(dc, px, py, surface);
    }

    private void PaintRuler(DrawingContext dc, RulerReading r, Size surface, int width, int height)
    {
        Point a = FrozenFrame.PixelToSurface(r.StartX + .5, r.StartY + .5, surface, width, height);
        Point b = FrozenFrame.PixelToSurface(r.EndX + .5, r.EndY + .5, surface, width, height);
        var halo = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (r.Dx != 0 && r.Dy != 0)
        {
            var guide = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1) { DashStyle = DashStyles.Dash };
            var corner = new Point(b.X, a.Y);
            dc.DrawLine(guide, a, corner);
            dc.DrawLine(guide, corner, b);
        }
        dc.DrawLine(halo, a, b);
        dc.DrawLine(new Pen(AccentBrush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, a, b);
        Vector along = b - a;
        Vector normal = along.Length > .01 ? new Vector(-along.Y / along.Length, along.X / along.Length) : new Vector(0, -1);
        var tick = new Pen(Brushes.White, 1.5);
        foreach (Point end in new[] { a, b })
        {
            dc.DrawLine(tick, end - normal * 7, end + normal * 7);
            dc.DrawEllipse(Brushes.White, new Pen(AccentBrush, 1.5), end, 3, 3);
        }
        string text = r.Label + (r.Snapped ? "   ⇧ kilitli" : "");
        var label = Text(text, 12.5, Brushes.White, FontWeights.SemiBold);
        Point mid = a + along / 2;
        if (normal.Y > 0) normal = -normal;
        Point origin = mid + normal * 18 - new Vector((label.Width + 16) / 2, label.Height + 8);
        origin.X = Math.Clamp(origin.X, 4, Math.Max(4, surface.Width - label.Width - 24));
        origin.Y = Math.Clamp(origin.Y, 4, Math.Max(4, surface.Height - label.Height - 16));
        Pill(dc, label, origin, AccentBrush);
    }

    private void PaintLoupe(DrawingContext dc, int px, int py, Size surface)
    {
        FrozenFrame frame = Frame;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double Snap(double v) => Math.Round(v * dpi) / dpi;
        double hair = 1 / dpi;
        const int cells = 15, half = cells / 2;
        double cell = Math.Max(1, Math.Round(10 * dpi)) / dpi;   // whole physical pixels per magnified pixel
        double grid = cell * cells, pad = Snap(9);
        double boxWidth = Snap(Math.Max(grid + pad * 2, 272)), textHeight = Snap(80);
        double boxHeight = pad + grid + pad + textHeight;

        Point cursor = FrozenFrame.PixelToSurface(px + .5, py + .5, surface, frame.Width, frame.Height);
        Rect monitor = MonitorRectAt(cursor);
        const double offset = 24;
        double left = cursor.X + offset, top = cursor.Y + offset;
        if (left + boxWidth > monitor.Right - 6) left = cursor.X - offset - boxWidth;
        if (top + boxHeight > monitor.Bottom - 6) top = cursor.Y - offset - boxHeight;
        left = Snap(Math.Max(monitor.Left + 6, left));
        top = Snap(Math.Max(monitor.Top + 6, top));

        var box = new Rect(left, top, boxWidth, boxHeight);
        dc.DrawRoundedRectangle(PanelBrush, new Pen(AccentBrush, hair), box, 8, 8);
        double gridLeft = Snap(left + (boxWidth - grid) / 2), gridTop = Snap(top + pad);
        var outside = new SolidColorBrush(Color.FromRgb(34, 34, 34));
        for (int row = 0; row < cells; row++)
        {
            for (int col = 0; col < cells; col++)
            {
                PixelColor? pixel = frame.GetPixel(px - half + col, py - half + row);
                Brush brush = pixel is PixelColor p ? new SolidColorBrush(p.ToColor()) : outside;
                dc.DrawRectangle(brush, null, new Rect(gridLeft + col * cell, gridTop + row * cell, cell, cell));
            }
        }
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(64, 0, 0, 0)), hair);
        for (int i = 1; i < cells; i++)
        {
            double offsetLine = i * cell + hair / 2;
            dc.DrawLine(gridPen, new Point(gridLeft + offsetLine, gridTop), new Point(gridLeft + offsetLine, gridTop + grid));
            dc.DrawLine(gridPen, new Point(gridLeft, gridTop + offsetLine), new Point(gridLeft + grid, gridTop + offsetLine));
        }
        dc.DrawRectangle(null, new Pen(EdgeBrush, hair), new Rect(gridLeft - hair / 2, gridTop - hair / 2, grid + hair, grid + hair));
        var centre = new Rect(gridLeft + half * cell, gridTop + half * cell, cell, cell);
        dc.DrawRectangle(null, new Pen(Brushes.Black, 2 * hair), new Rect(centre.X - hair, centre.Y - hair, centre.Width + 2 * hair, centre.Height + 2 * hair));
        dc.DrawRectangle(null, new Pen(Brushes.White, hair), new Rect(centre.X + hair / 2, centre.Y + hair / 2, centre.Width - hair, centre.Height - hair));

        if (frame.GetPixel(px, py) is not PixelColor color) return;
        double textTop = gridTop + grid + pad, textLeft = left + pad;
        var swatch = new Rect(Snap(textLeft), Snap(textTop + 2), Snap(34), Snap(34));
        dc.DrawRoundedRectangle(new SolidColorBrush(color.ToColor()), new Pen(Brushes.White, hair), swatch, 4, 4);
        double tx = swatch.Right + Snap(10);
        dc.DrawText(Text(color.Hex, 15, Brushes.White, FontWeights.SemiBold, MonoFont), new Point(tx, textTop));
        dc.DrawText(Text(color.RgbText + "   " + color.HslText, 11.5, MutedBrush), new Point(tx, textTop + 21));
        (int dx, int dy) = frame.ToDesktop(px, py);
        dc.DrawText(Text(string.Create(CultureInfo.InvariantCulture, $"X {dx}   Y {dy}"), 11, DimBrush), new Point(textLeft, textTop + 42));
        dc.DrawText(Text("Tık: HEX  ·  Shift: RGB  ·  Ctrl: HSL  ·  Oklar: 1 px", 11, DimBrush), new Point(textLeft, textTop + 59));
    }
}
