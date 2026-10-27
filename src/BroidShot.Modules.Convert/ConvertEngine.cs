using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BroidShot.Modules.Convert;

/// <summary>
/// Runs one conversion batch. No UI types: progress comes back through a callback, passwords through a delegate,
/// so the same code runs in the app and in the verification program. Errors are collected per file; one bad file
/// never stops the others. Output files are written atomically and never overwrite existing files.
/// </summary>
public sealed class ConvertEngine
{
    private readonly PdfPasswordPrompt? _passwords;

    public ConvertEngine(PdfPasswordPrompt? passwords = null)
    {
        _passwords = passwords;
    }

    /// <summary>Reports (fraction 0..1 or null, status text). May be called from any thread.</summary>
    public Action<double?, string>? Report { get; set; }

    /// <summary>Per-file progress (item, fraction 0..1) while that file is being processed. May be called from any thread.</summary>
    public Action<ConvertItem, double>? ItemProgress { get; set; }

    /// <summary>A file's own part is finished (merge / images → PDF still add the combined output later). May be called from any thread.</summary>
    public Action<ConvertItem, FileOutcome>? ItemFinished { get; set; }

    public async Task<BatchOutcome> RunAsync(ConvertOperation operation, IReadOnlyList<ConvertItem> items, ConvertOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        var batch = new BatchOutcome();
        try
        {
            switch (operation)
            {
                case ConvertOperation.ImagesToPdf:
                    await ImagesToPdfAsync(items, options, batch, cancellationToken).ConfigureAwait(false);
                    break;
                case ConvertOperation.MergePdf:
                    await MergeAsync(items, options, batch, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    for (int index = 0; index < items.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ConvertItem item = items[index];
                        var outcome = new FileOutcome(item.Path);
                        batch.Files.Add(outcome);
                        double baseFraction = index / (double)items.Count, share = 1.0 / items.Count;
                        void Progress(double inner, string status)
                        {
                            Report?.Invoke(baseFraction + share * Math.Clamp(inner, 0, 1), $"{item.Name}: {status}");
                            ItemProgress?.Invoke(item, Math.Clamp(inner, 0, 1));
                        }
                        Report?.Invoke(baseFraction, $"{item.Name} ({index + 1}/{items.Count})");
                        ItemProgress?.Invoke(item, 0);
                        try
                        {
                            await RunSingleAsync(operation, item, options, outcome, Progress, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            outcome.Error = Describe(ex);
                        }
                        ItemFinished?.Invoke(item, outcome);
                    }
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            batch.WasCancelled = true;
        }
        catch (ConvertException ex)
        {
            // Problems that concern the whole batch (e.g. too few PDFs to merge).
            batch.Files.Add(new FileOutcome(items.Count > 0 ? items[0].Path : "") { Error = ex.Message, Label = items.Count > 1 ? $"{items.Count} dosya" : null });
        }
        return batch;
    }

    private async Task RunSingleAsync(ConvertOperation operation, ConvertItem item, ConvertOptions options, FileOutcome outcome, Action<double, string> progress, CancellationToken token)
    {
        switch (operation)
        {
            case ConvertOperation.PdfToImages: await PdfToImagesAsync(item, options, outcome, progress, token).ConfigureAwait(false); break;
            case ConvertOperation.SplitPdf: await SplitAsync(item, options, outcome, progress, token).ConfigureAwait(false); break;
            case ConvertOperation.ExtractPages:
            case ConvertOperation.DeletePages:
            case ConvertOperation.RotatePages:
                await EditPagesAsync(operation, item, options, outcome, token).ConfigureAwait(false); break;
            case ConvertOperation.ConvertImages: await ConvertImageAsync(item, options, outcome, token).ConfigureAwait(false); break;
            default: throw new ConvertException("Bu işlem tek dosya için çalıştırılamaz.");
        }
    }

    // ---------- PDF -> images ----------

    private async Task PdfToImagesAsync(ConvertItem item, ConvertOptions options, FileOutcome outcome, Action<double, string> progress, CancellationToken token)
    {
        var (document, password) = await PdfRendering.OpenAsync(item.Path, item.Password, _passwords, token).ConfigureAwait(false);
        item.Password = password;
        int pageCount = (int)document.PageCount;
        item.PageCount = pageCount;
        PageSelection selection = PageRangeParser.Parse(options.PageRange, pageCount);
        if (!selection.Success) throw new ConvertException(selection.Error!);

        string directory = ResolveOutputDirectory(options, item.Path);
        string baseName = Path.GetFileNameWithoutExtension(item.Path);
        string extension = options.RasterFormat == RasterFormat.Jpg ? ".jpg" : ".png";
        int done = 0;
        foreach (int page in selection.Pages)
        {
            token.ThrowIfCancellationRequested();
            progress(done / (double)selection.Pages.Count, $"sayfa {page}/{pageCount}");
            var (png, _, _) = await PdfRendering.RenderPageAsync(document, page - 1, options.Dpi, token).ConfigureAwait(false);
            byte[] data = options.RasterFormat == RasterFormat.Jpg ? ImageProcessing.PngToJpeg(png, options.JpgQuality) : png;
            string name = FileNaming.ExpandTemplate(options.NameTemplate, baseName, page.ToString(System.Globalization.CultureInfo.InvariantCulture), pageCount) + extension;
            outcome.Outputs.Add(await FileNaming.WriteAtomicAsync(Path.Combine(directory, name), data, token).ConfigureAwait(false));
            done++;
        }
        progress(1, "tamamlandı");
    }

    // ---------- single-PDF page operations ----------

    private async Task SplitAsync(ConvertItem item, ConvertOptions options, FileOutcome outcome, Action<double, string> progress, CancellationToken token)
    {
        using PdfSharp.Pdf.PdfDocument source = PdfEditing.OpenForImport(item, _passwords);
        int pageCount = source.PageCount;
        item.PageCount = pageCount;
        string directory = ResolveOutputDirectory(options, item.Path);
        string baseName = Path.GetFileNameWithoutExtension(item.Path);

        var parts = new List<(string Label, IReadOnlyList<int> Pages)>();
        if (options.SplitMode == SplitMode.EachPage)
        {
            for (int page = 1; page <= pageCount; page++) parts.Add((page.ToString(System.Globalization.CultureInfo.InvariantCulture), new[] { page }));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.SplitRanges)) throw new ConvertException("Bölme aralıklarını yaz. Her virgülle ayrılan aralık ayrı bir dosya olur. Örnek: 1-3,4-6,7");
            PageSelection selection = PageRangeParser.Parse(options.SplitRanges, pageCount);
            if (!selection.Success) throw new ConvertException(selection.Error!);
            foreach (IReadOnlyList<int> group in selection.Groups) parts.Add((PageRangeParser.Format(group), group));
        }

        for (int i = 0; i < parts.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            progress(i / (double)parts.Count, $"parça {i + 1}/{parts.Count}");
            byte[] data = PdfEditing.ExtractPages(source, parts[i].Pages);
            string name = FileNaming.ExpandTemplate(options.NameTemplate, baseName, parts[i].Label, pageCount) + ".pdf";
            outcome.Outputs.Add(await FileNaming.WriteAtomicAsync(Path.Combine(directory, name), data, token).ConfigureAwait(false));
        }
        progress(1, "tamamlandı");
    }

    private async Task EditPagesAsync(ConvertOperation operation, ConvertItem item, ConvertOptions options, FileOutcome outcome, CancellationToken token)
    {
        using PdfSharp.Pdf.PdfDocument source = PdfEditing.OpenForImport(item, _passwords);
        int pageCount = source.PageCount;
        item.PageCount = pageCount;
        if (operation != ConvertOperation.RotatePages && string.IsNullOrWhiteSpace(options.PageRange))
            throw new ConvertException(operation == ConvertOperation.ExtractPages ? "Çıkarılacak sayfaları yaz. Örnek: 1-3,5" : "Silinecek sayfaları yaz. Örnek: 2,4-6");
        PageSelection selection = PageRangeParser.Parse(options.PageRange, pageCount);
        if (!selection.Success) throw new ConvertException(selection.Error!);

        byte[] data;
        string suffix;
        switch (operation)
        {
            case ConvertOperation.ExtractPages: data = PdfEditing.ExtractPages(source, selection.Pages); suffix = "_secili"; break;
            case ConvertOperation.DeletePages: data = PdfEditing.DeletePages(source, selection.Pages); suffix = "_duzenlendi"; break;
            default:
                if (options.RotateDegrees is not (90 or 180 or 270)) throw new ConvertException("Döndürme açısı 90, 180 veya 270 olmalı.");
                data = PdfEditing.RotatePages(source, selection.Pages, options.RotateDegrees); suffix = "_dondurulmus"; break;
        }
        string desired = Path.Combine(ResolveOutputDirectory(options, item.Path), FileNaming.Sanitize(Path.GetFileNameWithoutExtension(item.Path)) + suffix + ".pdf");
        outcome.Outputs.Add(await FileNaming.WriteAtomicAsync(desired, data, token).ConfigureAwait(false));
    }

    // ---------- several inputs -> one PDF ----------

    private async Task ImagesToPdfAsync(IReadOnlyList<ConvertItem> items, ConvertOptions options, BatchOutcome batch, CancellationToken token)
    {
        if (items.Count == 0) throw new ConvertException("Listede görsel yok.");
        var outcomes = new Dictionary<ConvertItem, FileOutcome>(ReferenceEqualityComparer.Instance);
        foreach (ConvertItem item in items)
        {
            var outcome = new FileOutcome(item.Path);
            outcomes[item] = outcome;
            batch.Files.Add(outcome);
        }

        var result = new FileOutcome(items[0].Path) { Label = items.Count == 1 ? "PDF" : $"{items.Count} görselden PDF" };
        batch.Files.Add(result);
        try
        {
            Report?.Invoke(0, "PDF hazırlanıyor");
            ItemProgress?.Invoke(items[0], 0);
            var (data, pageCount) = await Task.Run(() => PdfEditing.ImagesToPdf(items, options.PageFit,
                (done, total) =>
                {
                    Report?.Invoke(done / (double)total, $"görsel {done}/{total}");
                    ConvertItem finished = items[done - 1];
                    ItemFinished?.Invoke(finished, outcomes[finished]);
                    if (done < total) ItemProgress?.Invoke(items[done], 0);
                },
                (item, error) => outcomes[item].Error = error.Message, token), token).ConfigureAwait(false);
            string name = OutputNameOrDefault(options, items, items.Count == 1 ? "" : "_birlesik");
            result.Outputs.Add(await FileNaming.WriteAtomicAsync(Path.Combine(ResolveOutputDirectory(options, items[0].Path), name + ".pdf"), data, token).ConfigureAwait(false));
            if (pageCount < items.Count) result.Warnings.Add($"{items.Count - pageCount} görsel PDF'e eklenemedi (ayrıntı dosya satırlarında).");
            Report?.Invoke(1, "tamamlandı");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { result.Error = Describe(ex); }
    }

    private async Task MergeAsync(IReadOnlyList<ConvertItem> items, ConvertOptions options, BatchOutcome batch, CancellationToken token)
    {
        if (items.Count < 2) throw new ConvertException("Birleştirmek için en az iki PDF gerekir.");
        var result = new FileOutcome(items[0].Path) { Label = $"{items.Count} PDF birleştirildi" };
        var opened = new List<PdfSharp.Pdf.PdfDocument>();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                ConvertItem item = items[i];
                var outcome = new FileOutcome(item.Path);
                batch.Files.Add(outcome);
                Report?.Invoke(i / (double)items.Count, $"{item.Name} okunuyor ({i + 1}/{items.Count})");
                ItemProgress?.Invoke(item, 0.5);
                try { opened.Add(PdfEditing.OpenForImport(item, _passwords)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { outcome.Error = Describe(ex); }
                ItemFinished?.Invoke(item, outcome);
            }
            batch.Files.Add(result);
            if (opened.Count == 0) { result.Error = "Birleştirilecek okunabilir PDF yok."; return; }
            try
            {
                Report?.Invoke(0.9, "Birleştiriliyor");
                byte[] data = await Task.Run(() => PdfEditing.Merge(opened), token).ConfigureAwait(false);
                string name = OutputNameOrDefault(options, items, "_birlesik");
                result.Outputs.Add(await FileNaming.WriteAtomicAsync(Path.Combine(ResolveOutputDirectory(options, items[0].Path), name + ".pdf"), data, token).ConfigureAwait(false));
                if (opened.Count < items.Count) result.Warnings.Add($"{items.Count - opened.Count} dosya okunamadığı için birleştirmeye katılmadı.");
                Report?.Invoke(1, "tamamlandı");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Error = Describe(ex); }
        }
        finally
        {
            foreach (PdfSharp.Pdf.PdfDocument document in opened) document.Dispose();
        }
    }

    // ---------- image -> image ----------

    private Task ConvertImageAsync(ConvertItem item, ConvertOptions options, FileOutcome outcome, CancellationToken token) => Task.Run(async () =>
    {
        RasterImage image = ImageProcessing.Load(item.Path);
        token.ThrowIfCancellationRequested();
        if (image.FrameCount > 1) outcome.Warnings.Add("Çok kareli dosya: yalnız ilk kare dönüştürüldü.");
        ImageProcessing.ApplyOrientation(image);
        ImageTarget target = options.TargetFormat;
        if (target != ImageTarget.Ico && options.ResizeMode != ResizeKind.None)
        {
            if (options.ResizeMode == ResizeKind.Percent && !(options.ResizePercent > 0 && options.ResizePercent <= 1000))
                throw new ConvertException("Yüzde 0'dan büyük ve en çok 1000 olmalı.");
            var (width, height) = ImageMath.Resolve(image.Width, image.Height, options);
            ImageProcessing.Resize(image, width, height);
        }
        token.ThrowIfCancellationRequested();
        var (data, warnings) = ImageProcessing.Encode(image, target, options);
        outcome.Warnings.AddRange(warnings);
        string baseName = FileNaming.Sanitize(Path.GetFileNameWithoutExtension(item.Path));
        string desired = Path.Combine(ResolveOutputDirectory(options, item.Path), baseName + ExtensionFor(target));
        outcome.Outputs.Add(await FileNaming.WriteAtomicAsync(desired, data, token).ConfigureAwait(false));
    }, token);

    public static string ExtensionFor(ImageTarget target) => target switch
    {
        ImageTarget.Png => ".png",
        ImageTarget.Jpg => ".jpg",
        ImageTarget.Bmp => ".bmp",
        ImageTarget.Tiff => ".tif",
        ImageTarget.Gif => ".gif",
        _ => ".ico"
    };

    // ---------- helpers ----------

    /// <summary>The folder for outputs: the chosen folder, or next to the source file.</summary>
    public static string ResolveOutputDirectory(ConvertOptions options, string sourcePath)
    {
        if (options.Location == OutputLocation.CustomFolder && !string.IsNullOrWhiteSpace(options.OutputFolder))
        {
            string custom = Path.GetFullPath(options.OutputFolder);
            Directory.CreateDirectory(custom);
            return custom;
        }
        return Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? throw new ConvertException("Çıktı klasörü belirlenemedi.");
    }

    private static string OutputNameOrDefault(ConvertOptions options, IReadOnlyList<ConvertItem> items, string suffix)
    {
        string custom = (options.OutputName ?? "").Trim();
        if (custom.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) custom = custom[..^4];
        if (custom.Length > 0) return FileNaming.Sanitize(custom);
        return FileNaming.Sanitize(Path.GetFileNameWithoutExtension(items[0].Path)) + suffix;
    }

    /// <summary>Turkish, user-facing text for any exception.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        ConvertException known => known.Message,
        UnauthorizedAccessException => "Dosyaya ya da klasöre yazma/okuma izni yok.",
        PathTooLongException => "Dosya yolu çok uzun.",
        DirectoryNotFoundException => "Klasör bulunamadı.",
        IOException io => "Dosya hatası: " + io.Message,
        OutOfMemoryException => "Bellek yetmedi; daha küçük bir DPI/boyut dene.",
        _ => "Beklenmeyen hata: " + ex.Message
    };
}
