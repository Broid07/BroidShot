using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfClipboard = System.Windows.Clipboard;

namespace BroidShot.Modules.Clipboard.Native;

/// <summary>
/// Listens for clipboard changes with AddClipboardFormatListener on a hidden message-only window and reads
/// the new content (files, text + HTML, image). Changes made by this module (<see cref="SetClipboard"/>) are ignored.
/// Must be used on the UI (STA) thread.
/// </summary>
internal sealed class ClipboardListener : IDisposable
{
    private static readonly TimeSpan OwnChangeWindow = TimeSpan.FromMilliseconds(1500);
    private readonly Action<string, Exception?> _log;
    private readonly DispatcherTimer _debounce;
    private HwndSource? _source;
    private uint _ownSequence;
    private DateTime _ownSetUtc = DateTime.MinValue;
    private bool _reading;

    public ClipboardListener(Dispatcher dispatcher, Action<string, Exception?> log)
    {
        _log = log;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await ReadAndRaiseAsync(); };
    }

    /// <summary>Raised on the UI thread with what was copied.</summary>
    public event Action<ClipCapture>? Captured;

    public bool IsRunning => _source is not null;

    public void Start()
    {
        if (_source is not null) return;
        var parameters = new HwndSourceParameters("BroidShotClipboardListener")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        if (!NativeMethods.AddClipboardFormatListener(_source.Handle))
        {
            int error = Marshal.GetLastWin32Error();
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
            throw new InvalidOperationException($"Pano dinleyicisi başlatılamadı (Win32 hata {error}).");
        }
    }

    public void Stop()
    {
        _debounce.Stop();
        if (_source is null) return;
        NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }

    /// <summary>Puts data on the clipboard and remembers the change so the listener skips it.</summary>
    public void SetClipboard(IDataObject data)
    {
        _ownSetUtc = DateTime.UtcNow;
        WpfClipboard.SetDataObject(data, true);
        _ownSequence = NativeMethods.GetClipboardSequenceNumber();
        _ownSetUtc = DateTime.UtcNow;
    }

    private bool IsOwnChange()
    {
        if (NativeMethods.GetClipboardSequenceNumber() == _ownSequence) return true;
        bool ownerIsUs = NativeMethods.WindowProcessId(NativeMethods.GetClipboardOwner()) == (uint)Environment.ProcessId;
        return ownerIsUs && DateTime.UtcNow - _ownSetUtc < OwnChangeWindow;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            handled = true;
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    private async Task ReadAndRaiseAsync()
    {
        if (_reading || _source is null) return;
        _reading = true;
        try
        {
            if (IsOwnChange()) return;
            ClipCapture? capture = await ReadAsync();
            if (capture is not null && !capture.IsEmpty) Captured?.Invoke(capture);
        }
        catch (Exception ex)
        {
            _log("Pano içeriği okunamadı.", ex); // never include the content itself
        }
        finally { _reading = false; }
    }

    private async Task<ClipCapture?> ReadAsync()
    {
        bool sensitiveFormat = SourceMarkedSensitive();
        string? sourceApp = SourceProcessName();

        IDataObject? data = null;
        for (int attempt = 0; attempt < 5 && data is null; attempt++)
        {
            try { data = WpfClipboard.GetDataObject(); }
            catch (COMException) { await Task.Delay(40); }
            catch (ExternalException) { await Task.Delay(40); }
        }
        if (data is null) return null;

        if (TryGet(data, DataFormats.FileDrop) is string[] { Length: > 0 } files)
            return new ClipCapture { Files = files, SourceApp = sourceApp, SensitiveFormat = sensitiveFormat };

        string? text = TryGet(data, DataFormats.UnicodeText) as string ?? TryGet(data, DataFormats.Text) as string;
        string? html = TryGet(data, DataFormats.Html) as string;
        if (!string.IsNullOrEmpty(text) || !string.IsNullOrWhiteSpace(ClipboardHistory.HtmlToText(html)))
            return new ClipCapture { Text = text, Html = html, SourceApp = sourceApp, SensitiveFormat = sensitiveFormat };

        if (TryGet(data, "PNG") is MemoryStream pngStream && pngStream.Length > 8)
        {
            byte[] png = pngStream.ToArray();
            (int w, int h) = ClipboardHistory.PngSize(png);
            if (w > 0) return new ClipCapture { ImagePng = png, ImageWidth = w, ImageHeight = h, SourceApp = sourceApp, SensitiveFormat = sensitiveFormat };
        }
        if (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib))
        {
            BitmapSource? image = null;
            try { image = WpfClipboard.GetImage(); }
            catch (Exception ex) when (ex is COMException or ExternalException or NotSupportedException or ArgumentException) { }
            if (image is { PixelWidth: > 0, PixelHeight: > 0 })
            {
                var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                int stride = converted.PixelWidth * 4;
                long size = (long)stride * converted.PixelHeight;
                if (size > 512L * 1024 * 1024) return null;
                byte[] pixels = new byte[size];
                converted.CopyPixels(pixels, stride, 0);
                // DIBs without alpha often arrive as all-zero alpha; make them opaque.
                if (image.Format != PixelFormats.Bgra32 && image.Format != PixelFormats.Pbgra32)
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return new ClipCapture { ImagePixels = pixels, ImageWidth = converted.PixelWidth, ImageHeight = converted.PixelHeight, SourceApp = sourceApp, SensitiveFormat = sensitiveFormat };
            }
        }
        return null;
    }

    private static object? TryGet(IDataObject data, string format)
    {
        try { return data.GetDataPresent(format) ? data.GetData(format) : null; }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException or ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>ExcludeClipboardContentFromMonitorProcessing, Clipboard Viewer Ignore, or CanIncludeInClipboardHistory = 0.</summary>
    private bool SourceMarkedSensitive()
    {
        if (Available(SensitiveDetector.ExcludeFormat) || Available(SensitiveDetector.ViewerIgnoreFormat)) return true;
        uint history = NativeMethods.RegisterClipboardFormat(SensitiveDetector.HistoryFormat);
        if (history == 0 || !NativeMethods.IsClipboardFormatAvailable(history)) return false;
        // The value is a DWORD; 0 means "do not keep in history". If it cannot be read, err on the safe side.
        return ReadDword(history) is not { } value || value == 0;
    }

    private static bool Available(string format)
    {
        uint id = NativeMethods.RegisterClipboardFormat(format);
        return id != 0 && NativeMethods.IsClipboardFormatAvailable(id);
    }

    private int? ReadDword(uint format)
    {
        IntPtr owner = _source?.Handle ?? IntPtr.Zero;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (!NativeMethods.OpenClipboard(owner)) { System.Threading.Thread.Sleep(15); continue; }
            try
            {
                IntPtr handle = NativeMethods.GetClipboardData(format);
                if (handle == IntPtr.Zero) return null;
                if (NativeMethods.GlobalSize(handle).ToUInt64() < 4) return null;
                IntPtr pointer = NativeMethods.GlobalLock(handle);
                if (pointer == IntPtr.Zero) return null;
                try { return Marshal.ReadInt32(pointer); }
                finally { NativeMethods.GlobalUnlock(handle); }
            }
            finally { NativeMethods.CloseClipboard(); }
        }
        return null;
    }

    /// <summary>Process name of the clipboard owner window, or of the foreground window when the owner is unknown.</summary>
    internal static string? SourceProcessName()
    {
        uint pid = NativeMethods.WindowProcessId(NativeMethods.GetClipboardOwner());
        if (pid == 0) pid = NativeMethods.WindowProcessId(NativeMethods.GetForegroundWindow());
        return ProcessName(pid);
    }

    internal static string? ProcessName(uint pid)
    {
        if (pid == 0) return null;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    public void Dispose() => Stop();
}
