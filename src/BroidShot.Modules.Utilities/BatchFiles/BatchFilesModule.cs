using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using BroidShot.Core;

namespace BroidShot.Modules.Utilities;

internal enum BatchTab { Rename, Organize, Zip }

/// <summary>Toplu dosya: batch rename with preview and undo, organize a folder by type, zip / unzip.</summary>
public sealed class BatchFilesModule : UtilityModuleBase
{
    private RenameJournal? _journal;

    public override string Id => "batchfiles";
    public override string Title => "Toplu dosya";
    public override string Description => "Önizlemeli toplu yeniden adlandırma, klasörü türe göre düzenleme, zip oluşturma ve açma. Son toplu işlem geri alınabilir.";
    public override string Icon => "";
    public override IReadOnlyList<string> Keywords { get; } = new[]
    {
        "toplu", "batch", "dosya", "yeniden adlandır", "rename", "klasör", "düzenle", "organize", "zip", "unzip", "arşiv", "sıkıştır", "aç", "geri al", "undo"
    };

    public override IReadOnlyList<ToolCommand> Commands => new[]
    {
        new ToolCommand("Toplu yeniden adlandır", () => RequestTab(BatchTab.Rename))
        {
            Id = "batchfiles.rename", NavigateToModuleId = Id, Description = "Dosya adlarını önizleyerek toplu değiştirir",
            Keywords = new[] { "rename", "adlandır", "ad değiştir", "numara", "önek" }
        },
        new ToolCommand("Klasörü düzenle", () => RequestTab(BatchTab.Organize))
        {
            Id = "batchfiles.organize", NavigateToModuleId = Id, Description = "Bir klasördeki dosyaları türe göre alt klasörlere ayırır",
            Keywords = new[] { "organize", "ayır", "sırala", "türe göre", "görseller", "belgeler" }
        },
        new ToolCommand("Zip oluştur veya aç", () => RequestTab(BatchTab.Zip))
        {
            Id = "batchfiles.zip", NavigateToModuleId = Id, Description = "Dosyaları zip'le ya da zip dosyasını güvenle aç",
            Keywords = new[] { "zip", "unzip", "arşiv", "sıkıştır", "aç" }
        },
        ToolCommand.Open("Toplu dosya araçlarını aç", Id, "Yeniden adlandır, düzenle, zip; son işlemi geri al", new[] { "toplu", "geri al" }, "batchfiles.open"),
    };

    internal RenameJournal Journal => _journal ?? throw new InvalidOperationException("Modül henüz başlatılmadı.");
    internal BatchTab? PendingTab { get; set; }
    internal event Action<BatchTab>? TabRequested;
    internal event Action? JournalChanged;

    public override void Initialize(IToolHost host)
    {
        base.Initialize(host);
        _journal = new RenameJournal(Path.Combine(host.DataFolder, "rename-journal.json"));
    }

    public override FrameworkElement CreateView() => new BatchFilesView(this);

    private void RequestTab(BatchTab tab)
    {
        PendingTab = tab;
        TabRequested?.Invoke(tab);
    }

    internal void NotifyJournalChanged() => JournalChanged?.Invoke();

}
