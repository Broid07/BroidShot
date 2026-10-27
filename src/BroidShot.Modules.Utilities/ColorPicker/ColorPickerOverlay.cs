using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BroidShot.Modules.Utilities;

/// <summary>Full-desktop overlay with a magnifier loupe and pixel grid; a click picks the colour under the cursor.</summary>
internal sealed class ColorPickerOverlay : ScreenOverlayWindow
{
    private readonly TaskCompletionSource<Rgb?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LoupeVisual _loupe;
    private Border? _hint;
    private bool _armed;

    public ColorPickerOverlay(ScreenSnapshot snapshot) : base(snapshot, 0)
    {
        _loupe = new LoupeVisual(snapshot);
        Surface.Children.Add(_loupe);
        MouseMove += (_, _) => UpdateLoupe();
        MouseLeftButtonDown += (_, e) => { _armed = true; e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_armed) return;
            e.Handled = true;
            (int x, int y) = CursorOnDesktop();
            _result.TrySetResult(Snapshot.GetPixel(x, y));
            FinishOverlay();
        };
    }

    /// <summary>The picked colour, or null when cancelled.</summary>
    public Task<Rgb?> Result => _result.Task;

    protected override void OnShown()
    {
        (int x, int y) = CursorOnDesktop();
        BuildHint(UiScale, x, y);
        UpdateLoupe();
    }

    protected override void OnFinished() => _result.TrySetResult(null);

    protected override void OnKey(KeyEventArgs e)
    {
        // Arrow keys nudge the cursor one pixel for exact picking; Enter/Space picks.
        (int dx, int dy) = e.Key switch { Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, -1), Key.Down => (0, 1), _ => (0, 0) };
        if (dx != 0 || dy != 0)
        {
            e.Handled = true;
            (int x, int y) = CursorOnDesktop();
            Native.SetCursor(x + dx, y + dy);
            UpdateLoupe();
        }
        else if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            (int x, int y) = CursorOnDesktop();
            _result.TrySetResult(Snapshot.GetPixel(x, y));
            FinishOverlay();
        }
    }

    private void UpdateLoupe()
    {
        (int x, int y) = CursorOnDesktop();
        _loupe.Update(x, y, UiScale, Native.GetMonitorBounds(x, y));
    }

    /// <summary>Shows the loupe and hint for a desktop point without user input (verification renders the overlay this way).</summary>
    internal void ShowLoupeAt(int x, int y, double scale)
    {
        BuildHint(scale, x, y);
        _loupe.Update(x, y, scale, null);
    }

    private void BuildHint(double scale, int cursorX, int cursorY)
    {
        if (_hint is not null) Surface.Children.Remove(_hint);
        _hint = OverlayChrome.Hint(scale, "Renk seçmek için tıkla", ("Oklar", "1 piksel kaydır"), ("Enter", "seç"), ("Esc", "iptal"));
        Surface.Children.Insert(Math.Max(0, Surface.Children.IndexOf(_loupe)), _hint);
        OverlayChrome.PlaceTopCentre(_hint, Snapshot, cursorX, cursorY, scale);
    }

    /// <summary>Draws the magnified neighbourhood, the pixel grid and a colour read-out next to the cursor (same look as the capture overlay loupe).</summary>
    private sealed class LoupeVisual : FrameworkElement
    {
        private const int Cells = 15;
        private readonly ScreenSnapshot _snapshot;
        private int _x, _y;
        private double _scale = 1;
        private PixelRect? _monitor;

        public LoupeVisual(ScreenSnapshot snapshot)
        {
            _snapshot = snapshot;
            IsHitTestVisible = false;
            Width = snapshot.Bounds.Width;
            Height = snapshot.Bounds.Height;
        }

        public void Update(int x, int y, double scale, PixelRect? monitor)
        {
            _x = x; _y = y; _scale = Math.Clamp(scale, 1, 3); _monitor = monitor;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double s = _scale;
            double cell = Math.Round(10 * s);
            double grid = cell * Cells;
            double pad = Math.Round(9 * s);
            double textHeight = Math.Round(80 * s);
            double boxWidth = Math.Max(grid + pad * 2, Math.Round(272 * s)), boxHeight = pad + grid + pad + textHeight;
            double cursorX = _x - _snapshot.Bounds.X, cursorY = _y - _snapshot.Bounds.Y;
            double offset = Math.Round(24 * s);

            // Keep the loupe on the monitor under the cursor.
            PixelRect area = _monitor ?? _snapshot.Bounds;
            double areaLeft = area.X - _snapshot.Bounds.X, areaTop = area.Y - _snapshot.Bounds.Y;
            double areaRight = areaLeft + area.Width, areaBottom = areaTop + area.Height;
            double left = cursorX + offset, top = cursorY + offset;
            if (left + boxWidth > areaRight - 6) left = cursorX - offset - boxWidth;
            if (top + boxHeight > areaBottom - 6) top = cursorY - offset - boxHeight;
            left = Math.Round(Math.Max(areaLeft + 6, left));
            top = Math.Round(Math.Max(areaTop + 6, top));

            var box = new Rect(left + .5, top + .5, boxWidth, boxHeight);
            dc.DrawRoundedRectangle(OverlayChrome.Panel, new Pen(OverlayChrome.Accent, 1), box, 8 * s, 8 * s);

            double gridLeft = Math.Round(left + (boxWidth - grid) / 2), gridTop = Math.Round(top + pad);
            int half = Cells / 2;
            var outside = new SolidColorBrush(Color.FromRgb(34, 34, 34));
            for (int row = 0; row < Cells; row++)
            {
                for (int col = 0; col < Cells; col++)
                {
                    Rgb? pixel = _snapshot.GetPixel(_x - half + col, _y - half + row);
                    Brush brush = pixel is Rgb p ? new SolidColorBrush(Color.FromRgb(p.R, p.G, p.B)) : outside;
                    dc.DrawRectangle(brush, null, new Rect(gridLeft + col * cell, gridTop + row * cell, cell, cell));
                }
            }
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(64, 0, 0, 0)), 1);
            for (int i = 1; i < Cells; i++)
            {
                double line = i * cell + .5;
                dc.DrawLine(gridPen, new Point(gridLeft + line, gridTop), new Point(gridLeft + line, gridTop + grid));
                dc.DrawLine(gridPen, new Point(gridLeft, gridTop + line), new Point(gridLeft + grid, gridTop + line));
            }
            dc.DrawRectangle(null, new Pen(OverlayChrome.Edge, 1), new Rect(gridLeft - .5, gridTop - .5, grid + 1, grid + 1));
            var centre = new Rect(gridLeft + half * cell, gridTop + half * cell, cell, cell);
            dc.DrawRectangle(null, new Pen(Brushes.Black, 2), new Rect(centre.X - 1, centre.Y - 1, centre.Width + 2, centre.Height + 2));
            dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(centre.X + .5, centre.Y + .5, centre.Width - 1, centre.Height - 1));

            if (_snapshot.GetPixel(_x, _y) is not Rgb c) return;
            double textTop = gridTop + grid + pad, textLeft = left + pad;
            var swatch = new Rect(Math.Round(textLeft) + .5, Math.Round(textTop + 2 * s) + .5, Math.Round(34 * s), Math.Round(34 * s));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), new Pen(Brushes.White, 1), swatch, 4 * s, 4 * s);
            double tx = Math.Round(swatch.Right + 10 * s);
            dc.DrawText(OverlayChrome.Format(ColorFormats.ToHex(c), Math.Round(15 * s), OverlayChrome.Text, FontWeights.SemiBold, OverlayChrome.MonoFont), new Point(tx, Math.Round(textTop)));
            dc.DrawText(OverlayChrome.Format(ColorFormats.ToRgbText(c) + "   " + ColorFormats.ToHslText(c), Math.Round(11.5 * s * 2) / 2, OverlayChrome.Muted),
                new Point(tx, Math.Round(textTop + 21 * s)));
            dc.DrawText(OverlayChrome.Format(string.Create(CultureInfo.InvariantCulture, $"X {_x}   Y {_y}"), Math.Round(11 * s), OverlayChrome.Dim),
                new Point(Math.Round(textLeft), Math.Round(textTop + 42 * s)));
            dc.DrawText(OverlayChrome.Format("Tık / Enter: seç  ·  Oklar: 1 px  ·  Esc: iptal", Math.Round(11 * s), OverlayChrome.Dim),
                new Point(Math.Round(textLeft), Math.Round(textTop + 59 * s)));
        }
    }
}
