using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BroidShot.Core;

/// <summary>
/// Renders a page (or a window's content) with the app theme to a PNG for visual review, and checks that every
/// glyph drawn with the icon font exists in it.
/// </summary>
internal static class PageRenderer
{
    public static string OutputFolder
    {
        get
        {
            string path = Path.Combine(AppContext.BaseDirectory, "render");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>Hosts <paramref name="content"/> like the shell does (window background + page padding) and saves a PNG.</summary>
    public static string RenderPage(FrameworkElement content, string fileName, double width = 1100, double height = 760, bool pagePadding = true, string background = "WindowBackgroundBrush")
    {
        var host = new Border { Width = width, Height = height, Child = content };
        if (pagePadding) host.Padding = (Thickness)Application.Current.FindResource("PagePadding");
        host.Background = (Brush)Application.Current.FindResource(background);
        TextElement.SetFontFamily(host, ThemeResources.UiFontFamily);
        TextElement.SetFontSize(host, ThemeResources.BodyFontSize);
        TextElement.SetForeground(host, (Brush)Application.Current.FindResource("TextBrush"));
        TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(host, TextRenderingMode.ClearType);
        host.UseLayoutRounding = true;
        host.SnapsToDevicePixels = true;
        Layout(host, width, height);
        Pump(TimeSpan.FromMilliseconds(250));
        Layout(host, width, height);

        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        string path = Path.Combine(OutputFolder, fileName);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(path)) encoder.Save(stream);
        host.Child = null;
        return path;
    }

    public static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    public static void Pump(TimeSpan duration, Func<bool>? until = null)
    {
        DateTime end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end && (until is null || !until()))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>Glyphs drawn with the icon font (visible TextBlocks) that the installed icon font does not contain.</summary>
    public static IReadOnlyList<string> MissingIconGlyphs(DependencyObject root)
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string file = File.Exists(Path.Combine(fonts, "SegoeIcons.ttf")) ? Path.Combine(fonts, "SegoeIcons.ttf") : Path.Combine(fonts, "segmdl2.ttf");
        var typeface = new GlyphTypeface(new Uri(file));
        var missing = new List<string>();
        foreach (TextBlock block in Descendants<TextBlock>(root))
        {
            if (!block.FontFamily.Source.Contains("Segoe Fluent Icons", StringComparison.Ordinal) && !block.FontFamily.Source.Contains("MDL2", StringComparison.Ordinal)) continue;
            foreach (char c in block.Text ?? "")
            {
                if (char.IsWhiteSpace(c)) continue;
                if (!typeface.CharacterToGlyphMap.ContainsKey(c)) missing.Add($"U+{(int)c:X4} in \"{block.Name}\"");
            }
        }
        return missing.Distinct().ToArray();
    }

    /// <summary>Visible text drawn in a non-icon font that contains private-use (icon) characters, i.e. a glyph that renders as a box.</summary>
    public static IReadOnlyList<string> GlyphsInTextFont(DependencyObject root) =>
        Descendants<TextBlock>(root)
            .Where(b => b.IsVisible && !b.FontFamily.Source.Contains("Segoe Fluent Icons", StringComparison.Ordinal) && !b.FontFamily.Source.Contains("MDL2", StringComparison.Ordinal))
            .Where(b => (b.Text ?? "").Any(c => c >= '' && c <= ''))
            .Select(b => $"\"{b.Text}\" in {b.Name} ({b.FontFamily.Source})")
            .Distinct().ToArray();

    /// <summary>Gradient test image (BGRA32) that looks like a screenshot thumbnail.</summary>
    public static byte[] SampleImage(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                double fx = x / (double)width, fy = y / (double)height;
                bool window = x > width * 0.12 && x < width * 0.88 && y > height * 0.16 && y < height * 0.84;
                bool bar = window && y < height * 0.26;
                bool block = window && !bar && x > width * 0.18 && x < width * 0.5 && y > height * 0.34 && y < height * 0.74;
                (double r, double g, double b) = (40 + 60 * fx, 44 + 30 * fy, 90 + 100 * (1 - fx));
                if (window) (r, g, b) = (236, 236, 240);
                if (bar) (r, g, b) = (229, 72, 77);
                if (block) (r, g, b) = (70 + 80 * fy, 120, 200);
                pixels[i] = (byte)b; pixels[i + 1] = (byte)g; pixels[i + 2] = (byte)r; pixels[i + 3] = 255;
            }
        }
        return pixels;
    }
}
