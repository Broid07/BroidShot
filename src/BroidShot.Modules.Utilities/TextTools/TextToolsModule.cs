using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BroidShot.Core;

namespace BroidShot.Modules.Utilities;

/// <summary>Metin araçları: JSON, Base64, URL, harf, satır, sayım, özet, UUID, zaman damgası.</summary>
public sealed class TextToolsModule : UtilityModuleBase
{
    private readonly IClipboardService _clipboard;
    private IReadOnlyList<ToolCommand>? _commands;

    public TextToolsModule() : this(SystemClipboard.Instance) { }

    internal TextToolsModule(IClipboardService clipboard) { _clipboard = clipboard; }

    public override string Id => "texttools";
    public override string Title => "Metin araçları";
    public override string Description => "JSON biçimlendir, Base64, URL kodlama, harf dönüşümü, satır işlemleri, özet ve zaman damgası.";
    public override string Icon => "";
    public override IReadOnlyList<string> Keywords { get; } = new[]
    {
        "metin", "text", "json", "base64", "url", "encode", "decode", "hash", "md5", "sha", "uuid", "guid", "zaman damgası", "timestamp",
        "büyük harf", "küçük harf", "sırala", "kelime sayısı", "biçimlendir", "format"
    };

    /// <summary>One palette command per operation; each works on the clipboard text.</summary>
    public override IReadOnlyList<ToolCommand> Commands => _commands ??= BuildCommands();

    public override FrameworkElement CreateView() => new TextToolsView(this);

    private IReadOnlyList<ToolCommand> BuildCommands()
    {
        var list = new List<ToolCommand>();
        foreach (TextOperation op in TextOperations.All.Where(o => o.InPalette))
        {
            TextOperation captured = op;
            list.Add(new ToolCommand(op.PaletteTitle ?? op.Title, () => RunOnClipboard(captured))
            {
                Id = "texttools." + op.Id,
                Description = op.Description + " — panodaki metin üzerinde çalışır",
                Keywords = new[] { op.Group, "metin", "pano", "text" }
            });
        }
        list.Add(ToolCommand.Open("Metin araçlarını aç", Id, "Girdi ve çıktı kutularıyla metin işlemleri", new[] { "metin", "json", "base64" }, "texttools.open"));
        return list;
    }

    /// <summary>Applies the operation to the clipboard text and puts the result back (counting/validation only show a notification).</summary>
    internal void RunOnClipboard(TextOperation op)
    {
        try
        {
            string text = _clipboard.GetText() ?? "";
            if (op.NeedsInput && text.Length == 0) { Toast("Panoda metin yok", op.Title + " için önce bir metin kopyala.", ToastKind.Warning); return; }
            TextResult result = op.Run(text);
            if (result.IsError) { Toast(op.Title, result.Error!, ToastKind.Error); return; }
            string output = result.Output ?? "";
            bool replaces = op.Id is not ("count" or "json.validate");
            if (replaces)
            {
                _clipboard.SetText(output);
                Toast(op.Title, "Sonuç panoya kopyalandı: " + QrModule.Shorten(output), ToastKind.Success);
            }
            else Toast(op.Title, output, ToastKind.Info);
        }
        catch (Exception ex)
        {
            LogError("Metin işlemi başarısız: " + op.Id, ex);
            Toast(op.Title, "İşlem tamamlanamadı.", ToastKind.Error);
        }
    }

    internal void CopyText(string text)
    {
        try { _clipboard.SetText(text); Toast("Kopyalandı", QrModule.Shorten(text), ToastKind.Success); }
        catch (Exception ex) { LogError("Metin kopyalanamadı", ex); Toast("Kopyalanamadı", "Pano şu an kullanılamıyor.", ToastKind.Error); }
    }
}
