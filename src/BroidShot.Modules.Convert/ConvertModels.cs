using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace BroidShot.Modules.Convert;

public enum ConvertOperation
{
    PdfToImages,
    ImagesToPdf,
    MergePdf,
    SplitPdf,
    ExtractPages,
    DeletePages,
    RotatePages,
    ConvertImages
}

public enum FileKind { Pdf, Image }

/// <summary>Output format of PDF → image.</summary>
public enum RasterFormat { Png, Jpg }

/// <summary>Output format of image → image.</summary>
public enum ImageTarget { Png, Jpg, Bmp, Tiff, Gif, Ico }

public enum PageFit { A4, Original }

public enum SplitMode { EachPage, Ranges }

public enum ResizeKind { None, Percent, MaxSize }

public enum PngCompression { Fast, Balanced, Smallest }

public enum OutputLocation { NextToSource, CustomFolder }

/// <summary>Everything the user can configure. Persisted as part of <see cref="ConvertSettings"/>; every property has a default.</summary>
public sealed class ConvertOptions
{
    public OutputLocation Location { get; set; } = OutputLocation.NextToSource;
    public string OutputFolder { get; set; } = "";

    // PDF -> images
    public string PageRange { get; set; } = "";
    public int Dpi { get; set; } = 150;
    public RasterFormat RasterFormat { get; set; } = RasterFormat.Png;
    public int JpgQuality { get; set; } = 90;
    public string NameTemplate { get; set; } = FileNaming.DefaultPageTemplate;

    // Images -> PDF / merge: optional output name (without extension); empty = automatic.
    public PageFit PageFit { get; set; } = PageFit.A4;
    public string OutputName { get; set; } = "";

    // Split
    public SplitMode SplitMode { get; set; } = SplitMode.EachPage;
    public string SplitRanges { get; set; } = "";

    // Rotate: 90, 180 or 270 degrees clockwise.
    public int RotateDegrees { get; set; } = 90;

    // Image format conversion
    public ImageTarget TargetFormat { get; set; } = ImageTarget.Png;
    public ResizeKind ResizeMode { get; set; } = ResizeKind.None;
    public double ResizePercent { get; set; } = 50;
    public int MaxWidth { get; set; } = 1920;
    public int MaxHeight { get; set; } = 1080;
    public PngCompression PngCompression { get; set; } = PngCompression.Balanced;
    public bool StripMetadata { get; set; }
    public List<int> IconSizes { get; set; } = new() { 16, 32, 48, 64, 128, 256 };
}

/// <summary>Saved to &lt;DataRoot&gt;/modules/convert/settings.json.</summary>
public sealed class ConvertSettings
{
    public ConvertOperation LastOperation { get; set; } = ConvertOperation.PdfToImages;
    public ConvertOptions Options { get; set; } = new();
}

/// <summary>A file in the conversion list.</summary>
public sealed class ConvertItem : INotifyPropertyChanged
{
    private string _info = "";
    private bool _isProtected;
    private ItemState _state;
    private double _progress;
    private string _statusText = "";

    public ConvertItem(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Name = System.IO.Path.GetFileName(Path);
        Kind = FileTypes.GetKind(Path) ?? throw new NotSupportedException("Desteklenmeyen dosya türü: " + Name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public string Name { get; }
    public FileKind Kind { get; }
    /// <summary>Password the user entered for this PDF (kept in memory only).</summary>
    public string? Password { get; set; }
    /// <summary>Page count for PDFs once probed (0 = unknown).</summary>
    public int PageCount { get; set; }

    /// <summary>Short secondary text for the list row ("12 sayfa · 1,2 MB").</summary>
    public string Info
    {
        get => _info;
        set { if (_info != value) { _info = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Info))); } }
    }

    /// <summary>List icon (Segoe Fluent): "PDF" (EA90) or "Photo" (EB9F).</summary>
    public string Glyph => Kind == FileKind.Pdf ? "" : "";

    /// <summary>File size in bytes once probed (0 = unknown).</summary>
    public long SizeBytes { get; set; }

    /// <summary>True when the probe found a password-protected PDF (the password is asked when the batch runs).</summary>
    public bool IsProtected
    {
        get => _isProtected;
        set { if (_isProtected != value) { _isProtected = value; Notify(nameof(IsProtected)); } }
    }

    /// <summary>Where this file is in the current or last batch.</summary>
    public ItemState State
    {
        get => _state;
        private set { if (_state != value) { _state = value; Notify(nameof(State)); Notify(nameof(IsBusy)); Notify(nameof(ChipText)); } }
    }

    /// <summary>Progress of this file in percent (0..100) while <see cref="State"/> is Working.</summary>
    public double Progress
    {
        get => _progress;
        private set { if (Math.Abs(_progress - value) > 0.01) { _progress = value; Notify(nameof(Progress)); Notify(nameof(ChipText)); } }
    }

    /// <summary>Result line under the name: output name, warning or error (empty while ready).</summary>
    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText != value) { _statusText = value; Notify(nameof(StatusText)); } }
    }

    public bool IsBusy => _state is ItemState.Queued or ItemState.Working;

    /// <summary>Short status badge text.</summary>
    public string ChipText => _state switch
    {
        ItemState.Queued => "Sırada",
        ItemState.Working => $"%{Math.Round(_progress):0}",
        ItemState.Done => "Tamam",
        ItemState.Warning => "Uyarı",
        ItemState.Failed => "Hata",
        ItemState.Cancelled => "İptal edildi",
        _ => ""
    };

    public void SetState(ItemState state, string statusText = "", double progress = 0)
    {
        State = state;
        Progress = Math.Clamp(progress, 0, 100);
        StatusText = statusText ?? "";
    }

    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>Per-file state shown in the list.</summary>
