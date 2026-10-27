using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BroidShot.Core;

namespace BroidShot.Modules.Utilities;

/// <summary>One text template.</summary>
public sealed class Snippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Optional short keyword (searchable in the palette). No global keystroke watching is done.</summary>
    public string Trigger { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>Optional folder label.</summary>
    public string Folder { get; set; } = "";
    public DateTime Modified { get; set; } = DateTime.Now;

    public Snippet Clone() => (Snippet)MemberwiseClone();
}

internal sealed class SnippetFile
{
    public int Version { get; set; } = 1;
    public List<Snippet> Snippets { get; set; } = new();
}

/// <summary>JSON-backed snippet list (atomic writes, thread-safe).</summary>
internal sealed class SnippetStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private List<Snippet> _items = new();

    public SnippetStore(string filePath)
    {
        _path = filePath;
        Load();
    }

    public string FilePath => _path;

    /// <summary>Comparison key: Turkish-insensitive (İ, ı, I and i are the same letter), case-insensitive.</summary>
    private static string Fold(string? text) => SearchMatcher.Normalize(text?.Trim());
    public bool LoadFailed { get; private set; }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) { _items = new List<Snippet>(); return; }
                var file = JsonSerializer.Deserialize<SnippetFile>(File.ReadAllText(_path), Options);
                _items = (file?.Snippets ?? new List<Snippet>()).Where(s => !string.IsNullOrWhiteSpace(s.Id)).ToList();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LoadFailed = true;
                _items = new List<Snippet>();
                try { File.Copy(_path, _path + ".bozuk", true); } catch { }
                ErrorLog.Write("snippets", "Snippet dosyası okunamadı, boş liste kullanılıyor.", ex);
            }
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new SnippetFile { Snippets = _items }, Options));
        File.Move(temp, _path, true);
    }

    public IReadOnlyList<Snippet> All
    {
        get
        {
            lock (_gate)
                return _items.OrderBy(s => s.Folder, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s => s.Clone()).ToList();
        }
    }

    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_gate)
                return _items.Select(s => s.Folder.Trim()).Where(f => f.Length > 0).GroupBy(Fold).Select(g => g.First())
                    .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public Snippet? Get(string id)
    {
        lock (_gate) return _items.FirstOrDefault(s => s.Id == id)?.Clone();
    }

    /// <summary>Returns a Turkish error message, or null when the snippet is acceptable.</summary>
    public string? Validate(Snippet snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet.Name)) return "Ad boş olamaz.";
        if (string.IsNullOrEmpty(snippet.Content)) return "İçerik boş olamaz.";
        string trigger = snippet.Trigger.Trim();
        if (trigger.Length > 0)
        {
            if (Regex.IsMatch(trigger, @"\s")) return "Tetikleyici kelime boşluk içeremez.";
            lock (_gate)
                if (_items.Any(s => s.Id != snippet.Id && Fold(s.Trigger) == Fold(trigger)))
                    return "Bu tetikleyici kelime başka bir şablonda kullanılıyor.";
        }
        return null;
    }

    /// <summary>Adds or replaces (by Id). Returns false with an error message when validation fails.</summary>
    public bool Save(Snippet snippet, out string? error)
    {
        error = Validate(snippet);
        if (error is not null) return false;
        lock (_gate)
        {
            var stored = snippet.Clone();
            stored.Name = stored.Name.Trim();
            stored.Trigger = stored.Trigger.Trim();
            stored.Folder = stored.Folder.Trim();
            stored.Modified = DateTime.Now;
            int index = _items.FindIndex(s => s.Id == stored.Id);
            if (index >= 0) _items[index] = stored; else _items.Add(stored);
            try { Persist(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = "Şablonlar kaydedilemedi: " + ex.Message;
                return false;
            }
        }
        return true;
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            int removed = _items.RemoveAll(s => s.Id == id);
            if (removed == 0) return false;
            try { Persist(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            return true;
        }
    }

    /// <summary>Turkish-insensitive search over name, trigger, folder and content, best match first. Empty query returns everything.</summary>
    public IReadOnlyList<Snippet> Search(string? query, string? folder = null)
    {
        IEnumerable<Snippet> source = All;
        if (!string.IsNullOrEmpty(folder))
            source = source.Where(s => Fold(s.Folder) == Fold(folder));
        if (string.IsNullOrWhiteSpace(query)) return source.ToList();
        return source
            .Select(s => (Snippet: s, Score: SearchMatcher.Score(query, s.Name, new[] { s.Trigger, s.Folder }, s.Content)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Snippet.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Snippet).ToList();
    }
}
