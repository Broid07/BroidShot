using System;
using System.Collections.Generic;
using System.Windows;
using BroidShot.Core;

namespace BroidShot.Modules.Convert;

/// <summary>
/// File conversion tool: PDF → PNG/JPG, images → PDF, PDF merge / split / page edit, and image format conversion.
/// Registration (done at integration time): ProjectReference to this project and
/// <c>new BroidShot.Modules.Convert.ConvertModule(),</c> in ModuleRegistry.
/// </summary>
public sealed class ConvertModule : IToolModule
{
    private IToolHost? _host;
    private IDisposable? _filesSubscription;
    private ConvertView? _view;
    private ConvertOperation? _pendingOperation;
    private List<string>? _pendingFiles;
    private ConvertSettings _settings = new();

    public string Id => "convert";
    public string Title => "Dönüştür";
    public string Description => "PDF ve görselleri dönüştür, birleştir, böl; görsel biçimini ve boyutunu değiştir.";
    public string Category => ToolCategories.Files;
    public string Icon => "";
    public IReadOnlyList<string> Keywords { get; } = new[]
    {
        "dönüştür", "çevir", "pdf", "png", "jpg", "jpeg", "görsel", "resim", "birleştir", "böl", "ayır", "sayfa", "ico", "webp", "tiff",
        "convert", "merge", "split", "image", "format", "resize", "boyutlandır"
    };

    public IReadOnlyList<ToolCommand> Commands { get; }

    public ConvertModule()
    {
        Commands = new[]
        {
            Command("PDF'i PNG'ye çevir", "convert.pdf-to-png", ConvertOperation.PdfToImages,
                "PDF sayfalarını PNG ya da JPG görsellerine çıkarır", "pdf", "png", "jpg", "sayfa", "görsel", "export"),
            Command("Görselleri PDF yap", "convert.images-to-pdf", ConvertOperation.ImagesToPdf,
                "Görselleri sırayla tek bir PDF'te toplar", "görsel", "resim", "pdf", "foto", "tara"),
            Command("PDF birleştir", "convert.merge-pdf", ConvertOperation.MergePdf,
                "Birden çok PDF'i tek dosyada birleştirir", "pdf", "birleştir", "merge", "ekle"),
            Command("PDF böl", "convert.split-pdf", ConvertOperation.SplitPdf,
                "PDF'i sayfa sayfa ya da aralıklara göre parçalar", "pdf", "böl", "ayır", "split", "sayfa"),
            Command("Görsel formatını değiştir", "convert.image-format", ConvertOperation.ConvertImages,
                "PNG, JPG, BMP, TIFF, GIF, ICO arasında dönüştürür; boyutlandırır", "görsel", "resim", "format", "png", "jpg", "ico", "webp", "boyutlandır"),
        };
    }

    internal IToolHost Host => _host ?? throw new InvalidOperationException("Modül henüz başlatılmadı.");
    internal ConvertSettings Settings => _settings;

    private ToolCommand Command(string title, string id, ConvertOperation operation, string description, params string[] keywords) =>
        new(title, () => StartOperation(operation))
        {
            Id = id,
            Description = description,
            Keywords = keywords,
            NavigateToModuleId = Id
        };

    public void Initialize(IToolHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = host.Settings.Load<ConvertSettings>();
        _filesSubscription = host.Messages.Subscribe<FilesToConvertMessage>(OnFilesToConvert);
    }

    public FrameworkElement CreateView()
    {
        _view = new ConvertView(this);
        if (_pendingFiles is { Count: > 0 } files)
        {
            _pendingFiles = null;
            _view.AddFilesFromOutside(files);
        }
        if (_pendingOperation is { } operation)
        {
            _pendingOperation = null;
            _view.StartOperation(operation);
        }
        return _view;
    }

    public void Shutdown()
    {
        try { _filesSubscription?.Dispose(); } catch (Exception ex) { _host?.Log("Abonelik kapatılamadı.", ex); }
        _filesSubscription = null;
        try { _view?.Shutdown(); } catch (Exception ex) { _host?.Log("Dönüştürme sayfası kapatılamadı.", ex); }
    }

    /// <summary>Palette command: select the operation on the page and open the file dialog.</summary>
    private void StartOperation(ConvertOperation operation)
    {
        if (_view is null) { _pendingOperation = operation; return; }
        _view.StartOperation(operation);
    }

    private void OnFilesToConvert(FilesToConvertMessage message)
    {
        if (message.Paths is not { Count: > 0 }) return;
        Host.NavigateToSelf();
        if (_view is null) { (_pendingFiles ??= new List<string>()).AddRange(message.Paths); return; }
        _view.AddFilesFromOutside(message.Paths);
    }

    internal void SaveSettings()
    {
        try { Host.Settings.Save(_settings); }
        catch (Exception ex) { Host.Log("Dönüştürme ayarları kaydedilemedi.", ex); }
    }
}
