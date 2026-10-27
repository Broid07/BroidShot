using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace BroidShot.Capture;

/// <summary>
/// HWND-directed PrintWindow backend. It never substitutes a desktop crop, which could
/// include an unrelated overlapping application. GPU/protected applications may reject it.
/// </summary>
internal static class WindowCaptureBackend
{
    private static int activeWorker;
    private const uint PwRenderFullContent = 2;
    private const uint Blackness = 0x00000042;

    public static CaptureSnapshot Capture(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new ArgumentException("Bir pencere seçilmedi.", nameof(handle));
        if (Interlocked.CompareExchange(ref activeWorker, 1, 0) != 0)
            throw new InvalidOperationException("Önceki pencere yakalama isteğinin yanıtı bekleniyor. Biraz sonra yeniden dene.");

        var completion = new TaskCompletionSource<CaptureSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.TrySetResult(CaptureCore(handle)); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { Volatile.Write(ref activeWorker, 0); }
        }) { IsBackground = true, Name = "BroidShot window capture" };
        try { worker.Start(); }
        catch { Volatile.Write(ref activeWorker, 0); throw; }

        // PrintWindow is synchronous and can block inside another process. Do not abandon
        // native resources from this thread: the worker owns and releases them on return.
        var finished = Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
        if (finished != completion.Task)
        {
            _ = completion.Task.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new TimeoutException("Seçilen pencere yakalama isteğine yanıt vermedi. Başka bir pencere veya alan seçimini dene.");
        }
        return completion.Task.GetAwaiter().GetResult();
    }

    private static CaptureSnapshot CaptureCore(IntPtr handle)
    {
        IntPtr previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            ValidateWindow(handle);
            NativeRect raw = GetRawBounds(handle);
            NativeRect frame = GetFrameBounds(handle, raw);
            int width = raw.Right - raw.Left;
            int height = raw.Bottom - raw.Top;
            if (width <= 0 || height <= 0 || (long)width * height > 128L * 1024 * 1024)
                throw new InvalidOperationException("Pencerenin yakalama boyutları geçersiz veya çok büyük.");

            IntPtr desktopDc = IntPtr.Zero;
            IntPtr memoryDc = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;
            IntPtr previousBitmap = IntPtr.Zero;
            try
            {
                desktopDc = GetDC(IntPtr.Zero);
                if (desktopDc == IntPtr.Zero) throw NativeFailure("Yakalama görüntüsüne erişilemedi");
                memoryDc = CreateCompatibleDC(desktopDc);
                if (memoryDc == IntPtr.Zero) throw NativeFailure("Pencere yakalama belleği oluşturulamadı");
                bitmap = CreateCompatibleBitmap(desktopDc, width, height);
                if (bitmap == IntPtr.Zero) throw NativeFailure("Pencere görüntüsü oluşturulamadı");
                previousBitmap = SelectObject(memoryDc, bitmap);
                if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
                    throw NativeFailure("Pencere yakalama görüntüsü hazırlanamadı");
                // A successful but unsupported WM_PRINT handler must not expose stale GDI memory.
                if (!PatBlt(memoryDc, 0, 0, width, height, Blackness))
                    throw NativeFailure("Pencere yakalama görüntüsü temizlenemedi");
                if (!PrintWindow(handle, memoryDc, PwRenderFullContent))
                    throw new InvalidOperationException("Bu uygulama doğrudan pencere yakalamayı desteklemiyor. Alan seçimini kullanabilirsin.");

                BitmapSource image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                ValidateWindow(handle);
                if (!raw.Equals(GetRawBounds(handle)))
                    throw new InvalidOperationException("Pencere yakalama sırasında taşındı veya boyutu değişti. Lütfen yeniden dene.");

                // GetWindowRect contains the resize border hidden by DWM. PrintWindow renders
                // the full HWND, so crop that border only after capturing at its full dimensions.
                int cropX = Math.Clamp(frame.Left - raw.Left, 0, width - 1);
                int cropY = Math.Clamp(frame.Top - raw.Top, 0, height - 1);
                int cropWidth = Math.Min(frame.Right - frame.Left, width - cropX);
                int cropHeight = Math.Min(frame.Bottom - frame.Top, height - cropY);
                var cropped = new CroppedBitmap(image, new Int32Rect(cropX, cropY, cropWidth, cropHeight));
                cropped.Freeze();
                RejectEmptyRendering(cropped);
                return new CaptureSnapshot(cropped,
                    new PixelRect(raw.Left + cropX, raw.Top + cropY, cropWidth, cropHeight),
                    Array.Empty<MonitorInfo>());
            }
            finally
            {
                if (memoryDc != IntPtr.Zero && previousBitmap != IntPtr.Zero && previousBitmap != new IntPtr(-1))
                    SelectObject(memoryDc, previousBitmap);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                if (desktopDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, desktopDc);
            }
        }
        finally { if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi); }
    }

    private static void ValidateWindow(IntPtr handle)
    {
        if (!IsWindow(handle) || !IsWindowVisible(handle))
            throw new InvalidOperationException("Seçilen pencere kapatıldı veya artık görünür değil.");
        if (IsIconic(handle))
            throw new InvalidOperationException("Küçültülmüş pencere yakalanamıyor. Pencereyi geri açıp yeniden seç.");
        if (IsHungAppWindow(handle))
            throw new InvalidOperationException("Seçilen uygulama yanıt vermiyor; şu anda pencere görüntüsü alınamıyor.");
        if (DwmGetWindowAttributeInt(handle, 14, out int cloaked, sizeof(int)) >= 0 && cloaked != 0)
            throw new InvalidOperationException("Seçilen pencere başka bir sanal masaüstünde veya sistem tarafından gizlenmiş.");
        if (GetWindowDisplayAffinity(handle, out uint affinity) && affinity != 0)
            throw new InvalidOperationException("Seçilen pencere ekran yakalamaya karşı korumalı.");
    }

    private static NativeRect GetRawBounds(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var rectangle)) throw NativeFailure("Pencerenin boyutları okunamadı");
        return rectangle;
    }

    private static NativeRect GetFrameBounds(IntPtr handle, NativeRect raw)
    {
        if (DwmGetWindowAttribute(handle, 9, out var frame, Marshal.SizeOf<NativeRect>()) < 0
            || frame.Right <= frame.Left || frame.Bottom <= frame.Top) return raw;
        // DWM and GetWindowRect can temporarily disagree during a transition.
        frame.Left = Math.Max(frame.Left, raw.Left);
        frame.Top = Math.Max(frame.Top, raw.Top);
        frame.Right = Math.Min(frame.Right, raw.Right);
        frame.Bottom = Math.Min(frame.Bottom, raw.Bottom);
        return frame.Right > frame.Left && frame.Bottom > frame.Top ? frame : raw;
    }

    private static void RejectEmptyRendering(BitmapSource image)
    {
        int bytesPerPixel = (image.Format.BitsPerPixel + 7) / 8;
        byte[] row = new byte[image.PixelWidth * bytesPerPixel];
        for (int y = 0; y < image.PixelHeight; y++)
        {
            image.CopyPixels(new Int32Rect(0, y, image.PixelWidth, 1), row, row.Length, 0);
            for (int x = 0; x < image.PixelWidth; x++)
            {
                int offset = x * bytesPerPixel;
                if (row[offset] != 0 || row[offset + 1] != 0 || row[offset + 2] != 0) return;
            }
        }
        throw new InvalidOperationException("Pencere yalnızca siyah/boş görüntü döndürdü. Bu uygulama doğrudan yakalamayı desteklemiyor olabilir; alan seçimini kullanabilirsin.");
    }

    private static Exception NativeFailure(string message) => new InvalidOperationException(message + ".", new Win32Exception(Marshal.GetLastWin32Error()));
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect rectangle, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PatBlt(IntPtr dc, int x, int y, int width, int height, uint operation);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
}
