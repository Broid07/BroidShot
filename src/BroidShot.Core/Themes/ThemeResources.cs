using System;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BroidShot.Core;

/// <summary>
/// The shared dark + BroidShot red theme. The application merges it into Application.Resources, so module
/// pages and windows inherit it automatically. Merge it explicitly only for standalone hosts (tests,
/// designer): &lt;ResourceDictionary Source="pack://application:,,,/BroidShot.Core;component/Themes/Theme.xaml"/&gt;.
/// Token and style names are documented in Themes/README.md.
/// </summary>
public static class ThemeResources
{
    public const string PackUri = "pack://application:,,,/BroidShot.Core;component/Themes/Theme.xaml";

    /// <summary>Body font source (same value as the UiFont resource): bundled Inter 4.1, Segoe UI as fallback.</summary>
    public const string UiFontSource = "pack://application:,,,/BroidShot.Core;component/Themes/Fonts/#Inter, Segoe UI";

    private static FontFamily? _uiFont;

    /// <summary>The UI font (Inter) as a FontFamily, for code that cannot use the UiFont resource.</summary>
    public static FontFamily UiFontFamily => _uiFont ??= new FontFamily(UiFontSource);

    /// <summary>Glyph font stack (same value as the IconFont resource). Segoe Fluent Icons on Windows 11, MDL2 on Windows 10.</summary>
    public const string IconFontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";

    /// <summary>Default UI text size in DIPs.</summary>
    public const double BodyFontSize = 14;

    private static int _defaultsApplied;

    public static Uri Uri { get; } = new(PackUri, UriKind.Absolute);

    public static ResourceDictionary Load() => new() { Source = Uri };

    /// <summary>
    /// Makes every Window (including windows created by modules) start with the theme font, size and
    /// text rendering as inherited defaults. The theme deliberately has no font setters in its implicit
    /// TextBlock style: those would override inherited values, e.g. turning the glyph of
    /// <c>&lt;Button Style="{StaticResource IconButton}" Content="&amp;#xE711;"/&gt;</c> into a box.
    /// Call once at startup, before the first window is created. Safe to call more than once.
    /// </summary>
    public static void ApplyDefaults()
    {
        if (Interlocked.Exchange(ref _defaultsApplied, 1) == 1) return;
        try
        {
            TextElement.FontFamilyProperty.OverrideMetadata(typeof(Window),
                new FrameworkPropertyMetadata(UiFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
            TextElement.FontSizeProperty.OverrideMetadata(typeof(Window),
                new FrameworkPropertyMetadata(BodyFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Metadata is already sealed (a window existed before this call). Windows that set
            // FontFamily themselves, like the shell, are unaffected.
            ErrorLog.Write("theme", "Varsayılan yazı tipi uygulanamadı", ex);
        }
    }
}
