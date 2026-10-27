using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BroidShot.Core;

namespace BroidShot.Modules.Utilities;

/// <summary>
/// Shared look of the labels and hint bars drawn on the screen overlays (colour picker, ruler, region select). Matches the
/// capture overlay: near-opaque dark panel, thin edge, BroidShot red for the active/measured element, Inter text, keycaps.
/// Colours come from the theme; sizes are in DIPs and multiplied by the overlay's DPI scale (the overlay lays out in physical pixels).
/// </summary>
internal static class OverlayChrome
{
    private static Brush? _panel, _edge, _accent, _text, _muted, _dim, _keyFill, _keyEdge;
    private static FontFamily? _mono;

    public static Color AccentColor => Ui.ThemeColor("AccentColor", Color.FromRgb(229, 72, 77));

    /// <summary>Panel fill: the theme's overlay colour, very slightly translucent.</summary>
    public static Brush Panel => _panel ??= Frozen(new SolidColorBrush(WithAlpha(Ui.ThemeColor("OverlayColor", Color.FromRgb(26, 26, 31)), 246)));
    public static Brush Edge => _edge ??= Frozen(new SolidColorBrush(Ui.ThemeColor("StrokeStrongColor", Color.FromRgb(66, 66, 76))));
    public static Brush Accent => _accent ??= Frozen(new SolidColorBrush(AccentColor));
    public static Brush Text => _text ??= Frozen(new SolidColorBrush(Ui.ThemeColor("TextColor", Color.FromRgb(243, 243, 244))));
    public static Brush Muted => _muted ??= Frozen(new SolidColorBrush(Ui.ThemeColor("TextSecondaryColor", Color.FromRgb(169, 169, 178))));
    public static Brush Dim => _dim ??= Frozen(new SolidColorBrush(Ui.ThemeColor("TextTertiaryColor", Color.FromRgb(120, 120, 130))));
    public static Brush KeyFill => _keyFill ??= Frozen(new SolidColorBrush(Ui.ThemeColor("SurfaceActiveColor", Color.FromRgb(36, 36, 42))));
    public static Brush KeyEdge => _keyEdge ??= Frozen(new SolidColorBrush(Ui.ThemeColor("StrokeStrongColor", Color.FromRgb(66, 66, 76))));

    public static FontFamily UiFont => ThemeResources.UiFontFamily;
    public static FontFamily MonoFont => _mono ??= Ui.Resource<FontFamily>("MonoFont") ?? new FontFamily("Cascadia Mono, Consolas");

    /// <summary>A rounded panel pill. <paramref name="accent"/> draws the red edge used for the live measurement.</summary>
    public static Border Pill(double scale, bool accent = false) => new()
    {
        Background = Panel,
        BorderBrush = accent ? Accent : Edge,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Math.Round(7 * scale)),
        Padding = new Thickness(Math.Round(10 * scale), Math.Round(5 * scale), Math.Round(10 * scale), Math.Round(6 * scale)),
        SnapsToDevicePixels = true,
        IsHitTestVisible = false,
        Effect = new DropShadowEffect { BlurRadius = 16 * scale, ShadowDepth = 2 * scale, Opacity = .45, Color = Colors.Black }
    };

    public static TextBlock Label(string text, double scale, bool strong = false, Brush? brush = null) => new()
    {
        Text = text,
        Foreground = brush ?? Text,
        FontFamily = UiFont,
        FontSize = Math.Round(12.5 * scale * 2) / 2,
        FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Small keyboard key ("Esc", "Shift").</summary>
    public static Border Keycap(string key, double scale) => new()
    {
        Background = KeyFill,
        BorderBrush = KeyEdge,
        BorderThickness = new Thickness(1, 1, 1, Math.Max(1, Math.Round(2 * scale))),
        CornerRadius = new CornerRadius(Math.Round(4 * scale)),
        Padding = new Thickness(Math.Round(6 * scale), 0, Math.Round(6 * scale), Math.Round(1 * scale)),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = key, Foreground = Muted, FontFamily = UiFont, FontSize = Math.Round(11.5 * scale * 2) / 2, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    /// <summary>
    /// Hint bar: a leading title, then "key + action" pairs, e.g. Hint(scale, "Ölçmek için sürükle", ("Shift", "yatay/dikey"), ("Esc", "kapat")).
    /// </summary>
    public static Border Hint(double scale, string title, params (string Key, string Action)[] keys)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Border
        {
            Width = Math.Round(6 * scale), Height = Math.Round(6 * scale), CornerRadius = new CornerRadius(3 * scale), Background = Accent,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Math.Round(9 * scale), 0)
        };
        row.Children.Add(dot);
        row.Children.Add(Label(title, scale, strong: true));
        foreach ((string key, string action) in keys)
        {
            row.Children.Add(new Border { Width = 1, Background = Edge, Margin = new Thickness(Math.Round(12 * scale), Math.Round(3 * scale), Math.Round(12 * scale), Math.Round(3 * scale)) });
            row.Children.Add(Keycap(key, scale));
            TextBlock text = Label(action, scale, brush: Muted);
            text.Margin = new Thickness(Math.Round(7 * scale), 0, 0, 0);
            row.Children.Add(text);
        }
        Border pill = Pill(scale);
        pill.Padding = new Thickness(Math.Round(12 * scale), Math.Round(7 * scale), Math.Round(14 * scale), Math.Round(7 * scale));
        pill.CornerRadius = new CornerRadius(Math.Round(10 * scale));
        pill.Child = row;
        return pill;
    }

    /// <summary>Places an element horizontally centred near the top of the monitor under the cursor (snapshot coordinates).</summary>
    public static void PlaceTopCentre(FrameworkElement element, ScreenSnapshot snapshot, int cursorX, int cursorY, double scale)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        PixelRect monitor = Intersect(Native.GetMonitorBounds(cursorX, cursorY) ?? snapshot.Bounds, snapshot.Bounds) ?? snapshot.Bounds;
        double x = monitor.X - snapshot.Bounds.X + (monitor.Width - element.DesiredSize.Width) / 2;
        double y = monitor.Y - snapshot.Bounds.Y + Math.Round(20 * scale);
        Canvas.SetLeft(element, Math.Round(Math.Max(0, x)));
        Canvas.SetTop(element, Math.Round(Math.Max(0, y)));
    }

    public static FormattedText Format(string text, double size, Brush brush, FontWeight? weight = null, FontFamily? family = null) =>
        new(text, CultureInfo.GetCultureInfo("tr-TR"), FlowDirection.LeftToRight,
            new Typeface(family ?? UiFont, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush, null, TextFormattingMode.Display, 1.0);

    private static PixelRect? Intersect(PixelRect a, PixelRect b)
    {
        int left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y), right = Math.Min(a.Right, b.Right), bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : null;
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Brush Frozen(SolidColorBrush brush) { brush.Freeze(); return brush; }
}
