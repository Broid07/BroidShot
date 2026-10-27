using System.Windows;

namespace BroidShot.Core;

/// <summary>
/// Attached properties read by the shared control templates in Themes/Theme.xaml.
/// XAML: xmlns:core="clr-namespace:BroidShot.Core;assembly=BroidShot.Core"
/// <list type="bullet">
/// <item><c>core:ThemeAssist.Icon="&amp;#xE8C8;"</c> on any Button/ToggleButton: glyph (IconFont) drawn before the content.</item>
/// <item><c>core:ThemeAssist.Placeholder="Ara…"</c> on a TextBox: hint shown while the text is empty.</item>
/// <item><c>core:ThemeAssist.CornerRadius="8"</c> on buttons, inputs and list items: overrides the style's radius.</item>
/// </list>
/// </summary>
public static class ThemeAssist
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(ThemeAssist), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static string? GetIcon(DependencyObject element) => (string?)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, string? value) => element.SetValue(IconProperty, value);

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(ThemeAssist), new FrameworkPropertyMetadata(null));

    public static string? GetPlaceholder(DependencyObject element) => (string?)element.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject element, string? value) => element.SetValue(PlaceholderProperty, value);

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(ThemeAssist), new FrameworkPropertyMetadata(new CornerRadius(6), FrameworkPropertyMetadataOptions.AffectsRender));

    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);
}
