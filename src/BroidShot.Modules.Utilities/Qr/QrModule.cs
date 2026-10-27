using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using BroidShot.Core;

namespace BroidShot.Modules.Utilities;

/// <summary>Saved to &lt;DataRoot&gt;/modules/qr/settings.json.</summary>
public sealed class QrSettings
{
    public int Size { get; set; } = 512;
    public QrErrorLevel ErrorLevel { get; set; } = QrErrorLevel.Medium;
}

/// <summary>QR: create from text/URL, read from a file, the clipboard image or a screen region (ZXing.Net).</summary>
public sealed class QrModule : UtilityModuleBase
{
    private readonly IClipboardService _clipboard;
    private QrSettings _settings = new();
    private bool _overlayOpen;

    public QrModule() : this(SystemClipboard.Instance) { }

    internal QrModule(IClipboardService clipboard) { _clipboard = clipboard; }

    public override string Id => "qr";
    public override string Title => "QR";
    public override string Description => "Metin veya bağlantıdan QR oluştur; görsel, pano veya ekrandan QR oku.";
    public override string Icon => "";
    public override IReadOnlyList<string> Keywords { get; } = new[] { "qr", "karekod", "barkod", "qrcode", "kare kod", "tara", "oku", "scan" };

    public override IReadOnlyList<ToolCommand> Commands => new[]
    {
        new ToolCommand("QR oluştur", UseClipboardTextForNewCode)
        {
            Id = "qr.create", NavigateToModuleId = Id,
            Description = "Panodaki metinden QR kodu üretir",
            Keywords = new[] { "qr", "karekod", "oluştur", "üret", "generate" }
        },
        new ToolCommand("Ekrandaki QR'ı oku", () => _ = ReadFromScreenAsync(copy: true))
        {
            Id = "qr.read-screen", Description = "Ekranda bir alan seç; içindeki QR kodunu okuyup kopyalar",
            Keywords = new[] { "qr", "karekod", "oku", "tara", "ekran", "scan" }
        },
        ToolCommand.Open("QR sayfasını aç", Id, "QR oluştur ve oku", new[] { "qr", "karekod" }, "qr.open"),
    };

    internal QrSettings Settings => _settings;

    /// <summary>Text for the generator waiting to be picked up by the page (set by palette commands before the page exists).</summary>
    internal string? PendingInput { get; set; }

    /// <summary>Raised on the UI thread with the decoded text and a Turkish source label.</summary>
    internal event Action<string, string>? Decoded;
    internal event Action<string>? InputRequested;

    public override void Initialize(IToolHost host)
    {
        base.Initialize(host);
        _settings = host.Settings.Load<QrSettings>();
        _settings.Size = Math.Clamp(_settings.Size, QrService.MinSize, QrService.MaxSize);
    }

    public override FrameworkElement CreateView() => new QrView(this);

    internal void SaveSettings()
    {
        try { Host.Settings.Save(_settings); }
        catch (Exception ex) { LogError("QR ayarları kaydedilemedi", ex); }
    }

    private void UseClipboardTextForNewCode()
    {
        string? text = null;
        try { text = _clipboard.GetText(); } catch { }
        if (string.IsNullOrWhiteSpace(text)) return;
        PendingInput = text.Length > QrService.MaxTextLength ? text[..QrService.MaxTextLength] : text;
        InputRequested?.Invoke(PendingInput);
    }

    internal BitmapSource? GetClipboardImage()
    {
        try { return _clipboard.GetImage(); } catch { return null; }
    }

    internal void CopyText(string text)
    {
        try { _clipboard.SetText(text); Toast("Kopyalandı", Shorten(text), ToastKind.Success); }
        catch (Exception ex) { LogError("Metin kopyalanamadı", ex); Toast("Kopyalanamadı", "Pano şu an kullanılamıyor.", ToastKind.Error); }
    }

    internal void CopyImage(BitmapSource image)
    {
        try { _clipboard.SetImage(image); Toast("QR görseli kopyalandı", "Panoya görsel olarak konuldu.", ToastKind.Success); }
        catch (Exception ex) { LogError("Görsel kopyalanamadı", ex); Toast("Kopyalanamadı", "Pano şu an kullanılamıyor.", ToastKind.Error); }
    }

    internal static string Shorten(string text) => text.Length <= 120 ? text : text[..120] + "…";

    /// <summary>Opens http(s) links in the default browser; anything else is refused.</summary>
    internal static bool TryOpenLink(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")) return false;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return true;
    }

    /// <summary>Decodes on a worker thread. Raises <see cref="Decoded"/> or shows a toast when nothing was found.</summary>
    internal async Task<string?> DecodeAsync(RawImage image, string source, bool copy, bool toastOnMiss = true)
    {
        string? text = await Task.Run(() =>
        {
            try { return QrService.Decode(image); }
            catch (Exception ex) { LogError("QR okunamadı", ex); return null; }
        });
        if (text is null)
        {
            if (toastOnMiss) Toast("QR bulunamadı", source + " içinde okunabilir bir QR kodu yok.", ToastKind.Warning);
            return null;
        }
        Decoded?.Invoke(text, source);
        if (copy)
        {
            try { _clipboard.SetText(text); Toast("QR okundu", "Panoya kopyalandı: " + Shorten(text), ToastKind.Success); }
            catch (Exception ex) { LogError("QR içeriği kopyalanamadı", ex); Toast("QR okundu", Shorten(text), ToastKind.Success); }
        }
        return text;
    }

    internal async Task<string?> ReadFromFileAsync(string path, bool copy = false)
    {
        try
        {
            BitmapSource bitmap = QrService.LoadImage(path);
            return await DecodeAsync(QrService.ToRaw(bitmap), "Görsel dosyası", copy);
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Toast("Görsel açılamadı", "Dosya bir görsel olarak okunamadı.", ToastKind.Error);
            return null;
        }
    }

    internal async Task<string?> ReadFromClipboardAsync(bool copy = false)
    {
        BitmapSource? image = GetClipboardImage();
        if (image is null)
        {
            Toast("Panoda görsel yok", "Önce bir görsel veya ekran alıntısı kopyala.", ToastKind.Warning);
            return null;
        }
        return await DecodeAsync(QrService.ToRaw(image), "Pano görseli", copy);
    }

    internal async Task<string?> ReadFromScreenAsync(bool copy)
    {
        if (_overlayOpen || !IsInitialized) return null;
        _overlayOpen = true;
        try
        {
            RawImage? crop = null;
            using (await MainWindowHider.HideAsync())
            {
                ScreenSnapshot snapshot = ScreenSnapshot.Capture();
                var overlay = new RegionSelectOverlay(snapshot, "QR kodunu içine alacak şekilde sürükle");
                overlay.Show();
                PixelRect? region = await overlay.Result;
                if (region is PixelRect area && snapshot.Crop(area, out int w, out int h) is { } bytes)
                    crop = new RawImage(w, h, bytes);
            }
            return crop is null ? null : await DecodeAsync(crop, "Ekran alanı", copy);
        }
        catch (Exception ex)
        {
            LogError("Ekrandan QR okunamadı", ex);
            Toast("Ekran okunamadı", ex.Message, ToastKind.Error);
            return null;
        }
        finally { _overlayOpen = false; }
    }
}
