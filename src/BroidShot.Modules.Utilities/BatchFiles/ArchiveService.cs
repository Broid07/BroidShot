using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace BroidShot.Modules.Utilities;

/// <summary>Thrown for archive entries that would be written outside the destination folder.</summary>
internal sealed class ZipSlipException : IOException
{
    public ZipSlipException(string entryName)
        : base($"Arşiv güvenli değil: “{entryName}” girdisi hedef klasörün dışına yazmaya çalışıyor. Hiçbir dosya çıkarılmadı.") { EntryName = entryName; }

    public string EntryName { get; }
}

internal sealed record ExtractResult(int Files, string Destination);

/// <summary>Zip creation and extraction. Extraction validates every entry first (zip-slip) and never overwrites existing files.</summary>
internal static class ArchiveService
{
    public const int MaxEntries = 100_000;
    public const long MaxTotalBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>Returns <paramref name="path"/>, or "name (2).ext" etc. when it is taken.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string directory = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Zips files (and, recursively, folders) into <paramref name="zipPath"/>, which must not exist yet. The archive is built in a temporary
    /// file and moved into place, so a failure leaves nothing behind. Source files are only read.
    /// </summary>
    public static int CreateZip(IReadOnlyList<string> paths, string zipPath, IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (paths.Count == 0) throw new ArgumentException("Eklenecek dosya yok.", nameof(paths));
        string fullZip = Path.GetFullPath(zipPath);
        if (File.Exists(fullZip)) throw new IOException("Bu adla bir zip dosyası zaten var.");

        var items = new List<(string Source, string Entry)>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                if (string.Equals(Path.GetFullPath(path), fullZip, StringComparison.OrdinalIgnoreCase)) continue;
                items.Add((path, UniqueEntry(Path.GetFileName(path), usedNames)));
            }
            else if (Directory.Exists(path))
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                string parent = Path.GetDirectoryName(full) ?? full;
                foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFullPath(file), fullZip, StringComparison.OrdinalIgnoreCase)) continue;
                    items.Add((file, UniqueEntry(Path.GetRelativePath(parent, file).Replace('\\', '/'), usedNames)));
                }
            }
            else throw new FileNotFoundException("Dosya bulunamadı: " + Path.GetFileName(path));
        }
        if (items.Count == 0) throw new ArgumentException("Eklenecek dosya yok.", nameof(paths));

        Directory.CreateDirectory(Path.GetDirectoryName(fullZip)!);
        string temp = fullZip + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    (string source, string entryName) = items[i];
                    ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    DateTime written = File.GetLastWriteTime(source);
                    entry.LastWriteTime = written.Year is >= 1980 and <= 2107 ? written : new DateTime(1980, 1, 1);
                    using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using Stream output = entry.Open();
                    input.CopyTo(output);
                    progress?.Report((i + 1) / (double)items.Count);
                }
            }
            File.Move(temp, fullZip, false);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
        return items.Count;
    }

    private static string UniqueEntry(string name, HashSet<string> used)
    {
        string candidate = name;
        string directory = Path.GetDirectoryName(name)?.Replace('\\', '/') ?? "";
        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        for (int i = 2; !used.Add(candidate); i++)
            candidate = (directory.Length > 0 ? directory + "/" : "") + $"{stem} ({i}){extension}";
        return candidate;
    }

    /// <summary>
    /// Validates the whole archive, then extracts into <paramref name="destination"/> (created if needed). Entries that are rooted, contain
    /// "..", drive letters or stream separators, or resolve outside the destination reject the archive with <see cref="ZipSlipException"/>
    /// before anything is written. Existing files are kept: a clashing entry gets " (2)" in its name.
    /// </summary>
    public static ExtractResult Extract(string zipPath, string destination, IProgress<double>? progress = null, CancellationToken token = default)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("Arşivde çok fazla girdi var.");

        var plan = new List<(ZipArchiveEntry Entry, string Target, bool IsDirectory)>();
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target = ResolveEntry(root, entry.FullName, out bool isDirectory);
            if (!isDirectory)
            {
                total += entry.Length;
                if (total > MaxTotalBytes) throw new InvalidDataException("Arşivin açılmış boyutu çok büyük.");
            }
            plan.Add((entry, target, isDirectory));
        }

        Directory.CreateDirectory(root);
        int files = 0;
        for (int i = 0; i < plan.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            (ZipArchiveEntry entry, string target, bool isDirectory) = plan[i];
            if (isDirectory) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string finalPath = UniquePath(target);
                using Stream input = entry.Open();
                using var output = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyLimited(input, output, entry.Length);
                try { File.SetLastWriteTime(finalPath, entry.LastWriteTime.LocalDateTime); } catch (ArgumentOutOfRangeException) { }
                files++;
            }
            progress?.Report((i + 1) / (double)plan.Count);
        }
        return new ExtractResult(files, root);
    }

    /// <summary>Full path of an archive entry inside <paramref name="root"/>; throws <see cref="ZipSlipException"/> if it would escape.</summary>
    public static string ResolveEntry(string root, string entryName, out bool isDirectory)
    {
        string normalized = entryName.Replace('\\', '/');
        isDirectory = normalized.EndsWith('/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0'))
            throw new ZipSlipException(entryName);
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s == ".."))
            throw new ZipSlipException(entryName);
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ZipSlipException(entryName);
        }
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ZipSlipException(entryName);
        return full;
    }

    private static void CopyLimited(Stream input, Stream output, long declaredLength)
    {
        byte[] buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > declaredLength) throw new InvalidDataException("Arşiv bozuk: girdi bildirilenden büyük.");
            output.Write(buffer, 0, read);
        }
    }
}
