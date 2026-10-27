using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BroidShot.Capture;

namespace BroidShot.Editor;

public sealed partial class CaptureOverlayWindow : Window
{
    private readonly CaptureSnapshot _snapshot;
    private readonly AnnotationDocument _document = new();
    private readonly CaptureSurface _surface;
    private readonly Canvas _chrome = new();
    private readonly Border _toolbar;
    private readonly TextBlock _sizeLabel = new();
    private readonly TextBlock _toolLabel = new();
    private readonly System.Collections.Generic.Dictionary<AnnotationTool, Button> _toolButtons = new();
    private AnnotationTool _tool = AnnotationTool.Region;
    private Color _color = Color.FromRgb(217, 64, 70);
    private double _thickness = 4;
    private Point _start;
    private Point _previous;
    private Annotation? _active;
    private bool _dragging;

    public event Action<BitmapSource>? Completed;
    public event Action<BitmapSource>? SaveRequested;

    public CaptureOverlayWindow(CaptureSnapshot snapshot, PixelRect? initialSelection = null)
    {
        _snapshot = snapshot;
        Title = "BroidShot — alan seç ve düzenle";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = snapshot.Bounds.X;
        Top = snapshot.Bounds.Y;
        Width = snapshot.Bounds.Width;
        Height = snapshot.Bounds.Height;
        Topmost = true;
        ShowInTaskbar = true;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/BroidShot;component/Branding/BroidShot.ico"));
        Background = Brushes.Black;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Cursor = Cursors.Cross;
        _surface = new CaptureSurface(snapshot.Image, _document);
        var grid = new Grid();
        grid.Children.Add(_surface);
        grid.Children.Add(_chrome);
        Content = grid;
        _toolbar = BuildToolbar();
        _chrome.Children.Add(_toolbar);
        var hintText = new TextBlock { Text = CaptureHint, Foreground = Brushes.White, FontSize = 14 };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 17, 17, 17)),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 11, 18, 11), BorderBrush = new SolidColorBrush(Color.FromRgb(115, 47, 51)), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = hintText
        };
        Canvas.SetLeft(hint, 22);
        Canvas.SetTop(hint, 22);
        _chrome.Children.Add(hint);
        InitializeTools(grid, hint, hintText);
        _surface.MouseLeftButtonDown += OnPointerDown;
        _surface.MouseMove += OnPointerMove;
        _surface.MouseLeftButtonUp += OnPointerUp;
        PreviewKeyDown += OnKeyDown;
        SizeChanged += (_, _) => Refresh();
        Loaded += (_, _) => { Activate(); Focus(); Refresh(); };
        if (initialSelection is PixelRect bounds)
        {
            _document.Selection = CaptureGeometry.ToSnapshotSelection(snapshot.Bounds, bounds);
            _tool = AnnotationTool.Arrow;
        }
        Refresh();
    }

    private Border BuildToolbar()
    {
        var container = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(6, 2, 6, 8) };
        _sizeLabel.Foreground = new SolidColorBrush(Color.FromRgb(226, 226, 224));
        _sizeLabel.FontSize = 13;
        _sizeLabel.FontWeight = FontWeights.SemiBold;
        _toolLabel.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));
        _toolLabel.FontSize = 12;
        _toolLabel.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(_sizeLabel, Dock.Left);
        header.Children.Add(_sizeLabel);
        header.Children.Add(_toolLabel);
        container.Children.Add(header);
        var tools = new WrapPanel();
        AddTool(tools, AnnotationTool.Select, "↖", "Seç / taşı (V)");
        AddTool(tools, AnnotationTool.Arrow, "↗", "Ok (A)");
        AddTool(tools, AnnotationTool.Rectangle, "▭", "Dikdörtgen (R)");
        AddTool(tools, AnnotationTool.Ellipse, "○", "Elips (E)");
        AddTool(tools, AnnotationTool.Pen, "✎", "Kalem (P)");
        AddTool(tools, AnnotationTool.Text, "T", "Metin (T) · çift tıkla düzenle");
        AddTool(tools, AnnotationTool.Number, "①", "Numara (N)");
        AddTool(tools, AnnotationTool.Highlight, "▰", "Vurgula (H)");
        AddTool(tools, AnnotationTool.Redact, "■", "Hassas bilgiyi kapat (B)");
        AddTool(tools, AnnotationTool.Region, "⌗", "Alanı yeniden seç (C)");
        tools.Children.Add(MakeButton("↶", "Geri al (Ctrl+Z)", () => { _document.Undo(); Refresh(); }));
        tools.Children.Add(MakeButton("↷", "İleri al (Ctrl+Y)", () => { _document.Redo(); Refresh(); }));
        tools.Children.Add(MakeButton("⌫", "Seçili nesneyi sil (Delete)", DeleteSelected));
        container.Children.Add(tools);
        var options = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        foreach (var color in new[] { Color.FromRgb(217, 64, 70), Colors.Gold, Color.FromRgb(61, 214, 175), Color.FromRgb(92, 165, 255), Colors.White, Colors.Black })
        {
            var swatch = MakeButton("●", "Çizim rengi", () => { _color = color; ApplySelectedStyle(); });
            swatch.Foreground = new SolidColorBrush(color);
            swatch.Width = 29;
            options.Children.Add(swatch);
        }
        var thickness = new ComboBox { Width = 83, Margin = new Thickness(8, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Çizgi kalınlığı / metin boyutu" };
        foreach (int width in new[] { 2, 4, 6, 9, 12 }) thickness.Items.Add($"{width} px");
        thickness.SelectedIndex = 1;
        thickness.SelectionChanged += (_, _) => { _thickness = new[] { 2, 4, 6, 9, 12 }[thickness.SelectedIndex]; ApplySelectedStyle(); };
        options.Children.Add(thickness);
        options.Children.Add(MakeButton("OCR", "Seçimdeki metni tanı (O)", () => { if (_mode == OverlayMode.Ocr) RunModeAction(); else SetMode(OverlayMode.Ocr); }));
        options.Children.Add(MakeButton("Kaydet", "Farklı kaydet (Ctrl+S)", () => Emit(SaveRequested)));
        var done = MakeButton("✓  Kopyala", "Kopyala ve geçmişe ekle (Ctrl+C)", Finish);
        done.Background = new SolidColorBrush(Color.FromRgb(29, 29, 29));
        done.BorderBrush = new SolidColorBrush(Color.FromRgb(139, 51, 56));
        done.FontWeight = FontWeights.SemiBold;
        options.Children.Add(done);
        options.Children.Add(MakeButton("✕", "İptal (Esc)", Close));
        container.Children.Add(options);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(17, 17, 17)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(52, 52, 52)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(10), Width = 585,
            Child = container, Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = .4 }
        };
    }

    private static Button MakeButton(string text, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = text, ToolTip = tooltip, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(2),
            MinWidth = 34, Height = 36, FontSize = 14, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)), BorderBrush = new SolidColorBrush(Color.FromRgb(47, 47, 47)), BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand, Focusable = false
        };
        if (text.Length <= 2 && !char.IsLetterOrDigit(text[0]))
        {
            button.FontFamily = new FontFamily("Segoe UI Symbol");
            button.FontSize = 16;
        }
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private void AddTool(Panel panel, AnnotationTool tool, string label, string tooltip)
    {
        var button = MakeButton(label, tooltip, () => { SetMode(OverlayMode.Capture); _tool = tool; _document.SelectedId = null; Refresh(); });
        _toolButtons.Add(tool, button);
        panel.Children.Add(button);
    }

    private Point PixelPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(_surface);
        return new Point(Math.Clamp(p.X * _snapshot.Image.PixelWidth / Math.Max(1, _surface.ActualWidth), 0, _snapshot.Image.PixelWidth), Math.Clamp(p.Y * _snapshot.Image.PixelHeight / Math.Max(1, _surface.ActualHeight), 0, _snapshot.Image.PixelHeight));
    }

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (ToolPointerDown(e)) return;
        Focus();
        var point = PixelPoint(e);
        var hit = _document.Items.LastOrDefault(x => x.Contains(point));
        if (e.ClickCount == 2 && hit?.Tool == AnnotationTool.Text)
        {
            EditText(hit);
            e.Handled = true;
            return;
        }
        if (_tool != AnnotationTool.Region && !_document.Selection.Contains(point)) return;
        if (_tool == AnnotationTool.Text)
        {
            var value = PromptText("");
            if (!string.IsNullOrWhiteSpace(value))
            {
                _document.Checkpoint();
                var text = new Annotation { Tool = _tool, Start = point, Color = _color, Thickness = _thickness, Text = value };
                UpdateTextBounds(text);
                _document.Items.Add(text);
                _document.SelectedId = text.Id;
            }
            Refresh();
            return;
        }
        if (_tool == AnnotationTool.Number)
        {
            _document.Checkpoint();
            double radius = Math.Max(16, _thickness * 5);
            _document.Items.Add(new Annotation { Tool = _tool, Start = point, End = point + new Vector(radius, radius), Color = _color, Thickness = _thickness, Text = (_document.Items.Count(x => x.Tool == AnnotationTool.Number) + 1).ToString() });
            Refresh();
            return;
        }
        _start = _previous = point;
        _active = null;
        _dragging = true;
        _document.Checkpoint();
        if (_tool == AnnotationTool.Region) _document.Selection = new Rect(point, point);
        else if (_tool == AnnotationTool.Select)
        {
            _active = hit;
            _document.SelectedId = hit?.Id;
        }
        else
        {
            _active = new Annotation { Tool = _tool, Start = point, End = point, Color = _color, Thickness = _thickness };
            _active.Points.Add(point);
            _document.Items.Add(_active);
            _document.SelectedId = null;
        }
        _surface.CaptureMouse();
        Refresh();
        e.Handled = true;
    }

    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (ToolPointerMove(e)) return;
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var point = PixelPoint(e);
        if (_tool == AnnotationTool.Region) _document.Selection = new Rect(_start, point);
        else if (_tool == AnnotationTool.Select) _active?.Move(point - _previous);
        else if (_active != null)
        {
            _active.End = point;
            if (_active.Tool == AnnotationTool.Pen && (_active.Points.Last() - point).Length >= .8) _active.Points.Add(point);
        }
        _previous = point;
        Refresh();
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (ToolPointerUp(e)) return;
        if (!_dragging) return;
        _dragging = false;
        _surface.ReleaseMouseCapture();
        if (_tool == AnnotationTool.Region)
        {
            _document.Selection = AnnotationRenderer.PixelSelection(_document.Selection, _snapshot.Image.PixelWidth, _snapshot.Image.PixelHeight);
            if (_document.Selection.Width > 2 && _document.Selection.Height > 2) _tool = AnnotationTool.Arrow;
        }
        else if (_active != null && _tool != AnnotationTool.Select && (_active.End - _active.Start).Length < 2)
            _document.Items.Remove(_active);
        _active = null;
        Refresh();
        e.Handled = true;
    }

    private void Refresh()
    {
        _surface.InvalidateVisual();
        _toolLayer.InvalidateVisual();
        PositionToolChrome();
        bool hasSelection = !_document.Selection.IsEmpty && _document.Selection.Width > 2 && _document.Selection.Height > 2;
        _toolbar.Visibility = hasSelection && !(_dragging && _tool == AnnotationTool.Region) && !ToolRegionDragging ? Visibility.Visible : Visibility.Collapsed;
        Cursor = _mode == OverlayMode.Capture && _tool == AnnotationTool.Select ? Cursors.Arrow : Cursors.Cross;
        foreach (var entry in _toolButtons)
        {
            entry.Value.Background = new SolidColorBrush(entry.Key == _tool ? Color.FromRgb(39, 39, 39) : Color.FromRgb(28, 28, 28));
            entry.Value.BorderBrush = new SolidColorBrush(entry.Key == _tool ? Color.FromRgb(169, 57, 63) : Color.FromRgb(47, 47, 47));
        }
        if (!hasSelection) return;
        var selection = _document.Selection;
        _sizeLabel.Text = $"{(int)selection.Width} × {(int)selection.Height} px";
        _toolLabel.Text = "Ctrl+Z: geri al  ·  Ctrl+C: bitir";
        double sx = Math.Max(1, _surface.ActualWidth) / _snapshot.Image.PixelWidth;
        double sy = Math.Max(1, _surface.ActualHeight) / _snapshot.Image.PixelHeight;
        double toolbarWidth = Math.Min(585, Math.Max(200, ActualWidth - 24));
        _toolbar.Width = toolbarWidth;
        _toolbar.Measure(new Size(toolbarWidth, double.PositiveInfinity));
        double toolbarHeight = _toolbar.DesiredSize.Height;
        double left = Math.Clamp(selection.Left * sx, 12, Math.Max(12, ActualWidth - toolbarWidth - 12));
        double below = selection.Bottom * sy + 12;
        double top = below + toolbarHeight < ActualHeight - 12 ? below : selection.Top * sy - toolbarHeight - 12;
        top = Math.Clamp(top, 12, Math.Max(12, ActualHeight - toolbarHeight - 12));
        Canvas.SetLeft(_toolbar, left);
        Canvas.SetTop(_toolbar, top);
    }

    private void ApplySelectedStyle()
    {
        var selected = _document.Items.FirstOrDefault(x => x.Id == _document.SelectedId);
        if (selected == null) return;
        _document.Checkpoint();
        selected.Color = _color;
        selected.Thickness = _thickness;
        if (selected.Tool == AnnotationTool.Text) UpdateTextBounds(selected);
        Refresh();
    }

    private void DeleteSelected()
    {
        if (_document.SelectedId is not Guid id) return;
        _document.Checkpoint();
        _document.Items.RemoveAll(x => x.Id == id);
        _document.SelectedId = null;
        Refresh();
    }

    private void EditText(Annotation text)
    {
        var value = PromptText(text.Text);
        if (value == null || value == text.Text) return;
        _document.Checkpoint();
        if (string.IsNullOrWhiteSpace(value)) _document.Items.Remove(text);
        else { text.Text = value; UpdateTextBounds(text); }
        _document.SelectedId = text.Id;
        Refresh();
    }

    private static void UpdateTextBounds(Annotation text)
    {
        var formatted = AnnotationRenderer.CreateText(text.Text, Math.Max(16, text.Thickness * 5), Brushes.White);
        text.End = text.Start + new Vector(Math.Max(10, formatted.WidthIncludingTrailingWhitespace), formatted.Height);
    }

    private string? PromptText(string initial)
    {
        var dialog = new Window
        {
            Title = "Metin düzenle", Owner = this, Width = 440, Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(17, 17, 17)), Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 14,
            UseLayoutRounding = true, SnapsToDevicePixels = true,
            ShowInTaskbar = false
        };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var cancel = MakeButton("Vazgeç", "Metin eklemeyi iptal et", () => dialog.DialogResult = false);
        var save = MakeButton("Uygula", "Metni uygula", () => dialog.DialogResult = true);
        save.IsDefault = true;
        cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        var input = new TextBox { Text = initial, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 18, Padding = new Thickness(10), Background = new SolidColorBrush(Color.FromRgb(24, 24, 24)), Foreground = Brushes.White, CaretBrush = Brushes.White };
        AutomationProperties.SetName(input, "Alıntıya eklenecek metin");
        panel.Children.Add(input);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (ToolKeyDown(e)) return;
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (e.Key)
            {
                case Key.C: Finish(); break;
                case Key.S: Emit(SaveRequested); break;
                case Key.Z: _document.Undo(); Refresh(); break;
                case Key.Y: _document.Redo(); Refresh(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete || e.Key == Key.Back) { DeleteSelected(); e.Handled = true; return; }
        AnnotationTool? tool = e.Key switch { Key.V => AnnotationTool.Select, Key.A => AnnotationTool.Arrow, Key.R => AnnotationTool.Rectangle, Key.E => AnnotationTool.Ellipse, Key.P => AnnotationTool.Pen, Key.T => AnnotationTool.Text, Key.N => AnnotationTool.Number, Key.H => AnnotationTool.Highlight, Key.B => AnnotationTool.Redact, Key.C => AnnotationTool.Region, _ => null };
        if (tool.HasValue) { SetMode(OverlayMode.Capture); _tool = tool.Value; _document.SelectedId = null; Refresh(); e.Handled = true; }
    }

    private bool Emit(Action<BitmapSource>? action)
    {
        if (_document.Selection.IsEmpty || _document.Selection.Width < 3 || _document.Selection.Height < 3) return false;
        try { action?.Invoke(AnnotationRenderer.Export(_snapshot.Image, _document)); return true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "BroidShot", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    private void Finish()
    {
        if (_document.Selection.IsEmpty || _document.Selection.Width < 3 || _document.Selection.Height < 3) return;
        if (Emit(Completed)) Close();
    }

    private sealed class CaptureSurface : FrameworkElement
    {
        private readonly BitmapSource _source;
        private readonly AnnotationDocument _document;
        public CaptureSurface(BitmapSource source, AnnotationDocument document)
        {
            _source = source;
            _document = document;
            Focusable = false;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }
        protected override void OnRender(DrawingContext context)
        {
            base.OnRender(context);
            context.PushTransform(new ScaleTransform(ActualWidth / _source.PixelWidth, ActualHeight / _source.PixelHeight));
            var full = new Rect(0, 0, _source.PixelWidth, _source.PixelHeight);
            context.DrawImage(_source, full);
            if (!_document.Selection.IsEmpty && _document.Selection.Width > 0 && _document.Selection.Height > 0)
            {
                var selection = _document.Selection;
                context.DrawGeometry(new SolidColorBrush(Color.FromArgb(135, 0, 0, 0)), null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(selection)));
                context.PushClip(new RectangleGeometry(selection));
                AnnotationRenderer.Draw(context, _document.Items);
                context.Pop();
                context.DrawRectangle(null, new Pen(Brushes.White, 1), selection);
                var selected = _document.Items.FirstOrDefault(x => x.Id == _document.SelectedId);
                if (selected != null)
                {
                    var bounds = selected.Bounds;
                    bounds.Inflate(4, 4);
                    context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(217, 64, 70)), 1) { DashStyle = DashStyles.Dash }, bounds);
                }
            }
            else context.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, full);
            context.Pop();
        }
    }
}
