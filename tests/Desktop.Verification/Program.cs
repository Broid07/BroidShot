using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BroidShot;
using BroidShot.Core;
using BroidShot.Lifecycle;
using BroidShot.Shell;

internal static class Program
{
    internal static int Assertions;
    internal static int Result = 1;
    internal static string Root = "";
    [STAThread]
    private static int Main(string[] args)
    {
        Root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("BROIDSHOT_DATA_DIR", Path.Combine(Root, "profile"));
        var app = new VerificationApplication();
        app.Resources = (ResourceDictionary)Application.LoadComponent(new Uri("/BroidShot;component/Theme.xaml", UriKind.Relative));
        app.Run();
        return Result;
    }
    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Assertions++;
    }
}

internal sealed class VerificationApplication : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Exercise the production window and resources with an isolated profile and fake
        // startup entry. This is an application integration test, not desktop input injection.
        try
        {
            await SeedHistoryAsync();
            var preferences = new UserPreferences { F12Enabled = false, RunInBackground = true, KeepHistory = false, HotkeyPreset = 2 };
            var entry = new MemoryStartupEntry();
            var startup = new StartupService(entry, () => @"""C:\BroidShot\BroidShot.exe"" --autostart");
            var probe = new ProbeModule();
            var broken = new BrokenModule();
            var modules = ModuleRegistry.CreateModules().Concat(new IToolModule[] { new BroidShot.Modules.Sample.SampleModule(), probe, broken, new BrokenViewModule() }).ToArray();
            var window = new MainWindow(preferences, startup, modules);
            MainWindow = window;
            window.InitializeSession();
            Program.Assert(new WindowInteropHelper(window).Handle != IntPtr.Zero && !window.IsVisible,
                "Tray-only initialization creates the shortcut/message window without showing the main UI.");
            Program.Assert(entry.WriteCount == 0, "Launching the application must not enable Windows startup.");
            var start = (CheckBox)window.FindName("StartWithWindowsCheck");
            var tray = (CheckBox)window.FindName("StartInTrayCheck");
            var background = (CheckBox)window.FindName("RunInBackgroundCheck");
            Program.Assert(start.IsChecked == false && !tray.IsEnabled, "Startup checkbox reflects the actual registration.");
            start.IsChecked = true;
            Program.Assert(startup.IsEnabled && tray.IsEnabled, "Startup toggle writes registration and enables tray-start preference.");
            background.IsChecked = false;
            Program.Assert(!preferences.RunInBackground && !tray.IsEnabled, "Disabling background prevents hidden startup.");
            background.IsChecked = true;
            start.IsChecked = false;
            Program.Assert(!startup.IsEnabled && !tray.IsEnabled, "Turning startup off removes the registration and disables its child option.");

            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Program.Assert(window.IsVisible && window.IsLoaded, "A hidden startup session can reveal the main window.");
            Program.Assert(window.FontFamily.Source.Contains("#Inter") && window.FontSize == 14,
                "Main window uses the bundled Inter 14px UI typography.");
            Program.Assert(TextOptions.GetTextFormattingMode(window) == TextFormattingMode.Display && TextOptions.GetTextRenderingMode(window) == TextRenderingMode.ClearType,
                "UI text uses pixel-aligned Display/ClearType rendering.");
            Render(window, "main-window.png");
            RenderScaled(window, "main-window@150.png", 1.5);
            VerifyTheme(window);
            ((Button)window.FindName("SettingsNavButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Render(window, "settings-window.png");
            await VerifyModulesAsync(window, probe, broken);

            bool actuallyClosed = false;
            window.Closed += (_, _) => actuallyClosed = true;
            window.Close();
            Program.Assert(!window.IsVisible && !actuallyClosed, "Close hides the window and keeps the session alive when background mode is enabled.");
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Program.Assert(window.IsVisible, "The tray session can be reopened after closing to background.");
            background.IsChecked = false;
            Program.Assert(!UserPreferences.Load().RunInBackground, "Background mode selection persists before exiting.");
            bool closingObserved = false;
            window.Closing += (_, args) =>
            {
                closingObserved = true;
                Program.Assert(!args.Cancel, "Close is not cancelled when background mode is disabled.");
            };
            Exit += (_, _) =>
            {
                if (!closingObserved) return;
                if (probe.ShutdownCount != 1 || broken.ShutdownCount != 0)
                {
                    Console.Error.WriteLine("Module shutdown must run exactly once, and only for initialized modules.");
                    return;
                }
                Program.Result = 0;
                Console.WriteLine($"PASS: {Program.Assertions} desktop lifecycle/typography assertions; startup writes used an isolated in-memory store.");
                Console.WriteLine("Rendered: " + Program.Root);
            };
            window.Close();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Program.Result = 1; Shutdown(1); }
    }

    private static async Task VerifyModulesAsync(MainWindow window, ProbeModule probe, BrokenModule broken)
    {
        var nav = (StackPanel)window.FindName("ModuleNav");
        string[] names = nav.Children.OfType<Button>().Select(b => AutomationProperties.GetName(b)).ToArray();
        IToolModule[] registered = ModuleRegistry.CreateModules().ToArray();
        Program.Assert(registered.Length > 0 && names.SequenceEqual(new[] { "Ekran alıntısı" }.Concat(registered.Select(m => m.Title))
                .Concat(new[] { "Örnek modül", "Sonda modülü", "Bozuk modül", "Sayfası bozuk modül" })),
            "Sidebar lists the built-in page and registered modules in registry order: " + string.Join(", ", names));
        string[] headers = nav.Children.OfType<FrameworkElement>().Where(e => Equals(e.Tag, "rail-group")).Select(e => AutomationProperties.GetName(e)).ToArray();
        string[] expectedHeaders = new[] { ToolCategories.Capture }.Concat(registered.Select(m => m.Category))
            .Concat(new[] { ToolCategories.Developer, "Doğrulama" }).Distinct().ToArray();
        Program.Assert(headers.SequenceEqual(expectedHeaders), "Modules are grouped by category in first-appearance order: " + string.Join(", ", headers));
        VerifyRail(window, nav);
        Program.Assert(probe.InitializeCount == 1 && probe.Host is { ModuleId: "probe" }, "Healthy modules are initialized once with a scoped host.");
        Program.Assert(probe.Host!.DataFolder == Path.Combine(Program.Root, "profile", "modules", "probe", "data") && Directory.Exists(probe.Host.DataFolder),
            "Module data folder lives under the isolated data root.");
        Program.Assert(broken.ShutdownCount == 0, "Failed module is not shut down early.");

        Program.Assert(window.NavigateToModule("sample") && window.CurrentPageId == "sample" && window.CurrentPage?.GetType().Name == "SampleView",
            "Navigating shows the sample module view.");
        Program.Assert(((FrameworkElement)window.FindName("ScreenshotPage")).Visibility == Visibility.Collapsed, "Only the selected page is visible.");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Render(window, "module-page.png");
        Program.Assert(window.CurrentPage is { } samplePage && FindAncestor<ScrollViewer>(samplePage) is not null,
            "A module page whose root is a plain StackPanel is hosted in a ScrollViewer so long content can scroll.");
        Program.Assert(window.NavigateToModule("broken") && Equals(window.CurrentPage?.Tag, "module-error"), "A module whose Initialize failed shows an error page.");
        Program.Assert(window.NavigateToModule("broken-view") && Equals(window.CurrentPage?.Tag, "module-error"), "A module whose CreateView failed shows an error page.");
        Program.Assert(!window.NavigateToModule("missing"), "Unknown module ids are rejected.");

        var toast = window.SearchPalette("bildirim");
        Program.Assert(toast.Count > 0 && toast[0].Title == "Örnek bildirim göster", "Palette finds the sample command by keyword.");
        Program.Assert(window.SearchPalette("alan sec").Any(e => e.Title == "Alan seç"), "Palette finds built-in capture commands without Turkish characters.");
        Program.Assert(window.SearchPalette("ornek").Any(e => e.IsModule && e.Title == "Örnek modül"), "Palette lists module pages.");
        var bozuk = window.SearchPalette("bozuk");
        Program.Assert(bozuk.Any(e => e.Title == "Bozuk modül") && !bozuk.Any(e => e.Title == "Bozuk komut"), "Commands of failed modules are hidden.");
        Program.Assert(window.SearchPalette("ayar").Any(e => e.Title == "Ayarlar"), "Settings are reachable from the palette.");

        await window.RunPaletteEntryAsync(toast[0]);
        Program.Assert(window.Toasts.VisibleCount == 1, "Running the sample command shows a toast.");
        string sampleSettings = Path.Combine(Program.Root, "profile", "modules", "sample", "settings.json");
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(sampleSettings)))
            Program.Assert(json.RootElement.GetProperty("ToastCount").GetInt32() == 1, "Module settings are stored under <data>/modules/<id>/settings.json.");

        window.ShowCommandPalette();
        CommandPaletteWindow palette = window.Palette ?? throw new InvalidOperationException("Palette did not open.");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        RenderElement((FrameworkElement)palette.Content, "palette.png");
        palette.Query = "örnek bildirim";
        Program.Assert(palette.Results.Count > 0 && palette.Results[0].Title == "Örnek bildirim göster", "Palette window filters as you type.");
        palette.RunSelected();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(50);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Program.Assert(window.Palette is null && window.Toasts.VisibleCount == 2, "Enter closes the palette and runs the selected command.");
        window.Toasts.Container.UpdateLayout();
        RenderElement(window.Toasts.Container, "toasts.png");
        Program.Assert(window.CurrentPageId == "broken-view", "A command without navigation keeps the current page.");

        string? got = null;
        using (window.Messages.Subscribe<UrlCopiedMessage>(m => got = m.Url))
            probe.Host.Messages.Publish(new UrlCopiedMessage("https://video.test/1", true, null, DateTimeOffset.Now));
        Program.Assert(got == "https://video.test/1", "Modules share one message bus.");

        ToolJob job = window.Jobs.Start("Doğrulama işi");
        job.Report(0.4, "yüzde 40");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Program.Assert(((FrameworkElement)window.FindName("JobsPanel")).Visibility == Visibility.Visible, "Running jobs appear in the icon rail.");
        var jobsPopup = (System.Windows.Controls.Primitives.Popup)window.FindName("JobsPopup");
        jobsPopup.IsOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        RenderElement((FrameworkElement)jobsPopup.Child, "jobs-popup.png");
        jobsPopup.IsOpen = false;
        job.Cancel();
        Program.Assert(job.IsCancellationRequested && job.Token.IsCancellationRequested, "Job cancel reaches the job token.");
        job.Cancelled();

        Program.Assert(window.NavigateToModule("screenshot") && window.CurrentPageId == "screenshot", "Screenshot page is reachable again.");
        Program.Assert(((TextBlock)window.FindName("PageTitle")).Text == "Ekran alıntısı", "The top bar shows the current page name.");
        Program.Assert(((TextBlock)window.FindName("HeroTitle")).Text == "Alıntıların, elinin altında.", "The screenshot page keeps its heading.");
    }

    /// <summary>Rail shape: icon buttons with tooltips, glyphs that exist in the icon font, one active entry.</summary>
    private static void VerifyRail(MainWindow window, StackPanel nav)
    {
        object railStyle = window.FindResource("RailButton");
        Button[] buttons = nav.Children.OfType<Button>().ToArray();
        Program.Assert(buttons.All(b => ReferenceEquals(b.Style, railStyle) && b.ToolTip is not null),
            "Every rail entry is an icon-only RailButton with a hover tooltip.");
        string[] missing = buttons.Where(b => b.Content is not string glyph || !GlyphExists(glyph)).Select(b => AutomationProperties.GetName(b)).ToArray();
        Program.Assert(missing.Length == 0, "Every rail glyph exists in Segoe Fluent Icons / Segoe MDL2 Assets (no empty boxes): " + string.Join(", ", missing));
        var settings = (Button)window.FindName("SettingsNavButton");
        Program.Assert(ReferenceEquals(settings.Style, railStyle) && GlyphExists((string)settings.Content), "Settings is pinned at the bottom of the rail.");
        Program.Assert(Equals(settings.Tag, "selected") && buttons.All(b => b.Tag is null), "Exactly one rail entry is marked active.");
        Program.Assert(KeyboardNavigation.GetTabNavigation(nav) == KeyboardNavigationMode.Once
                       && KeyboardNavigation.GetDirectionalNavigation(nav) == KeyboardNavigationMode.Cycle,
            "The rail is one Tab stop and is walked with the arrow keys.");
    }

    /// <summary>Theme regressions: glyph buttons keep the icon font, windows inherit the UI font, placeholders render.</summary>
    private static void VerifyTheme(MainWindow window)
    {
        string close = char.ConvertFromUtf32(0xE711);
        var iconButton = new Button { Content = close, Style = (Style)window.FindResource("IconButton") };
        var host = new StackPanel();
        host.Children.Add(iconButton);
        var placeholderBox = new TextBox { Style = (Style)window.FindResource("SearchBox") };
        BroidShot.Core.ThemeAssist.SetPlaceholder(placeholderBox, "Ara…");
        host.Children.Add(placeholderBox);
        var iconTextButton = new Button { Content = "Kopyala" };
        BroidShot.Core.ThemeAssist.SetIcon(iconTextButton, char.ConvertFromUtf32(0xE8C8));
        host.Children.Add(iconTextButton);
        var probe = new ProbeWindow { Content = host, Width = 300, Height = 200, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        probe.Show();
        probe.UpdateLayout();
        try
        {
            TextBlock? glyph = Descendants<TextBlock>(iconButton).FirstOrDefault(t => t.Text == close);
            Program.Assert(glyph is not null && glyph.FontFamily.Source.Contains("Segoe Fluent Icons"),
                "IconButton glyph content renders with the icon font (implicit TextBlock style must not override it): " + glyph?.FontFamily.Source);
            Program.Assert(probe.FontFamily.Source.Contains("#Inter") && probe.FontSize == 14,
                "Windows created by modules inherit the theme font and size: " + probe.FontFamily.Source);
            TextBlock? hint = Descendants<TextBlock>(placeholderBox).FirstOrDefault(t => t.Text == "Ara…");
            Program.Assert(hint is { IsVisible: true }, "ThemeAssist.Placeholder shows while a TextBox is empty.");
            TextBlock? leading = Descendants<TextBlock>(iconTextButton).FirstOrDefault(t => t.Text == char.ConvertFromUtf32(0xE8C8));
            Program.Assert(leading is { IsVisible: true } && leading.FontFamily.Source.Contains("Segoe Fluent Icons"), "ThemeAssist.Icon draws a leading glyph on buttons.");
        }
        finally { probe.Close(); }
        VerifyTypography(window);
    }

    /// <summary>Bundled Inter: real (not synthesized) static faces for every weight, full Turkish coverage, plus a specimen render.</summary>
    private static void VerifyTypography(MainWindow window)
    {
        const string turkish = "ğüşıöçİĞÜŞÖÇâîû";
        var ui = (FontFamily)window.FindResource("UiFont");
        foreach (FontWeight weight in new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold, FontWeights.Bold })
        {
            var typeface = new Typeface(ui, FontStyles.Normal, weight, FontStretches.Normal);
            Program.Assert(typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs) && glyphs.FamilyNames.Values.Any(n => n.StartsWith("Inter", StringComparison.Ordinal))
                           && glyphs.Weight == weight && !typeface.IsBoldSimulated,
                $"Inter {weight} resolves to its own bundled static face.");
            Program.Assert(turkish.All(c => glyphs.CharacterToGlyphMap.ContainsKey(c)), $"Inter {weight} covers Turkish letters {turkish}.");
        }

        var specimen = new StackPanel { Margin = new Thickness(24), Background = (Brush)window.FindResource("WindowBackgroundBrush") };
        foreach ((double size, FontWeight weight) in new[] { (28d, FontWeights.SemiBold), (20d, FontWeights.SemiBold), (16d, FontWeights.SemiBold), (14d, FontWeights.Medium), (14d, FontWeights.Normal), (13d, FontWeights.Normal), (12d, FontWeights.Normal) })
        {
            var line = new TextBlock { Text = $"{size:0} · Çağrı İşığı ölçüsüz güç: ğüşıöçİĞÜŞÖÇ 0123", FontSize = size, FontWeight = weight, Margin = new Thickness(0, 0, 0, 8),
                Foreground = (Brush)window.FindResource("TextBrush") };
            if (size >= 20) TextOptions.SetTextFormattingMode(line, TextFormattingMode.Ideal);
            specimen.Children.Add(line);
        }
        var host = new Window { Content = new Border { Child = specimen, Background = (Brush)window.FindResource("WindowBackgroundBrush") }, SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None };
        host.Show();
        host.UpdateLayout();
        try { RenderElement((FrameworkElement)host.Content, "typography.png"); }
        finally { host.Close(); }
    }

    private static bool GlyphExists(string glyph)
    {
        if (glyph.Length == 0) return false;
        foreach (string family in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs) && glyph.All(c => glyphs.CharacterToGlyphMap.ContainsKey(c))) return true;
        }
        return false;
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    /// <summary>A few history entries so the screenshot page renders real cards.</summary>
    private static async Task SeedHistoryAsync()
    {
        var store = new BroidShot.Services.HistoryStore(Path.Combine(Program.Root, "profile", "history"));
        Color[] tints = { Color.FromRgb(0x2D, 0x5B, 0xD8), Color.FromRgb(0x1F, 0x8A, 0x5B), Color.FromRgb(0xC2, 0x4A, 0x2E), Color.FromRgb(0x6B, 0x3F, 0xB5) };
        for (int i = 0; i < tints.Length; i++)
        {
            int width = i % 2 == 0 ? 1280 : 900, height = i % 2 == 0 ? 720 : 900;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF6)), null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new SolidColorBrush(tints[i]), null, new Rect(0, 0, width, height * 0.12));
                for (int line = 0; line < 6; line++)
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xD5, 0xD7, 0xDD)), null, new Rect(width * 0.08, height * (0.22 + line * 0.1), width * (0.5 + (line % 3) * 0.12), height * 0.04), 6, 6);
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            await store.AddAsync(bitmap, i == 1 ? "Örnek metin" : null);
        }
    }

    private static void Render(Window window, string filename) => RenderElement((FrameworkElement)window.Content, filename);

    private static void RenderScaled(Window window, string filename, double scale) => RenderElement((FrameworkElement)window.Content, filename, scale);

    private static void RenderElement(FrameworkElement element, string filename, double scale = 1)
    {
        // Draw through a VisualBrush so the element's own offset (margins) does not shift or crop the picture.
        var size = new Size(Math.Ceiling(element.ActualWidth), Math.Ceiling(element.ActualHeight));
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill, Viewbox = new Rect(VisualTreeHelper.GetOffset(element).X, VisualTreeHelper.GetOffset(element).Y, size.Width, size.Height), ViewboxUnits = BrushMappingMode.Absolute }, null, new Rect(size));
        var image = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(Program.Root, filename));
        encoder.Save(stream);
    }

    private sealed class ProbeWindow : Window { }

    private sealed class MemoryStartupEntry : IStartupEntryStore
    {
        private string? value;
        internal int WriteCount;
        public string? Read() => value;
        public void Write(string command) { value = command; WriteCount++; }
        public void Delete() => value = null;
    }
}
