using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BroidShot.Capture;

internal static class Program
{
    private static int assertions;

    [STAThread]
    private static int Main()
    {
        try { VerifyF12State(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        int result = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            Window? target = null;
            Window? cover = null;
            try
            {
                var background = Color.FromRgb(21, 183, 91);
                target = SyntheticWindow("BroidShot verification target", background);
                cover = SyntheticWindow("BroidShot verification occluder", Color.FromRgb(231, 43, 62));
                target.Show();
                await Task.Delay(400);
                cover.Topmost = true;
                cover.Show();
                cover.Activate();
                await Task.Delay(400);

                IntPtr handle = new WindowInteropHelper(target).Handle;
                var capture = new CaptureService();
                CaptureSnapshot snapshot = await Task.Run(() => capture.CaptureWindow(handle));
                Assert(snapshot.Image.IsFrozen, "Window capture must be frozen for cross-thread use.");
                Assert(snapshot.Image.PixelWidth == snapshot.Bounds.Width && snapshot.Image.PixelHeight == snapshot.Bounds.Height,
                    "Captured pixels must match physical snapshot bounds.");
                Assert(snapshot.Image.PixelWidth >= 200 && snapshot.Image.PixelHeight >= 120, "Synthetic window size was lost.");
                Color actual = ReadPixel(snapshot.Image, snapshot.Image.PixelWidth / 2, snapshot.Image.PixelHeight / 2);
                Assert(Math.Abs(actual.R - background.R) <= 2 && Math.Abs(actual.G - background.G) <= 2 && Math.Abs(actual.B - background.B) <= 2,
                    $"Occluded window returned wrong pixels: {actual}; expected {background} (not the red occluder).");
                Console.WriteLine("PASS: a green WPF window covered by a red topmost WPF window captured its own green content.");

                target.WindowStyle = WindowStyle.SingleBorderWindow;
                target.ResizeMode = ResizeMode.CanResize;
                await Task.Delay(250);
                PixelRect? frame = capture.GetWindowBounds(handle);
                CaptureSnapshot decorated = await Task.Run(() => capture.CaptureWindow(handle));
                Assert(frame.HasValue && decorated.Bounds == frame.Value,
                    "Decorated-window capture must crop the invisible resize border to the DWM frame bounds.");
                Color decoratedPixel = ReadPixel(decorated.Image, decorated.Image.PixelWidth / 2, decorated.Image.PixelHeight / 2);
                Assert(Math.Abs(decoratedPixel.G - background.G) <= 2 && Math.Abs(decoratedPixel.R - background.R) <= 2,
                    "Decorated-window capture returned occluding or misaligned pixels.");
                Console.WriteLine("PASS: a covered decorated window matched its DWM frame bounds and retained its own content.");

                target.Hide();
                bool rejected = false;
                try { await Task.Run(() => capture.CaptureWindow(handle)); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "Hidden windows must be explicitly rejected rather than replaced with desktop pixels.");
                Console.WriteLine($"PASS: {assertions} assertions (F12 state, occluded-window pixels, DWM border crop, dimensions and hidden-window failure).");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { cover?.Close(); target?.Close(); app.Shutdown(); }
        };
        app.Run();
        return result;
    }

    private static Window SyntheticWindow(string title, Color color) => new()
    {
        Title = title, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        Left = 120, Top = 120, Width = 280, Height = 180, ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Content = new Border { Background = new SolidColorBrush(color) },
        Background = new SolidColorBrush(color)
    };

    private static void VerifyF12State()
    {
        var state = new F12KeyState();
        Assert(state.Process(true, false) == new KeyDecision(true, true), "Plain F12 triggers and is consumed.");
        Assert(state.Process(true, false) == new KeyDecision(true, false), "Held F12 repeat must not trigger again.");
        Assert(state.Process(true, true) == new KeyDecision(true, false), "Modifier added while held must not create a trigger.");
        Assert(state.Process(false, true) == new KeyDecision(true, false), "A consumed press must consume its release.");
        Assert(state.Process(true, true) == new KeyDecision(false, false), "Modified F12 must reach the target app.");
        Assert(state.Process(true, false) == new KeyDecision(false, false), "Releasing a modifier while F12 is held must not trigger.");
        Assert(state.Process(false, false) == new KeyDecision(false, false), "Modified F12 release must reach the target app.");
        Assert(state.Process(true, false) == new KeyDecision(true, true), "A new plain press must trigger again.");
        state.Reset();
        Assert(state.Process(true, false) == new KeyDecision(true, true), "Reset must clear held state.");
    }

    private static Color ReadPixel(BitmapSource source, int x, int y)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }
}
