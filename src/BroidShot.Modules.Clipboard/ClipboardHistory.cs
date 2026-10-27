using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BroidShot.Core;
using BroidShot.Modules.Clipboard.Storage;

namespace BroidShot.Modules.Clipboard;

public sealed record RetentionPolicy(int MaxItems, TimeSpan MaxAge)
{
    public static RetentionPolicy Default { get; } = new(1000, TimeSpan.FromDays(30));
}

/// <summary>
/// In-memory view of the clipboard history backed by <see cref="ClipStore"/>. Classifies, detects sensitive
/// content, dedupes, applies retention and expires sensitive items one hour after capture.
/// Thread-safe; <see cref="Changed"/> is raised on the calling thread after the lock is released.
/// </summary>
internal sealed partial class ClipboardHistory : IDisposable
{
    public static readonly TimeSpan SensitiveLifetime = TimeSpan.FromHours(1);
    public const int MaxTextLength = 4_000_000;
    public const int MaxImageBytes = 48 * 1024 * 1024;
    private const int PreviewLength = 400;
    private const int SearchLength = 4000;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private readonly object _gate = new();
    private readonly ClipStore _store;
    private readonly TimeProvider _time;
    private List<ClipEntry> _items = new();
    private List<ClipTag> _tags = new();
    private RetentionPolicy _retention = RetentionPolicy.Default;

    public ClipboardHistory(ClipStore store, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler? Changed;

    public bool IsLoaded { get; private set; }

    public RetentionPolicy Retention
    {
        get { lock (_gate) return _retention; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _retention = value with { MaxItems = Math.Max(1, value.MaxItems), MaxAge = value.MaxAge <= TimeSpan.Zero ? TimeSpan.FromDays(1) : value.MaxAge };
        }
    }

    public IReadOnlyList<ClipEntry> Items { get { lock (_gate) return _items.ToArray(); } }

    public IReadOnlyList<ClipTag> Tags { get { lock (_gate) return _tags.ToArray(); } }

    public ClipEntry? Find(long id) { lock (_gate) return _items.FirstOrDefault(e => e.Id == id); }

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Startup: deletes expired sensitive items and retention overflow from disk, then loads the rest.</summary>
    public void Load()
    {
        lock (_gate)
        {
            long now = Now.ToUnixTimeMilliseconds();
            _store.DeleteItems(_store.ExpiredIds(now));
            _store.DeleteItems(_store.RetentionIds(_retention.MaxItems, (Now - _retention.MaxAge).ToUnixTimeMilliseconds()));
            _store.DeleteOrphanImages();
            Dictionary<long, HashSet<long>> tagMap = _store.LoadItemTags();
            var entries = new List<ClipEntry>();
            var unreadable = new List<long>();
            foreach (StoredItem stored in _store.LoadItems())
            {
                try { entries.Add(ToEntry(stored, tagMap.TryGetValue(stored.Id, out HashSet<long>? tags) ? tags : new HashSet<long>())); }
                catch (Exception ex) when (ex is CryptographicException or JsonException)
                {
                    unreadable.Add(stored.Id); // e.g. copied from another Windows account: DPAPI cannot decrypt it.
                }
            }
            _store.DeleteItems(unreadable);
            _items = entries;
            _tags = _store.LoadTags().ToList();
            IsLoaded = true;
        }
        OnChanged();
    }

    // ---- capture ----

    public CaptureResult Add(ClipCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.IsEmpty) return new CaptureResult(CaptureOutcome.Ignored, null, null);

        ClipKind kind;
        ClipPayload payload = ClipPayload.Empty;
        byte[]? png = null;
        int width = 0, height = 0;
        SensitiveReason reason = capture.SensitiveFormat ? SensitiveReason.SourceFlag : SensitiveReason.None;
        bool video = false;
        string? url = null;

        // Priority: files > text (+HTML) > image, so e.g. spreadsheet cells (text + bitmap) stay text.
        string text = capture.HasFiles ? "" : !string.IsNullOrEmpty(capture.Text) ? capture.Text : HtmlToText(capture.Html);
        if (capture.HasFiles)
        {
            kind = ClipKind.Files;
            payload = new ClipPayload(null, null, capture.Files!.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray());
        }
        else if (string.IsNullOrWhiteSpace(text) && capture.HasImage)
        {
            kind = ClipKind.Image;
            png = capture.ImagePng is { Length: > 0 } ? capture.ImagePng : EncodePng(capture.ImagePixels!, capture.ImageWidth, capture.ImageHeight);
            if (png.Length > MaxImageBytes) return new CaptureResult(CaptureOutcome.Ignored, null, null);
            (width, height) = capture.ImageWidth > 0 ? (capture.ImageWidth, capture.ImageHeight) : PngSize(png);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return new CaptureResult(CaptureOutcome.Ignored, null, null);
            string? html = string.IsNullOrEmpty(capture.Html) || capture.Html.Length > MaxTextLength ? null : capture.Html;
            payload = new ClipPayload(text, html, null);
            kind = ClipClassifier.Classify(text);
            if (reason == SensitiveReason.None) reason = SensitiveDetector.Detect(text);
            if (kind == ClipKind.Link && ClipClassifier.TryGetUrl(text, out string found))
            {
                url = found;
                video = ClipClassifier.IsVideoUrl(found);
            }
        }

        bool sensitive = reason != SensitiveReason.None;
        byte[] payloadBytes = SerializePayload(payload);
        string hash = _store.ComputeHash(HashInput(kind, payloadBytes, png));
        CaptureResult result;
        lock (_gate)
        {
            DateTimeOffset now = Now;
            DateTimeOffset? expires = sensitive ? now + SensitiveLifetime : null;
            int index = _items.FindIndex(e => e.Hash == hash);
            if (index >= 0 && _items[index].Sensitive == sensitive)
            {
                ClipEntry existing = _items[index];
                _store.UpdateUsage(existing.Id, now.ToUnixTimeMilliseconds(), expires?.ToUnixTimeMilliseconds(), capture.SourceApp);
                ClipEntry moved = existing with { LastUsedAt = now, ExpiresAt = expires, SourceApp = capture.SourceApp ?? existing.SourceApp, Reason = sensitive ? reason : SensitiveReason.None };
                _items.RemoveAt(index);
                _items.Insert(0, moved);
                result = new CaptureResult(CaptureOutcome.MovedToTop, moved, url);
            }
            else
            {
                if (index >= 0) RemoveLocked(new[] { _items[index].Id }); // sensitivity changed: replace the old row.
                string? imageFile = png is null ? null : _store.WriteImage(png);
                byte[]? protectedPayload = kind == ClipKind.Image ? null : _store.Protector.Protect(payloadBytes);
                var stored = new StoredItem
                {
                    Kind = kind,
                    CreatedAt = now.ToUnixTimeMilliseconds(),
                    LastUsedAt = now.ToUnixTimeMilliseconds(),
                    Reason = reason,
                    ExpiresAt = expires?.ToUnixTimeMilliseconds(),
                    SourceApp = capture.SourceApp,
                    IsVideoLink = video,
                    Hash = hash,
                    Payload = protectedPayload,
                    ImageFile = imageFile,
                    Width = width,
                    Height = height,
                };
                long id;
                try { id = _store.Insert(stored); }
                catch
                {
                    if (imageFile is not null) _store.DeleteImageFile(imageFile);
                    throw;
                }
                ClipEntry entry = BuildEntry(stored with { Id = id }, sensitive ? null : payload, new HashSet<long>());
                _items.Insert(0, entry);
                ApplyRetentionLocked();
                result = new CaptureResult(CaptureOutcome.Added, Find(id) ?? entry, url);
            }
        }
        OnChanged();
        return result;
    }

