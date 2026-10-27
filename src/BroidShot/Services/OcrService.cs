using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Tesseract;

namespace BroidShot.Services;

public sealed class OcrService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _dataDirectory;

    /// <summary>True when at least one language model (Turkish or English) is installed.</summary>
    public bool IsAvailable => Languages.Length > 0;

    /// <summary>Tesseract language string: "tur+eng" when both models exist, otherwise whichever one is present ("" = none).</summary>
    public string Languages
    {
        get
        {
            bool tur = File.Exists(Path.Combine(_dataDirectory, "tur.traineddata"));
            bool eng = File.Exists(Path.Combine(_dataDirectory, "eng.traineddata"));
            return tur && eng ? "tur+eng" : tur ? "tur" : eng ? "eng" : "";
        }
    }

    /// <summary>Human readable language list for status text, e.g. "Türkçe + İngilizce".</summary>
    public string LanguageLabel => Languages switch
    {
        "tur+eng" => "Türkçe + İngilizce",
        "tur" => "Türkçe",
        "eng" => "İngilizce",
        _ => "yok"
    };

    public OcrService(string? dataDirectory = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
    }

    public async Task<string> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken = default)
    {
        string languages = Languages;
        if (languages.Length == 0) throw new InvalidOperationException("OCR dil dosyaları bulunamadı. Uygulamayı tessdata klasörüyle birlikte çalıştırın.");
        BitmapSource frozen = ImageExporter.FreezeForBackground(image);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Pix pix = Pix.LoadFromMemory(ImageExporter.EncodePng(frozen));
                using var engine = new TesseractEngine(_dataDirectory, languages, EngineMode.LstmOnly);
                using Page page = engine.Process(pix, PageSegMode.Auto);
                string text = page.GetText().Trim();
                // Native recognition cannot safely be interrupted; cancel discards its result.
                cancellationToken.ThrowIfCancellationRequested();
                return text;
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TesseractException)
        {
            throw new InvalidOperationException("OCR motoru başlatılamadı. x64 yerel kitaplıkların ve Visual C++ çalışma zamanının kurulu olduğunu kontrol edin.", ex);
        }
        finally { _gate.Release(); }
    }
}
