using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace BroidShot.Modules.Convert;

/// <summary>PDF creation and page editing with PDFsharp (MIT). Every method returns the finished file as bytes.</summary>
public static class PdfEditing
{
    private static readonly string[] JpegExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif" };

    /// <summary>
    /// Opens a PDF for importing its pages. Asks <paramref name="prompt"/> for a password when the file is protected;
    /// the working password is remembered on <paramref name="item"/>.
    /// </summary>
    public static PdfSharp.Pdf.PdfDocument OpenForImport(ConvertItem item, PdfPasswordPrompt? prompt)
    {
        int attempt = 0;
        string? supplied = null;
        bool skipped = false, exhausted = false;
        var provider = new PdfSharp.Pdf.IO.PdfPasswordProvider(args =>
        {
            if (prompt is null) { args.Abort = true; return; }
            // A prompt that keeps answering with a wrong password must not loop forever.
            if (attempt >= PdfRendering.MaxPasswordAttempts) { exhausted = true; args.Abort = true; return; }
            string? password = prompt(item.Name, attempt++);
            if (password is null) { skipped = true; args.Abort = true; return; }
            supplied = password;
            args.Password = password;
        });

        PdfSharp.Pdf.PdfDocument? document;
        try
        {
            document = string.IsNullOrEmpty(item.Password)
                ? PdfReader.Open(item.Path, PdfDocumentOpenMode.Import, provider)
                : PdfReader.Open(item.Path, item.Password, PdfDocumentOpenMode.Import, provider);
        }
        catch (FileNotFoundException ex) { throw new ConvertException("Dosya bulunamadı.", ex); }
        catch (DirectoryNotFoundException ex) { throw new ConvertException("Dosya bulunamadı.", ex); }
        catch (UnauthorizedAccessException ex) { throw new ConvertException("Dosyaya erişim izni yok.", ex); }
        catch (IOException ex) { throw new ConvertException("Dosya okunamadı: " + ex.Message, ex); }
        catch (Exception ex) when (ex is PdfSharp.PdfSharpException or InvalidOperationException or NotImplementedException or NotSupportedException or ArgumentException or FormatException or IndexOutOfRangeException)
        {
            throw new ConvertException("PDF okunamadı. Dosya bozuk, desteklenmeyen bir şifrelemeyle korunuyor ya da PDF değil: " + FirstLine(ex.Message), ex);
        }

        if (document is null)
            throw new ConvertException(exhausted ? "Parola yanlış; çok fazla deneme yapıldı, dosya atlandı."
                : skipped || prompt is null ? "Bu PDF parola korumalı, parola girilmedi." : "PDF açılamadı.");
        if (supplied is not null) item.Password = supplied;
        return document;
    }

    /// <summary>
    /// Light probe for the file list: page count without asking for a password.
    /// Returns (0, true) for password-protected files and (0, false) for unreadable ones.
    /// </summary>
    public static (int Pages, bool NeedsPassword) Probe(string path, string? password = null)
    {
        bool needsPassword = false;
        var provider = new PdfSharp.Pdf.IO.PdfPasswordProvider(args => { needsPassword = true; args.Abort = true; });
        try
        {
            using PdfSharp.Pdf.PdfDocument? document = string.IsNullOrEmpty(password)
                ? PdfReader.Open(path, PdfDocumentOpenMode.Import, provider)
                : PdfReader.Open(path, password, PdfDocumentOpenMode.Import, provider);
            return document is null ? (0, needsPassword) : (document.PageCount, false);
        }
        catch (Exception ex) when (ex is PdfSharp.PdfSharpException or IOException or UnauthorizedAccessException or InvalidOperationException
            or NotImplementedException or NotSupportedException or ArgumentException or FormatException or IndexOutOfRangeException)
        {
            return (0, needsPassword);
        }
    }

    /// <summary>Page count of a PDF without keeping it open. Throws <see cref="ConvertException"/> for unreadable or protected files.</summary>
    public static int GetPageCount(ConvertItem item, PdfPasswordPrompt? prompt)
    {
        using PdfSharp.Pdf.PdfDocument document = OpenForImport(item, prompt);
        item.PageCount = document.PageCount;
        return document.PageCount;
    }

    /// <summary>One PDF made of the chosen pages (1-based, in the given order), optionally rotated clockwise.</summary>
    public static byte[] ExtractPages(PdfSharp.Pdf.PdfDocument source, IReadOnlyList<int> pages, int rotateDegrees = 0)
    {
        if (pages.Count == 0) throw new ConvertException("Seçilen sayfa yok.");
        using var output = new PdfSharp.Pdf.PdfDocument();
        foreach (int number in pages)
        {
            PdfPage added = output.AddPage(source.Pages[number - 1]);
            if (rotateDegrees != 0) added.Rotate = (((added.Rotate + rotateDegrees) % 360) + 360) % 360;
        }
        return Save(output);
    }

    /// <summary>All pages except those listed. Throws when nothing would remain.</summary>
    public static byte[] DeletePages(PdfSharp.Pdf.PdfDocument source, IReadOnlyList<int> pagesToDelete)
    {
        var drop = new HashSet<int>(pagesToDelete);
        var keep = new List<int>();
        for (int page = 1; page <= source.PageCount; page++) if (!drop.Contains(page)) keep.Add(page);
        if (keep.Count == 0) throw new ConvertException("Bütün sayfalar silinemez; PDF'te en az bir sayfa kalmalı.");
        return ExtractPages(source, keep);
    }

