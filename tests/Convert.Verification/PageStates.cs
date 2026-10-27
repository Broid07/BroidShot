using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BroidShot.Core;
using BroidShot.Modules.Convert;

internal static partial class Program
{
    /// <summary>The convert page in its states, hosted like the shell does (theme, page padding, 1100×760), plus the password prompt.</summary>
    private static class PageStates
    {
        private const double PageWidth = 1100, PageHeight = 760;

        internal static void Run()
        {
            EnsureTheme();
            string folder = NewFolder("page");
            var module = new ConvertModule();
            var host = new TestHost("convert", Path.Combine(folder, "profile"));
            module.Initialize(host);
            var view = (ConvertView)module.CreateView();
            Window window = HostWindow(view);
            try
            {
                Settle(window);
                var emptyPanel = (FrameworkElement)view.FindName("EmptyPanel");
                var filesPanel = (FrameworkElement)view.FindName("FilesPanel");
                var quick = (ItemsControl)view.FindName("QuickOperations");
                Assert(emptyPanel.IsVisible && !filesPanel.IsVisible, "An empty page shows the drop zone, not the file list.");
                Assert(quick.Items.Count == Enum.GetValues<ConvertOperation>().Length, "The empty page offers a quick start tile per operation.");
                CheckGlyphs(window, "empty");
                Save(window, "convert-1-empty.png");

                // PDFs: one long report, one short, one protected.
                string report = MakePdf(Path.Combine(folder, "Yıllık rapor 2025.pdf"), Enumerable.Repeat(A4, 12).ToArray());
                string brochure = MakePdf(Path.Combine(folder, "broşür.pdf"), A4, A4, A4);
                string locked = MakeEncryptedPdf(Path.Combine(folder, "maaş bordrosu.pdf"), "gizli123");
                OnUi(() => view.AddFilesFromOutside(new[] { report, brochure, locked }));
                var items = ((ItemsControl)view.FindName("FileList")).Items.Cast<ConvertItem>().ToList();
                Pump(() => items.All(i => i.Info.Length > 0));
                Settle(window);
                var tiles = (ListBox)view.FindName("OperationTiles");
                var operationBox = (ComboBox)view.FindName("OperationBox");
                Assert(filesPanel.IsVisible && !emptyPanel.IsVisible, "With files the page shows the list and the options.");
                Assert(tiles.Items.Count == 6 && tiles.SelectedIndex == operationBox.SelectedIndex, "PDFs show six operation tiles; the selected tile follows OperationBox.");
                Assert(items.Single(i => i.Name.StartsWith("maaş", StringComparison.Ordinal)).IsProtected, "The protected PDF is flagged in the list.");
                Assert(items[0].PageCount == 12 && ((TextBlock)view.FindName("ListSummary")).Text.Contains("15 sayfa"),
                    "The list summary counts the 12 + 3 readable PDF pages; the locked file needs its password first.");

                // Picking a tile selects the operation and switches the option rows.
                tiles.SelectedIndex = tiles.Items.Cast<OperationChoice>().ToList().FindIndex(c => c.Operation == ConvertOperation.SplitPdf);
                Settle(window);
                Assert(Selected(operationBox) == ConvertOperation.SplitPdf && ((FrameworkElement)view.FindName("RowSplitMode")).IsVisible
                    && !((FrameworkElement)view.FindName("RowDpi")).IsVisible, "Clicking a tile selects its operation and shows its options.");
                var splitMode = (ListBox)view.FindName("SplitModeBox");
                splitMode.SelectedIndex = 1;
                Settle(window);
                Assert(((FrameworkElement)view.FindName("RowSplitRanges")).IsVisible, "The segmented split mode reveals the range field.");
                splitMode.SelectedItem = null;
                Assert(splitMode.SelectedIndex == 1, "A segmented choice cannot be left empty.");
                splitMode.SelectedIndex = 0;
                tiles.SelectedIndex = 0;
                Settle(window);
                Assert(Selected(operationBox) == ConvertOperation.PdfToImages && ((FrameworkElement)view.FindName("RowDpi")).IsVisible, "PDF → PNG/JPG shows DPI and format.");
                CheckGlyphs(window, "pdf");
                Save(window, "convert-2-pdf-files.png");

                // Converting: first file done, second running, third queued.
                Call(view, "SetRunning", true);
                items[0].SetState(ItemState.Done, "→ 12 dosya (Yıllık rapor 2025_s1.png …)");
                items[1].SetState(ItemState.Working, "", 64);
                items[2].SetState(ItemState.Queued);
                Call(view, "ShowProgress", (double?)0.55, "broşür.pdf: sayfa 2/3");
                Settle(window);
                Assert(!((UIElement)view.FindName("ConvertButton")).IsEnabled && ((UIElement)view.FindName("CancelButton")).IsVisible
                    && ((ProgressBar)view.FindName("RunProgress")).Value > 50, "While running: overall progress and Cancel, Convert disabled.");
                Assert(!((UIElement)view.FindName("OperationTiles")).IsEnabled && !((UIElement)view.FindName("OptionsPanel")).IsEnabled, "Operation and options are locked while running.");
                Save(window, "convert-3-converting.png");
                Call(view, "SetRunning", false);
                foreach (ConvertItem item in items) item.SetState(ItemState.Ready);
                Call(view, "SetStatus", "", false);

                // Mixed list: no operation fits, a warning offers to keep one kind.
                string photo = MakeImage(Path.Combine(folder, "kapak.png"), 640, 400, Gradient);
                OnUi(() => Call(view, "AddFiles", new[] { photo }, false));
                Pump(() => ((ItemsControl)view.FindName("FileList")).Items.Cast<ConvertItem>().All(i => i.Info.Length > 0));
                Settle(window);
                Assert(((UIElement)view.FindName("MixedBar")).IsVisible && !((UIElement)view.FindName("ConvertButton")).IsEnabled, "A PDF + image list shows the mixed-type warning and cannot run.");
                Save(window, "convert-4-mixed.png");
                OnUi(() => Call(view, "KeepOnly", FileKind.Image));
                Assert(((ItemsControl)view.FindName("FileList")).Items.Count == 1 && Selected(operationBox) == ConvertOperation.ConvertImages, "\"Keep images\" drops the PDFs.");

                // Images → JPG with one broken file: a real run, per-file status and the result card.
                string scan = MakeImage(Path.Combine(folder, "tarama 03.jpg"), 900, 1200, Gradient);
                string broken = Path.Combine(folder, "bozuk görsel.png");
                File.WriteAllText(broken, "png değil");
                string logo = MakeImage(Path.Combine(folder, "logo.png"), 256, 256, (x, y) => (40, 40, 220, (byte)(x < 128 ? 255 : 120)));
                OnUi(() => view.AddFilesFromOutside(new[] { scan, broken, logo }));
                var imageItems = ((ItemsControl)view.FindName("FileList")).Items.Cast<ConvertItem>().ToList();
                Pump(() => imageItems.All(i => i.Info.Length > 0));
                Assert(imageItems.Count == 4 && Selected(operationBox) == ConvertOperation.ConvertImages, "Image files keep image conversion selected.");
                var target = (ListBox)view.FindName("TargetBox");
                target.SelectedIndex = 1; // JPG
                ((ListBox)view.FindName("ResizeModeBox")).SelectedIndex = 2; // max size
                Settle(window);
                Assert(((FrameworkElement)view.FindName("RowJpgQuality")).IsVisible && ((FrameworkElement)view.FindName("RowMaxSize")).IsVisible, "JPG target shows quality; \"Sınırla\" shows the size fields.");
                var convert = (Button)view.FindName("ConvertButton");
                OnUi(() => convert.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
                Pump(() => convert.IsEnabled && imageItems.All(i => !i.IsBusy), 60000);
                Settle(window);
                ConvertItem failed = imageItems.Single(i => i.Name.StartsWith("bozuk", StringComparison.Ordinal));
                Assert(failed.State == ItemState.Failed && failed.StatusText.Length > 0, $"The broken image shows its error on its row ({failed.State}: {failed.StatusText}).");
                Assert(imageItems.Where(i => i != failed).All(i => i.State == ItemState.Done && i.StatusText.StartsWith("→", StringComparison.Ordinal)),
                    $"Converted images show their output ({string.Join("; ", imageItems.Select(i => i.State + " " + i.StatusText))}).");
                Assert(((UIElement)view.FindName("ResultCard")).IsVisible && ((UIElement)view.FindName("OpenFolderButton")).IsVisible
                    && ((TextBlock)view.FindName("ResultTitle")).Text == "Kısmen tamamlandı", "The result card summarises the batch and offers \"Klasörü aç\".");
                Assert(File.Exists(Path.Combine(folder, "logo.jpg")), "The real run wrote the outputs.");
                CheckGlyphs(window, "done");
                Save(window, "convert-5-images-done.png");

                // Operation change clears the stale statuses.
                operationBox.SelectedIndex = 0;
                Assert(imageItems.All(i => i.State == ItemState.Ready), "Changing the operation clears the previous results from the rows.");
            }
            finally
            {
                window.Close();
                module.Shutdown();
            }
        }

        internal static void PasswordPrompt()
        {
            EnsureTheme();
            foreach (bool wrong in new[] { false, true })
            {
                PasswordWindow window = PasswordWindow.CreateForPreview("maaş bordrosu Ekim 2026.pdf", wrong);
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -30000;
                window.Top = -30000;
                window.ShowActivated = false;
                window.Topmost = false;
                window.Show();
                try
                {
                    Settle(window);
                    Assert(window.FontFamily.Source.Contains("Inter", StringComparison.Ordinal) && window.Background is SolidColorBrush { Color.R: < 40 },
                        "The password window uses Inter on the dark window background.");
                    Assert(Descendants(window).OfType<PasswordBox>().Count() == 1, "The prompt has one password field.");
                    Assert(Descendants(window).OfType<TextBlock>().Any(t => t.Text.Contains("yanlış", StringComparison.Ordinal)) == wrong, "The wrong-password notice appears only after a wrong attempt.");
                    CheckGlyphs(window, "password");
                    if (renderDir is not null)
                    {
                        var content = (FrameworkElement)window.Content;
                        Visuals.SavePng(content, content.ActualWidth, content.ActualHeight, Path.Combine(renderDir, wrong ? "password-2-wrong.png" : "password-1.png"));
                    }
                }
                finally
                {
                    window.Close();
                }
            }
        }

        // ---------- helpers ----------

        private static void EnsureTheme()
        {
            Application app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (app.TryFindResource("SurfaceBrush") is null) app.Resources.MergedDictionaries.Add(ThemeResources.Load());
        }

        /// <summary>Hosts the page like the shell: dark window, page padding, off screen.</summary>
        private static Window HostWindow(FrameworkElement view)
        {
            var padded = new Border { Child = view };
            padded.SetResourceReference(Border.PaddingProperty, "PagePadding");
            padded.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");
            var window = new Window
            {
                Content = padded,
                Width = PageWidth,
                Height = PageHeight,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000,
                Top = -30000,
            };
            window.Show();
            return window;
        }

        private static void Save(Window window, string name)
        {
            if (renderDir is null) return;
            var content = (FrameworkElement)window.Content;
            Visuals.SavePng(content, content.ActualWidth, content.ActualHeight, Path.Combine(renderDir, name));
        }

        private static ConvertOperation? Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as ConvertOperation?;

        private static object? Call(object target, string method, params object?[] args)
        {
            MethodInfo info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new MissingMethodException(target.GetType().Name, method);
            return info.Invoke(target, args);
        }

        /// <summary>Runs on the dispatcher (so async continuations come back to the UI thread) and waits for it.</summary>
        private static void OnUi(Action action)
        {
            bool done = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => { try { action(); } finally { done = true; } }));
            Pump(() => done);
        }

