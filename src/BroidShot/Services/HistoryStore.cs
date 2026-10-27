using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace BroidShot.Services;

/// <summary>Stores only flattened, final images. Never stores the capture source or undo history.</summary>
public sealed class HistoryStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string RootDirectory => _root;
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);
    public long MaxStorageBytes { get; set; } = 1024L * 1024 * 1024;

    public HistoryStore(string? rootDirectory = null)
    {
        _root = Path.GetFullPath(rootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BroidShot", "history"));
    }

    public async Task<IReadOnlyList<HistoryItem>> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run<IReadOnlyList<HistoryItem>>(() =>
            {
                Directory.CreateDirectory(_root);
                PruneCore();
                return ReadItemsCore();
            });
        }
        finally { _gate.Release(); }
    }

    public async Task<HistoryItem> AddAsync(BitmapSource image, string? ocrText = null)
    {
        BitmapSource frozen = ImageExporter.FreezeForBackground(image);
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(_root);
                string id = Guid.NewGuid().ToString("N");
                var item = new HistoryItem(id, DateTimeOffset.UtcNow, ImagePath(id), frozen.PixelWidth, frozen.PixelHeight, ocrText);
                try
                {
                    ImageExporter.Save(frozen, item.ImagePath);
                    WriteMetadataCore(item);
                    if (new FileInfo(item.ImagePath).Length + new FileInfo(MetadataPath(id)).Length > MaxStorageBytes)
                        throw new IOException("Bu alıntı geçmiş için ayrılan depolama sınırından büyük. Depolama sınırını artırın veya dosyaya kaydedin.");
                }
                catch
                {
                    if (File.Exists(item.ImagePath)) File.Delete(item.ImagePath);
                    if (File.Exists(MetadataPath(id))) File.Delete(MetadataPath(id));
                    throw;
                }
                PruneCore();
                return item;
            });
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(HistoryItem item)
    {
        Validate(item);
        await _gate.WaitAsync();
        try { await Task.Run(() => DeleteCore(item.Id)); }
        finally { _gate.Release(); }
    }

    public BitmapSource LoadImage(HistoryItem item)
    {
        Validate(item);
        using var stream = new FileStream(ImagePath(item.Id), FileMode.Open, FileAccess.Read, FileShare.Read);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public async Task UpdateOcrAsync(HistoryItem item, string text)
    {
        Validate(item);
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                if (!File.Exists(ImagePath(item.Id)) || !File.Exists(MetadataPath(item.Id)))
                    throw new FileNotFoundException("Alıntı geçmişten silinmiş.");
                WriteMetadataCore(item with { OcrText = text });
            });
        }
        finally { _gate.Release(); }
    }

    private List<HistoryItem> ReadItemsCore()
    {
        var items = new List<HistoryItem>();
        foreach (string file in Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(id)) continue;
            try
            {
                HistoryItem? item = JsonSerializer.Deserialize<HistoryItem>(File.ReadAllText(file), JsonOptions);
                if (item is null || item.Id != id || item.Width <= 0 || item.Height <= 0 || !File.Exists(ImagePath(id))) continue;
                // Ignore the persisted path. File operations always derive it from the validated ID.
                items.Add(item with { ImagePath = ImagePath(id) });
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A malformed entry must not prevent access to the remaining history.
            }
        }
        return items.OrderByDescending(x => x.CreatedAt).ToList();
    }

    private void PruneCore()
    {
        long total = 0;
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (HistoryItem item in ReadItemsCore())
        {
            long size;
            try { size = new FileInfo(ImagePath(item.Id)).Length + new FileInfo(MetadataPath(item.Id)).Length; }
            catch (FileNotFoundException) { continue; }
            if (item.CreatedAt < cutoff || total + size > MaxStorageBytes)
            {
                DeleteCore(item.Id);
                continue;
            }
            total += size;
        }
    }

    private void WriteMetadataCore(HistoryItem item)
    {
        string destination = MetadataPath(item.Id);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(item, JsonOptions));
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void DeleteCore(string id)
    {
        // Remove the image first: an interrupted delete cannot leave readable pixels behind metadata.
        File.Delete(ImagePath(id));
        File.Delete(MetadataPath(id));
    }

    private void Validate(HistoryItem item)
    {
        if (!IsValidId(item.Id) || !string.Equals(Path.GetFullPath(item.ImagePath), ImagePath(item.Id), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Geçersiz geçmiş kaydı.", nameof(item));
    }

    private static bool IsValidId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);
    private string ImagePath(string id) => Path.Combine(_root, id + ".png");
    private string MetadataPath(string id) => Path.Combine(_root, id + ".json");
}
