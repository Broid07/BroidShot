using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BroidShot.Modules.Media;

internal sealed class HistoryEntry
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string? FilePath { get; set; }
    public DownloadPreset Preset { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    [JsonIgnore] public string FileName => FilePath is null ? "" : Path.GetFileName(FilePath);
    [JsonIgnore] public string DateText => CompletedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
    [JsonIgnore] public bool FileExists => FilePath is not null && File.Exists(FilePath);
}

/// <summary>Completed downloads, newest first, stored as JSON in the module's data folder.</summary>
internal sealed class DownloadHistory
{
    public const int MaxEntries = 500;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly object _gate = new();
    private List<HistoryEntry> _entries = new();

    public DownloadHistory(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    public IReadOnlyList<HistoryEntry> Entries { get { lock (_gate) return _entries.ToArray(); } }

    public event Action? Changed;

    /// <summary>Loads the file; a missing or damaged file gives an empty history.</summary>
    public void Load()
    {
        List<HistoryEntry> loaded;
        try
        {
            loaded = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath), Options) ?? new List<HistoryEntry>()
                : new List<HistoryEntry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            Core.ErrorLog.Write("media", "İndirme geçmişi okunamadı; boş geçmişle devam ediliyor.", ex);
            loaded = new List<HistoryEntry>();
        }
        lock (_gate) _entries = loaded.OrderByDescending(e => e.CompletedAt).Take(MaxEntries).ToList();
        Changed?.Invoke();
    }

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }
        Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Save();
        Changed?.Invoke();
    }

    public void Save()
    {
        HistoryEntry[] snapshot;
        lock (_gate) snapshot = _entries.ToArray();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Options));
            File.Move(temp, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Core.ErrorLog.Write("media", "İndirme geçmişi kaydedilemedi.", ex);
        }
    }
}