    // ---- item actions ----

    /// <summary>Decrypted content. Sensitive items are decrypted on demand; null when the item is gone.</summary>
    public ClipPayload? GetPayload(long id)
    {
        ClipEntry? entry = Find(id);
        if (entry is null) return null;
        if (entry.Payload is not null) return entry.Payload;
        if (entry.ProtectedPayload is null) return ClipPayload.Empty;
        return DeserializePayload(_store.Protector.Unprotect(entry.ProtectedPayload));
    }

    public byte[]? GetImagePng(long id) => Find(id) is { ImageFile: { } file } ? _store.ReadImage(file) : null;

    /// <summary>Moves an item to the top (used after copying it back). A sensitive item keeps its original expiry.</summary>
    public void Touch(long id)
    {
        lock (_gate)
        {
            int index = _items.FindIndex(e => e.Id == id);
            if (index < 0) return;
            DateTimeOffset now = Now;
            ClipEntry entry = _items[index] with { LastUsedAt = now };
            _store.UpdateUsage(id, now.ToUnixTimeMilliseconds(), entry.ExpiresAt?.ToUnixTimeMilliseconds(), null);
            _items.RemoveAt(index);
            _items.Insert(0, entry);
        }
        OnChanged();
    }

    public void SetPinned(long id, bool pinned)
    {
        lock (_gate)
        {
            int index = _items.FindIndex(e => e.Id == id);
            if (index < 0) return;
            _store.SetPinned(id, pinned);
            _items[index] = _items[index] with { Pinned = pinned };
            if (!pinned) ApplyRetentionLocked();
        }
        OnChanged();
    }

