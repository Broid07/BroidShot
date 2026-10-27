using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace BroidShot.Modules.Clipboard.Storage;

/// <summary>One row of the items table. Payload bytes are DPAPI-protected; times are Unix milliseconds.</summary>
internal sealed record StoredItem
{
    public long Id { get; init; }
    public ClipKind Kind { get; init; }
    public long CreatedAt { get; init; }
    public long LastUsedAt { get; init; }
    public bool Pinned { get; init; }
    public SensitiveReason Reason { get; init; }
    public long? ExpiresAt { get; init; }
    public string? SourceApp { get; init; }
    public bool IsVideoLink { get; init; }
    public string? Hash { get; init; }
    public byte[]? Payload { get; init; }
    public string? ImageFile { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
/// SQLite index (metadata, tags, encrypted payloads) plus DPAPI-encrypted image files under
/// &lt;DataFolder&gt;/images. secure_delete is on so removed rows are overwritten in the database file.
/// Thread-safe: every call takes one lock around a single open connection.
/// </summary>
internal sealed class ClipStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly byte[] _hashKey;
    private bool _disposed;

    public ClipStore(string folder, IContentProtector protector)
    {
        Folder = Path.GetFullPath(folder ?? throw new ArgumentNullException(nameof(folder)));
        Protector = protector ?? throw new ArgumentNullException(nameof(protector));
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(ImagesFolder);
        DatabasePath = Path.Combine(Folder, "clipboard.db");
        _hashKey = LoadOrCreateHashKey();
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA foreign_keys = ON; PRAGMA secure_delete = ON; PRAGMA journal_mode = DELETE;");
        Migrate();
    }

    public string Folder { get; }
    public string DatabasePath { get; }
    public string ImagesFolder => Path.Combine(Folder, "images");
    public IContentProtector Protector { get; }

    private void Migrate()
    {
        long version = Convert.ToInt64(Scalar("PRAGMA user_version;") ?? 0L);
        if (version >= SchemaVersion) return;
        Execute("""
            CREATE TABLE IF NOT EXISTS items(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                last_used_at INTEGER NOT NULL,
                pinned INTEGER NOT NULL DEFAULT 0,
                reason TEXT NULL,
                expires_at INTEGER NULL,
                source_app TEXT NULL,
                video INTEGER NOT NULL DEFAULT 0,
                hash TEXT NULL,
                payload BLOB NULL,
                image_file TEXT NULL,
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_items_hash ON items(hash);
            CREATE INDEX IF NOT EXISTS ix_items_expires ON items(expires_at);
            CREATE TABLE IF NOT EXISTS tags(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS item_tags(
                item_id INTEGER NOT NULL REFERENCES items(id) ON DELETE CASCADE,
                tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY(item_id, tag_id));
            """);
        Execute($"PRAGMA user_version = {SchemaVersion};");
    }

    // ---- hashing (keyed, so equal-content detection does not leak guessable plaintext) ----

    private byte[] LoadOrCreateHashKey()
    {
        string path = Path.Combine(Folder, "hash.key");
        try
        {
            if (File.Exists(path))
            {
                byte[] key = Protector.Unprotect(File.ReadAllBytes(path));
                if (key.Length == 32) return key;
            }
        }
        catch (CryptographicException) { }
        byte[] created = RandomNumberGenerator.GetBytes(32);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, Protector.Protect(created));
        File.Move(temp, path, true);
        return created;
    }

    public string ComputeHash(ReadOnlySpan<byte> content) => Convert.ToHexString(HMACSHA256.HashData(_hashKey, content));

    // ---- items ----

