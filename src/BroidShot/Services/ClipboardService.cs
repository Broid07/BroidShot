using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BroidShot.Services;

public static class ClipboardService
{
    public static async Task CopyImageAsync(BitmapSource image)
    {
        BitmapSource frozen = ImageExporter.FreezeForBackground(image);
        byte[] png = await Task.Run(() => ImageExporter.EncodePng(frozen));
        Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        Exception? lastError = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    var data = new DataObject();
                    data.SetImage(frozen);
                    // Several browsers and messaging apps prefer the registered PNG clipboard format.
                    using var stream = new MemoryStream(png, writable: false);
                    data.SetData("PNG", stream);
                    Clipboard.SetDataObject(data, copy: true);
                });
                return;
            }
            catch (ExternalException ex) { lastError = ex; }
            if (attempt < 5) await Task.Delay(60 * (attempt + 1));
        }
        throw new InvalidOperationException("Pano başka bir uygulama tarafından kullanılıyor. Tekrar kopyalamayı deneyin.", lastError);
    }
}