        private static void Pump(Func<bool> until, int timeoutMs = 20000)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                if (until()) return;
                if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("The page did not reach the expected state in time.");
                Thread.Sleep(15);
            }
        }

        private static void Settle(Window window)
        {
            window.UpdateLayout();
            Pump(() => true);
            window.UpdateLayout();
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (DependencyObject nested in Descendants(child)) yield return nested;
            }
        }

        private static List<GlyphTypeface>? iconFaces;

        /// <summary>Every visible glyph drawn with the icon font exists in Segoe Fluent Icons or Segoe MDL2 Assets (no empty boxes).</summary>
        private static void CheckGlyphs(DependencyObject root, string state)
        {
            iconFaces ??= Fonts.SystemTypefaces
                .Where(t => t.FontFamily.Source is "Segoe Fluent Icons" or "Segoe MDL2 Assets")
                .Select(t => t.TryGetGlyphTypeface(out GlyphTypeface face) ? face : null)
                .OfType<GlyphTypeface>().ToList();
            if (iconFaces.Count == 0) return;
            var glyphs = new HashSet<char>();
            foreach (TextBlock text in Descendants(root).OfType<TextBlock>())
                if (text.FontFamily.Source.Contains("Segoe Fluent Icons", StringComparison.Ordinal) || text.FontFamily.Source.Contains("MDL2", StringComparison.Ordinal))
                    foreach (char c in text.Text) if (c >= 0xE000) glyphs.Add(c);
            Assert(glyphs.Count > 0, $"{state}: icons are drawn with the icon font.");
            char[] missing = glyphs.Where(c => !iconFaces.Any(f => f.CharacterToGlyphMap.ContainsKey(c))).ToArray();
            Assert(missing.Length == 0, $"{state}: missing icon glyphs {string.Join(", ", missing.Select(c => "U+" + ((int)c).ToString("X4")))}.");
        }
    }
}
