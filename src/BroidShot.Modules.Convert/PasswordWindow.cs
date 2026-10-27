using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BroidShot.Core;

namespace BroidShot.Modules.Convert;

/// <summary>
/// Small modal prompt for the password of a protected PDF. Built in code so it needs no extra XAML.
/// Uses the app theme: the implicit Window style (Inter, dark surface) is applied explicitly because implicit
/// styles do not reach derived window types, and the native title bar is switched to dark.
/// </summary>
internal sealed class PasswordWindow : Window
{
    private readonly PasswordBox _password;

    private PasswordWindow(string fileName, bool wrongPrevious)
    {
        // Standalone hosts (tests, a module opened without the shell) may not have the theme in Application.Resources.
        if (TryFindResource("SurfaceBrush") is null) Resources.MergedDictionaries.Add(ThemeResources.Load());
        SetResourceReference(StyleProperty, typeof(Window));

        Title = "Parola gerekli";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = System.Windows.ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;

        var root = new DockPanel { LastChildFill = true };

        // Footer: secondary "Atla", primary "Aç" (right-most).
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 14, 24, 14) };
        footer.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "StrokeSubtleBrush");
        DockPanel.SetDock(footer, Dock.Bottom);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var skip = new Button { Content = "Bu dosyayı atla", IsCancel = true, MinWidth = 96 };
        var ok = new Button { Content = "Aç", IsDefault = true, MinWidth = 96, Margin = new Thickness(0) };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(skip);
        buttons.Children.Add(ok);
        footer.Child = buttons;
        root.Children.Add(footer);

        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 22) };

        // Header: lock tile + title + file name.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var tile = new Border { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0) };
        tile.SetResourceReference(StyleProperty, "AccentIconTile");
        var lockGlyph = new TextBlock { Text = "", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center };
        lockGlyph.SetResourceReference(StyleProperty, "IconText");
        tile.Child = lockGlyph;
        DockPanel.SetDock(tile, Dock.Left);
        header.Children.Add(tile);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = "Bu PDF parola korumalı" };
        title.SetResourceReference(StyleProperty, "SectionHeading");
        title.Margin = new Thickness(0);
        var file = new TextBlock { Text = fileName, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = fileName };
        file.SetResourceReference(StyleProperty, "MutedText");
        file.TextWrapping = TextWrapping.NoWrap;
        titles.Children.Add(title);
        titles.Children.Add(file);
        header.Children.Add(titles);
        body.Children.Add(header);

        if (wrongPrevious)
        {
            var bar = new Border { Margin = new Thickness(0, 0, 0, 16) };
            bar.SetResourceReference(StyleProperty, "ErrorBar");
            var barContent = new DockPanel();
            var barGlyph = new TextBlock { Text = "", Margin = new Thickness(0, 0, 10, 0), FontSize = 14 };
            barGlyph.SetResourceReference(StyleProperty, "IconText");
            barGlyph.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            DockPanel.SetDock(barGlyph, Dock.Left);
            barContent.Children.Add(barGlyph);
            barContent.Children.Add(new TextBlock { Text = "Parola yanlış, tekrar dene.", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            bar.Child = barContent;
            body.Children.Add(bar);
        }

        var label = new TextBlock { Text = "Parola", Margin = new Thickness(0, 0, 0, 7), FontWeight = FontWeights.Medium };
        label.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeSmall");
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        body.Children.Add(label);
        _password = new PasswordBox();
        System.Windows.Automation.AutomationProperties.SetName(_password, "Parola");
        _password.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Accept();
            else if (e.Key == Key.Escape) DialogResult = false;
        };
        body.Children.Add(_password);
        var hint = new TextBlock { Text = "Parola yalnız bu oturumda bellekte tutulur; diske yazılmaz.", Margin = new Thickness(0, 8, 0, 0) };
        hint.SetResourceReference(StyleProperty, "CaptionText");
        body.Children.Add(hint);
        root.Children.Add(body);

        Content = root;
        Loaded += (_, _) => _password.Focus();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
    }

    private string? Result { get; set; }

    private void Accept()
    {
        Result = _password.Password;
        DialogResult = true;
    }

    /// <summary>For the visual check only: the window as it would be shown.</summary>
    internal static PasswordWindow CreateForPreview(string fileName, bool wrongPrevious) => new(fileName, wrongPrevious);

    /// <summary>Returns the entered password, or null when the user skips the file. Must run on the UI thread.</summary>
    internal static string? Ask(string fileName, bool wrongPrevious)
    {
        var window = new PasswordWindow(fileName, wrongPrevious);
        Window? owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        return window.ShowDialog() == true ? window.Result : null;
    }

    // ---------- dark native title bar (Core has no shared helper; the shell's DarkWindowChrome is internal to the app) ----------

    private const int DwmwaUseImmersiveDarkModeOld = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private void ApplyDarkTitleBar()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeOld, ref on, sizeof(int));
            // Unsupported attributes (before Windows 11) are ignored by DWM.
            if (TryFindResource("RailColor") is Color caption) SetColor(handle, DwmwaCaptionColor, caption);
            if (TryFindResource("StrokeSubtleColor") is Color border) SetColor(handle, DwmwaBorderColor, border);
            if (TryFindResource("TextColor") is Color text) SetColor(handle, DwmwaTextColor, text);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private static void SetColor(IntPtr handle, int attribute, Color color)
    {
        int value = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