    public bool Delete(long id)
    {
        int removed;
        lock (_gate) removed = RemoveLocked(new[] { id });
        if (removed > 0) OnChanged();
        return removed > 0;
    }

    /// <summary>Deletes every unpinned item. Returns the number removed.</summary>
    public int ClearUnpinned()
    {
        int removed;
        lock (_gate) removed = RemoveLocked(_items.Where(e => !e.Pinned).Select(e => e.Id).Concat(_store.UnpinnedIds()).ToArray());
        if (removed > 0) OnChanged();
        return removed;
    }

    /// <summary>Deletes sensitive items whose lifetime is over, from memory and disk. Returns the number removed.</summary>
    public int PurgeExpired()
    {
        int removed;
        lock (_gate)
        {
            DateTimeOffset now = Now;
            long[] ids = _items.Where(e => e.ExpiresAt is { } at && at <= now).Select(e => e.Id)
                .Concat(_store.ExpiredIds(now.ToUnixTimeMilliseconds())).ToArray();
            removed = RemoveLocked(ids);
        }
        if (removed > 0) OnChanged();
        return removed;
    }

    public int ApplyRetention()
    {
        int removed;
        lock (_gate) removed = ApplyRetentionLocked();
        if (removed > 0) OnChanged();
        return removed;
    }

    /// <summary>Earliest moment a sensitive item expires, for scheduling the next purge.</summary>
    public DateTimeOffset? NextExpiry { get { lock (_gate) return _items.Where(e => e.ExpiresAt.HasValue).Select(e => e.ExpiresAt).Min(); } }

    private int ApplyRetentionLocked()
    {
        IReadOnlyList<long> ids = _store.RetentionIds(_retention.MaxItems, (Now - _retention.MaxAge).ToUnixTimeMilliseconds());
        return RemoveLocked(ids);
    }

    private int RemoveLocked(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return 0;
        var set = new HashSet<long>(ids);
        IReadOnlyList<long> deleted = _store.DeleteItems(set);
        int before = _items.Count;
        _items.RemoveAll(e => set.Contains(e.Id));
        return Math.Max(deleted.Count, before - _items.Count);
    }

    // ---- tags ----

    public ClipTag CreateTag(string name)
    {
        string clean = ValidateTagName(name);
        ClipTag tag;
        lock (_gate)
        {
            EnsureUniqueTagName(clean, null);
            tag = new ClipTag(_store.CreateTag(clean), clean);
            _tags.Add(tag);
            SortTagsLocked();
        }
        OnChanged();
        return tag;
    }

    public void RenameTag(long id, string name)
    {
        string clean = ValidateTagName(name);
        lock (_gate)
        {
            int index = _tags.FindIndex(t => t.Id == id);
            if (index < 0) throw new InvalidOperationException("Etiket bulunamadı.");
            EnsureUniqueTagName(clean, id);
            _store.RenameTag(id, clean);
            _tags[index] = _tags[index] with { Name = clean };
            SortTagsLocked();
        }
        OnChanged();
    }