    /// <summary>Rotates the chosen pages (others untouched) and keeps every page.</summary>
    public static byte[] RotatePages(PdfSharp.Pdf.PdfDocument source, IReadOnlyList<int> pages, int degrees)
    {
        if (degrees % 90 != 0) throw new ConvertException("Döndürme açısı 90'ın katı olmalı.");
        var rotate = new HashSet<int>(pages);
        using var output = new PdfSharp.Pdf.PdfDocument();
        for (int page = 1; page <= source.PageCount; page++)
        {
            PdfPage added = output.AddPage(source.Pages[page - 1]);
            if (rotate.Contains(page)) added.Rotate = (((added.Rotate + degrees) % 360) + 360) % 360;
        }
        return Save(output);
    }

    /// <summary>Appends all pages of the documents in order.</summary>
    public static byte[] Merge(IEnumerable<PdfSharp.Pdf.PdfDocument> sources)
    {
        using var output = new PdfSharp.Pdf.PdfDocument();
        int total = 0;
        foreach (PdfSharp.Pdf.PdfDocument source in sources)
            for (int i = 0; i < source.PageCount; i++) { output.AddPage(source.Pages[i]); total++; }
        if (total == 0) throw new ConvertException("Birleştirilecek sayfa yok.");
        return Save(output);
    }

    /// <summary>
    /// Builds one PDF from images, one page per image in list order. Images that cannot be read are reported through
    /// <paramref name="onImageFailed"/> and skipped. Returns the PDF and its page count.
    /// </summary>
    public static (byte[] Data, int PageCount) ImagesToPdf(IReadOnlyList<ConvertItem> images, PageFit fit, Action<int, int>? onProgress,
        Action<ConvertItem, ConvertException> onImageFailed, CancellationToken cancellationToken)
    {
        var disposables = new List<IDisposable>();
        try
        {
            using var document = new PdfSharp.Pdf.PdfDocument();
            document.Info.Creator = "BroidShot";
            int index = 0;
            foreach (ConvertItem item in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { AddImagePage(document, item.Path, fit, disposables); }
                catch (ConvertException ex) { onImageFailed(item, ex); }
                onProgress?.Invoke(++index, images.Count);
            }
            if (document.PageCount == 0) throw new ConvertException("PDF'e eklenebilen görsel yok.");
            int pageCount = document.PageCount;
            return (Save(document), pageCount);
        }
        finally
        {
            foreach (IDisposable disposable in disposables) disposable.Dispose();
        }
    }

    private static void AddImagePage(PdfSharp.Pdf.PdfDocument document, string path, PageFit fit, List<IDisposable> disposables)
    {
        XImage? image = null;
        double dpiX = 96, dpiY = 96;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(JpegExtensions, extension) >= 0)
        {
            // Plain JPEGs are embedded as they are (no re-compression). Rotated (EXIF) ones go through the decoder.
            var probe = ImageProcessing.Probe(path);
            if (probe is { Orientation: 1 } info)
            {
                try
                {
                    image = XImage.FromFile(path);
                    dpiX = info.DpiX; dpiY = info.DpiY;
                }
                catch (Exception ex) when (ex is PdfSharp.PdfSharpException or NotSupportedException or InvalidOperationException or ArgumentException or IOException)
                {
                    image = null; // fall back to the decoder below
                }
            }
        }
        if (image is null)
        {
            RasterImage raster = ImageProcessing.Load(path);
            ImageProcessing.ApplyOrientation(raster);
            dpiX = raster.DpiX; dpiY = raster.DpiY;
            try { image = XImage.FromBitmapSource(raster.ToBitmapSource(allowAlpha: true)); }
            catch (Exception ex) when (ex is PdfSharp.PdfSharpException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                throw new ConvertException("Görsel PDF'e eklenemedi: " + FirstLine(ex.Message), ex);
            }
        }
        disposables.Add(image);

        int pixelWidth = image.PixelWidth, pixelHeight = image.PixelHeight;
        PdfPage page = document.AddPage();
        double x = 0, y = 0, w, h;
        if (fit == PageFit.A4)
        {
            var layout = ImageMath.FitToA4(pixelWidth, pixelHeight);
            page.Width = XUnit.FromPoint(layout.PageWidth);
            page.Height = XUnit.FromPoint(layout.PageHeight);
            x = layout.X; y = layout.Y; w = layout.Width; h = layout.Height;
        }
        else
        {
            (w, h) = ImageMath.OriginalPageSize(pixelWidth, pixelHeight, dpiX, dpiY);
            page.Width = XUnit.FromPoint(w);
            page.Height = XUnit.FromPoint(h);
        }
        using XGraphics graphics = XGraphics.FromPdfPage(page);
        graphics.DrawImage(image, x, y, w, h);
    }

    private static byte[] Save(PdfSharp.Pdf.PdfDocument document)
    {
        using var memory = new MemoryStream();
        document.Save(memory, false);
        return memory.ToArray();
    }

    private static string FirstLine(string message)
    {
        int newline = message.IndexOfAny(new[] { '\r', '\n' });
        return newline < 0 ? message : message[..newline];
    }
}
