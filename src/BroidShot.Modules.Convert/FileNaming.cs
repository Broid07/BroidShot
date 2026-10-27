using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BroidShot.Modules.Convert;

/// <summary>Output naming: file name templates, sanitising and collision-free names.</summary>
public static class FileNaming
{
    public const string DefaultPageTemplate = "{ad}_s{sayfa}";

    private static readonly CultureInfo Turkish = new("tr-TR");

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly Regex TokenPattern = new(@"\{(?<name>[A-Za-zÇĞİÖŞÜçğıöşü]+)(?::(?<pad>\d{1,2}))?\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Replaces characters that are invalid in Windows file names and avoids reserved names.</summary>
    public static string Sanitize(string? name, string fallback = "dosya")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var sb = new StringBuilder(name.Length);
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 || c < 32 ? '_' : c);
        string result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0) return fallback;
        string stem = result.Contains('.') ? result[..result.IndexOf('.')] : result;
        if (ReservedNames.Contains(stem)) result = "_" + result;
        if (result.Length > 180) result = result[..180].TrimEnd('.', ' ');
        return result;
    }

    /// <summary>
    /// Expands a name template. Tokens: {ad} source name without extension, {sayfa} page number (or range label),
    /// {sayfa:3} zero-padded to 3 digits, {toplam} page count. Unknown tokens stay as typed. The result is sanitised.
    /// </summary>
    public static string ExpandTemplate(string? template, string sourceName, string pageLabel, int totalPages)
    {
        string pattern = string.IsNullOrWhiteSpace(template) ? DefaultPageTemplate : template;
        string expanded = TokenPattern.Replace(pattern, match =>
        {
            string key = match.Groups["name"].Value.ToLower(Turkish);
            int pad = match.Groups["pad"].Success ? int.Parse(match.Groups["pad"].Value, CultureInfo.InvariantCulture) : 0;
            switch (key)
            {
                case "ad": return sourceName;
                case "sayfa":
                    return pad > 0 && int.TryParse(pageLabel, NumberStyles.None, CultureInfo.InvariantCulture, out int page)
                        ? page.ToString("D" + pad, CultureInfo.InvariantCulture)
                        : pageLabel;
                case "toplam": return totalPages.ToString(CultureInfo.InvariantCulture);
                default: return match.Value;
            }
        });
        return Sanitize(expanded, sourceName);
    }

    /// <summary>
    /// Returns <paramref name="desiredPath"/> when it is free; otherwise "name (2).ext", "name (3).ext", … The file
    /// system is the only source of truth, so existing files are never overwritten.
    /// </summary>
    public static string GetUniquePath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath)) return desiredPath;
        string directory = Path.GetDirectoryName(desiredPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(desiredPath);
        string extension = Path.GetExtension(desiredPath);
        for (int n = 2; n < 100_000; n++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Boş bir dosya adı bulunamadı: " + desiredPath);
    }

    /// <summary>
    /// Writes a file atomically: the content goes to a temporary file in the target folder, then the file is moved
    /// into place without overwriting. When the name is taken (even by a file that appears meanwhile) the next free
    /// " (n)" name is used. Returns the final path. The temporary file is removed on failure or cancellation.
    /// </summary>
    public static async Task<string> WriteAtomicAsync(string desiredPath, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        string directory = Path.GetDirectoryName(Path.GetFullPath(desiredPath)) ?? throw new IOException("Geçersiz çıktı yolu.");
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            for (int attempt = 0; attempt < 200; attempt++)
            {
                string target = GetUniquePath(desiredPath);
                try
                {
                    File.Move(temp, target, overwrite: false);
                    return target;
                }
                catch (IOException) when (File.Exists(target)) { /* lost a race for this name; try the next one */ }
            }
            throw new IOException("Çıktı dosyası için boş bir ad bulunamadı: " + desiredPath);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static Task<string> WriteAtomicAsync(string desiredPath, byte[] content, CancellationToken cancellationToken) =>
        WriteAtomicAsync(desiredPath, (stream, token) => stream.WriteAsync(content, token).AsTask(), cancellationToken);
}