    public void DeleteTag(long id)
    {
        lock (_gate)
        {
            _store.DeleteTag(id);
            _tags.RemoveAll(t => t.Id == id);
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].TagIds.Contains(id))
                    _items[i] = _items[i] with { TagIds = new HashSet<long>(_items[i].TagIds.Where(t => t != id)) };
        }
        OnChanged();
    }

    public void SetTag(long itemId, long tagId, bool assigned)
    {
        lock (_gate)
        {
            int index = _items.FindIndex(e => e.Id == itemId);
            if (index < 0 || !_tags.Any(t => t.Id == tagId)) return;
            _store.SetItemTag(itemId, tagId, assigned);
            var tags = new HashSet<long>(_items[index].TagIds);
            if (assigned) tags.Add(tagId); else tags.Remove(tagId);
            _items[index] = _items[index] with { TagIds = tags };
        }
        OnChanged();
    }

    private static string ValidateTagName(string? name)
    {
        string clean = (name ?? "").Trim();
        if (clean.Length == 0) throw new ArgumentException("Etiket adı boş olamaz.");
        if (clean.Length > 40) throw new ArgumentException("Etiket adı en fazla 40 karakter olabilir.");
        return clean;
    }

    private void EnsureUniqueTagName(string name, long? exceptId)
    {
        if (_tags.Any(t => t.Id != exceptId && string.Compare(t.Name, name, Turkish, CompareOptions.IgnoreCase) == 0))
            throw new InvalidOperationException($"\"{name}\" adlı bir etiket zaten var.");
    }

    private void SortTagsLocked() => _tags.Sort((a, b) => string.Compare(a.Name, b.Name, Turkish, CompareOptions.IgnoreCase));

    // ---- conversion helpers ----

    private ClipEntry ToEntry(StoredItem stored, HashSet<long> tags)
    {
        ClipPayload? payload = null;
        if (stored.Reason == SensitiveReason.None)
            payload = stored.Payload is null ? ClipPayload.Empty : DeserializePayload(_store.Protector.Unprotect(stored.Payload));
        return BuildEntry(stored, payload, tags);
    }

    private static ClipEntry BuildEntry(StoredItem stored, ClipPayload? payload, HashSet<long> tags)
    {
        bool sensitive = stored.Reason != SensitiveReason.None;
        string preview;
        string search = "";
        if (stored.Kind == ClipKind.Image)
            preview = stored.Width > 0 ? $"Görsel · {stored.Width}×{stored.Height}" : "Görsel";
        else if (sensitive)
            preview = SensitiveDetector.Mask(null);
        else if (stored.Kind == ClipKind.Files)
        {
            IReadOnlyList<string> files = payload?.Files ?? Array.Empty<string>();
            preview = files.Count == 1 ? files[0] : $"{files.Count} dosya: " + string.Join(", ", files.Take(6).Select(SafeFileName));
            search = SearchMatcher.Normalize(Truncate(string.Join(" ", files), SearchLength));
        }
        else
        {
            string text = payload?.Text ?? "";
            preview = Truncate(text.Trim(), PreviewLength);
            search = SearchMatcher.Normalize(Truncate(text, SearchLength));
        }
        return new ClipEntry
        {
            Id = stored.Id,
            Kind = stored.Kind,
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(stored.CreatedAt),
            LastUsedAt = DateTimeOffset.FromUnixTimeMilliseconds(stored.LastUsedAt),
            Pinned = stored.Pinned,
            Reason = stored.Reason,
            ExpiresAt = stored.ExpiresAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null,
            SourceApp = stored.SourceApp,
            IsVideoLink = stored.IsVideoLink,
            ImageFile = stored.ImageFile,
            Width = stored.Width,
            Height = stored.Height,
            Hash = stored.Hash,
            Payload = sensitive ? null : payload,
            ProtectedPayload = sensitive ? stored.Payload : null,
            TagIds = tags,
            SearchText = search,
            Preview = preview,
        };
    }

    private static string SafeFileName(string path)
    {
        try { string name = Path.GetFileName(path.TrimEnd('\\', '/')); return name.Length > 0 ? name : path; }
        catch (ArgumentException) { return path; }
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    private static byte[] HashInput(ClipKind kind, byte[] payload, byte[]? png)
    {
        byte[] content = png ?? payload;
        byte[] buffer = new byte[content.Length + 1];
        buffer[0] = (byte)(kind == ClipKind.Image ? 1 : kind == ClipKind.Files ? 2 : 0);
        content.CopyTo(buffer, 1);
        return buffer;
    }

    private sealed class PayloadDto
    {
        [JsonPropertyName("t")] public string? Text { get; set; }
        [JsonPropertyName("h")] public string? Html { get; set; }
        [JsonPropertyName("f")] public List<string>? Files { get; set; }
    }

    internal static byte[] SerializePayload(ClipPayload payload) => JsonSerializer.SerializeToUtf8Bytes(new PayloadDto
    {
        Text = payload.Text,
        Html = payload.Html,
        Files = payload.Files?.ToList(),
    });

    internal static ClipPayload DeserializePayload(byte[] bytes)
    {
        PayloadDto dto = JsonSerializer.Deserialize<PayloadDto>(bytes) ?? new PayloadDto();
        return new ClipPayload(dto.Text, dto.Html, dto.Files);
    }

    internal static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        int stride = width * 4;
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    internal static (int Width, int Height) PngSize(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P') return (0, 0);
        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (width, height);
    }

    /// <summary>Plain text from a CF_HTML clipboard string (header and fragment markers removed).</summary>
    internal static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        string body = html;
        int start = body.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        int end = body.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start) body = body[(start + 20)..end];
        else
        {
            int tag = body.IndexOf('<');
            if (tag > 0 && body.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)) body = body[tag..];
        }
        body = ScriptRegex().Replace(body, " ");
        body = BlockTagRegex().Replace(body, "\n");
        body = TagRegex().Replace(body, "");
        body = WebUtility.HtmlDecode(body);
        var builder = new StringBuilder();
        foreach (string line in body.Split('\n'))
        {
            string collapsed = SpaceRegex().Replace(line, " ").Trim();
            if (collapsed.Length > 0) builder.AppendLine(collapsed);
        }
        return builder.ToString().TrimEnd();
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex SpaceRegex();

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _store.Dispose();
}