public enum ItemState { Ready, Queued, Working, Done, Warning, Failed, Cancelled }

/// <summary>Outcome for one source file of a batch.</summary>
public sealed class FileOutcome
{
    public FileOutcome(string source) { Source = source; }

    public string Source { get; }
    /// <summary>Display text for results that cover several inputs (e.g. "3 PDF birleştirildi"); null = use the source file name.</summary>
    public string? Label { get; set; }
    public string SourceName => Label ?? Path.GetFileName(Source);
    public List<string> Outputs { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>Turkish error text; null when the file succeeded.</summary>
    public string? Error { get; set; }
    public bool Success => Error is null;
}

public sealed class BatchOutcome
{
    public List<FileOutcome> Files { get; } = new();
    public bool WasCancelled { get; set; }
    public IEnumerable<string> Outputs => Files.SelectMany(f => f.Outputs);
    public int Succeeded => Files.Count(f => f.Success);
    public int Failed => Files.Count(f => !f.Success);
}

/// <summary>Thrown for expected, user-facing conversion problems (message is Turkish).</summary>
public sealed class ConvertException : Exception
{
    public ConvertException(string message) : base(message) { }
    public ConvertException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>Supported file types and which operations apply to a set of files.</summary>
public static class FileTypes
{
    public static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".tif", ".tiff", ".gif", ".webp", ".ico", ".heic", ".heif", ".jxr", ".wdp"
    };

    public static FileKind? GetKind(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf") return FileKind.Pdf;
        if (Array.IndexOf(ImageExtensions, ext) >= 0) return FileKind.Image;
        return null;
    }

    public static string ImageDialogFilter =>
        "Görseller|" + string.Join(";", ImageExtensions.Select(e => "*" + e)) + "|Tüm dosyalar|*.*";

    public static string PdfDialogFilter => "PDF dosyaları|*.pdf|Tüm dosyalar|*.*";

    public static string AnyDialogFilter =>
        "PDF ve görseller|*.pdf;" + string.Join(";", ImageExtensions.Select(e => "*" + e)) + "|PDF dosyaları|*.pdf|Görseller|" +
        string.Join(";", ImageExtensions.Select(e => "*" + e)) + "|Tüm dosyalar|*.*";

    public static string FilterFor(ConvertOperation operation) => IsPdfOperation(operation) ? PdfDialogFilter
        : operation is ConvertOperation.ImagesToPdf or ConvertOperation.ConvertImages ? ImageDialogFilter : AnyDialogFilter;

    public static bool IsPdfOperation(ConvertOperation operation) => operation is ConvertOperation.PdfToImages or ConvertOperation.MergePdf
        or ConvertOperation.SplitPdf or ConvertOperation.ExtractPages or ConvertOperation.DeletePages or ConvertOperation.RotatePages;

    public static FileKind InputKind(ConvertOperation operation) => IsPdfOperation(operation) ? FileKind.Pdf : FileKind.Image;

    /// <summary>
    /// Operations that can run on the given file kinds. An empty list shows every operation (nothing is selected yet);
    /// a list that mixes PDFs and images has no valid operation.
    /// </summary>
    public static IReadOnlyList<ConvertOperation> ValidOperations(IReadOnlyCollection<FileKind> kinds)
    {
        var all = (ConvertOperation[])Enum.GetValues(typeof(ConvertOperation));
        if (kinds.Count == 0) return all;
        var distinct = kinds.Distinct().ToList();
        if (distinct.Count > 1) return Array.Empty<ConvertOperation>();
        return all.Where(op => InputKind(op) == distinct[0]).ToList();
    }

    public static string Describe(ConvertOperation operation) => operation switch
    {
        ConvertOperation.PdfToImages => "PDF → PNG / JPG",
        ConvertOperation.ImagesToPdf => "Görselleri PDF yap",
        ConvertOperation.MergePdf => "PDF birleştir",
        ConvertOperation.SplitPdf => "PDF böl",
        ConvertOperation.ExtractPages => "PDF'ten sayfa çıkar",
        ConvertOperation.DeletePages => "PDF'ten sayfa sil",
        ConvertOperation.RotatePages => "PDF sayfalarını döndür",
        ConvertOperation.ConvertImages => "Görsel formatını değiştir",
        _ => operation.ToString()
    };
}