    public long Insert(StoredItem item)
    {
        lock (_gate)
        {
            using SqliteCommand command = Command("""
                INSERT INTO items(kind, created_at, last_used_at, pinned, reason, expires_at, source_app, video, hash, payload, image_file, width, height)
                VALUES($kind, $created, $used, $pinned, $reason, $expires, $source, $video, $hash, $payload, $image, $width, $height);
                SELECT last_insert_rowid();
                """);
            command.Parameters.AddWithValue("$kind", (int)item.Kind);
            command.Parameters.AddWithValue("$created", item.CreatedAt);
            command.Parameters.AddWithValue("$used", item.LastUsedAt);
            command.Parameters.AddWithValue("$pinned", item.Pinned ? 1 : 0);
            command.Parameters.AddWithValue("$reason", item.Reason == SensitiveReason.None ? DBNull.Value : item.Reason.ToString());
            command.Parameters.AddWithValue("$expires", (object?)item.ExpiresAt ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", (object?)item.SourceApp ?? DBNull.Value);
            command.Parameters.AddWithValue("$video", item.IsVideoLink ? 1 : 0);
            command.Parameters.AddWithValue("$hash", (object?)item.Hash ?? DBNull.Value);
            command.Parameters.Add("$payload", SqliteType.Blob).Value = (object?)item.Payload ?? DBNull.Value;
            command.Parameters.AddWithValue("$image", (object?)item.ImageFile ?? DBNull.Value);
            command.Parameters.AddWithValue("$width", item.Width);
            command.Parameters.AddWithValue("$height", item.Height);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    public IReadOnlyList<StoredItem> LoadItems()
    {
        lock (_gate)
        {
            using SqliteCommand command = Command("SELECT id, kind, created_at, last_used_at, pinned, reason, expires_at, source_app, video, hash, payload, image_file, width, height FROM items ORDER BY last_used_at DESC, id DESC;");
            using SqliteDataReader reader = command.ExecuteReader();
            var items = new List<StoredItem>();
            while (reader.Read())
            {
                items.Add(new StoredItem
                {
                    Id = reader.GetInt64(0),
                    Kind = Enum.IsDefined(typeof(ClipKind), reader.GetInt32(1)) ? (ClipKind)reader.GetInt32(1) : ClipKind.Text,
                    CreatedAt = reader.GetInt64(2),
                    LastUsedAt = reader.GetInt64(3),
                    Pinned = reader.GetInt64(4) != 0,
                    Reason = reader.IsDBNull(5) ? SensitiveReason.None
                        : Enum.TryParse(reader.GetString(5), out SensitiveReason reason) && reason != SensitiveReason.None ? reason : SensitiveReason.Secret,
                    ExpiresAt = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    SourceApp = reader.IsDBNull(7) ? null : reader.GetString(7),
                    IsVideoLink = reader.GetInt64(8) != 0,
                    Hash = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Payload = reader.IsDBNull(10) ? null : (byte[])reader.GetValue(10),
                    ImageFile = reader.IsDBNull(11) ? null : reader.GetString(11),
                    Width = reader.GetInt32(12),
                    Height = reader.GetInt32(13),
                });
            }
            return items;
        }
    }

    public int CountItems() { lock (_gate) return Convert.ToInt32(Scalar("SELECT COUNT(*) FROM items;")); }

    public void UpdateUsage(long id, long lastUsedAt, long? expiresAt, string? sourceApp)
    {
        lock (_gate)
        {
            using SqliteCommand command = Command("UPDATE items SET last_used_at = $used, expires_at = $expires, source_app = COALESCE($source, source_app) WHERE id = $id;");
            command.Parameters.AddWithValue("$used", lastUsedAt);
            command.Parameters.AddWithValue("$expires", (object?)expiresAt ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", (object?)sourceApp ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void SetPinned(long id, bool pinned)
    {
        lock (_gate)
        {
            using SqliteCommand command = Command("UPDATE items SET pinned = $pinned WHERE id = $id;");
            command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes rows (tags cascade) and their image files. Returns the ids that existed.</summary>
    public IReadOnlyList<long> DeleteItems(IEnumerable<long> ids)
    {
        long[] list = ids.Distinct().ToArray();
        if (list.Length == 0) return Array.Empty<long>();
        lock (_gate)
        {
            var removed = new List<long>();
            var images = new List<string>();
            using (SqliteTransaction transaction = _connection.BeginTransaction())
            {
                foreach (long id in list)
                {
                    using SqliteCommand select = Command("SELECT image_file FROM items WHERE id = $id;", transaction);
                    select.Parameters.AddWithValue("$id", id);
                    object? image = select.ExecuteScalar();
                    if (image is null) continue;
                    if (image is string file) images.Add(file);
                    using SqliteCommand delete = Command("DELETE FROM items WHERE id = $id;", transaction);
                    delete.Parameters.AddWithValue("$id", id);
                    delete.ExecuteNonQuery();
                    removed.Add(id);
                }
                transaction.Commit();
            }
            foreach (string file in images) DeleteImageFile(file);
            return removed;
        }
    }

    /// <summary>Ids of sensitive items whose 1-hour lifetime ended at or before <paramref name="now"/>.</summary>
    public IReadOnlyList<long> ExpiredIds(long now) => Ids("SELECT id FROM items WHERE expires_at IS NOT NULL AND expires_at <= $now;", ("$now", now));

    /// <summary>Unpinned ids older than <paramref name="minLastUsed"/>, plus the oldest unpinned ones beyond <paramref name="maxItems"/>.</summary>
    public IReadOnlyList<long> RetentionIds(int maxItems, long minLastUsed)
    {
        var ids = new HashSet<long>(Ids("SELECT id FROM items WHERE pinned = 0 AND last_used_at < $min;", ("$min", minLastUsed)));
        if (maxItems >= 0)
            foreach (long id in Ids("SELECT id FROM items WHERE pinned = 0 AND last_used_at >= $min ORDER BY last_used_at DESC, id DESC LIMIT -1 OFFSET $max;", ("$min", minLastUsed), ("$max", maxItems)))
                ids.Add(id);
        return ids.ToArray();
    }

    public IReadOnlyList<long> UnpinnedIds() => Ids("SELECT id FROM items WHERE pinned = 0;");

    // ---- tags ----

    public IReadOnlyList<ClipTag> LoadTags()
    {
        lock (_gate)
        {
            using SqliteCommand command = Command("SELECT id, name FROM tags ORDER BY name COLLATE NOCASE;");
            using SqliteDataReader reader = command.ExecuteReader();
            var tags = new List<ClipTag>();
            while (reader.Read()) tags.Add(new ClipTag(reader.GetInt64(0), reader.GetString(1)));
            return tags;
        }
    }

    public Dictionary<long, HashSet<long>> LoadItemTags()
    {
        lock (_gate)
        {
            using SqliteCommand command = Command("SELECT item_id, tag_id FROM item_tags;");
            using SqliteDataReader reader = command.ExecuteReader();
            var map = new Dictionary<long, HashSet<long>>();
            while (reader.Read())
            {
                long item = reader.GetInt64(0);
                if (!map.TryGetValue(item, out HashSet<long>? set)) map[item] = set = new HashSet<long>();
                set.Add(reader.GetInt64(1));
            }
            return map;
        }
    }

    public long CreateTag(string name)
    {
        lock (_gate)
        {
            using SqliteCommand command = Command("INSERT INTO tags(name) VALUES($name); SELECT last_insert_rowid();");
            command.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    public void RenameTag(long id, string name)
    {
        lock (_gate)
        {
            using SqliteCommand command = Command("UPDATE tags SET name = $name WHERE id = $id;");
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteTag(long id)
    {
        lock (_gate)
        {
            using SqliteCommand command = Command("DELETE FROM tags WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void SetItemTag(long itemId, long tagId, bool assigned)
    {
        lock (_gate)
        {
            using SqliteCommand command = Command(assigned
                ? "INSERT OR IGNORE INTO item_tags(item_id, tag_id) VALUES($item, $tag);"
                : "DELETE FROM item_tags WHERE item_id = $item AND tag_id = $tag;");
            command.Parameters.AddWithValue("$item", itemId);
            command.Parameters.AddWithValue("$tag", tagId);
            command.ExecuteNonQuery();
        }
    }

    // ---- images ----

    /// <summary>Encrypts PNG bytes into images/&lt;guid&gt;.bin and returns the file name.</summary>
    public string WriteImage(byte[] png)
    {
        string name = Guid.NewGuid().ToString("N") + ".bin";
        string path = Path.Combine(ImagesFolder, name);
        File.WriteAllBytes(path + ".tmp", Protector.Protect(png));
        File.Move(path + ".tmp", path, true);
        return name;
    }

    public byte[]? ReadImage(string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        string path = ImagePath(file);
        if (!File.Exists(path)) return null;
        try { return Protector.Unprotect(File.ReadAllBytes(path)); }
        catch (CryptographicException) { return null; }
    }

    public string ImagePath(string file) => Path.Combine(ImagesFolder, Path.GetFileName(file));

    public void DeleteImageFile(string file)
    {
        try { File.Delete(ImagePath(file)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Removes image files no row refers to (e.g. after a crash between writing the file and the row).</summary>
    public int DeleteOrphanImages()
    {
        HashSet<string> referenced;
        lock (_gate)
        {
            using SqliteCommand command = Command("SELECT image_file FROM items WHERE image_file IS NOT NULL;");
            using SqliteDataReader reader = command.ExecuteReader();
            referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) referenced.Add(reader.GetString(0));
        }
        int removed = 0;
        foreach (string path in Directory.EnumerateFiles(ImagesFolder))
        {
            if (referenced.Contains(Path.GetFileName(path))) continue;
            try { File.Delete(path); removed++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    // ---- helpers ----

    private IReadOnlyList<long> Ids(string sql, params (string Name, object Value)[] parameters)
    {
        lock (_gate)
        {
            using SqliteCommand command = Command(sql);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            using SqliteDataReader reader = command.ExecuteReader();
            var ids = new List<long>();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
            return ids;
        }
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = Command(sql);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using SqliteCommand command = Command(sql);
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }
}
