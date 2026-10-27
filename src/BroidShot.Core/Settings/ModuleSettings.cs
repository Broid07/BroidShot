using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BroidShot.Core;

/// <summary>
/// Typed JSON settings for one module (&lt;DataRoot&gt;/modules/&lt;id&gt;/settings.json).
/// Load never throws for a missing or damaged file (it returns defaults); Save is atomic.
/// Thread-safe. Use one settings class per module; add properties with defaults so older files keep loading.
/// </summary>
public sealed class ModuleSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _gate = new();

    public ModuleSettings(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Ayar dosyası yolu boş olamaz.", nameof(filePath));
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public bool Exists => File.Exists(FilePath);

    public T Load<T>() where T : class, new()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new T();
                return JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath), Options) ?? new T();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                ErrorLog.Write("settings", "Ayar dosyası okunamadı, varsayılanlar kullanılıyor: " + FilePath, ex);
                return new T();
            }
        }
    }

    public void Save<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, FilePath, true);
        }
    }
}
