using System;
using System.IO;
using System.Text.RegularExpressions;

namespace BroidShot.Core;

/// <summary>Parses sha256sum (GNU "&lt;hex&gt;  [*]&lt;name&gt;") and BSD ("SHA256 (&lt;name&gt;) = &lt;hex&gt;") checksum files.</summary>
public static partial class ChecksumFile
{
    /// <summary>Returns the lowercase SHA-256 listed for <paramref name="fileName"/>, or null.</summary>
    public static string? FindSha256(string content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        using var reader = new StringReader(content);
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string? name = null, hash = null;
            Match bsd = BsdLine().Match(line);
            if (bsd.Success) { name = bsd.Groups["name"].Value; hash = bsd.Groups["hash"].Value; }
            else
            {
                Match gnu = GnuLine().Match(line);
                if (gnu.Success) { name = gnu.Groups["name"].Value.Trim(); hash = gnu.Groups["hash"].Value; }
            }
            if (name is null || hash is null) continue;
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (string.Equals(name, fileName, StringComparison.Ordinal)) return hash.ToLowerInvariant();
        }
        return null;
    }

    public static bool IsSha256(string? value) => value is not null && Sha256Hex().IsMatch(value);

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>.+)$")]
    private static partial Regex GnuLine();
    [GeneratedRegex(@"^SHA256 \((?<name>.+)\) = (?<hash>[0-9a-fA-F]{64})$")]
    private static partial Regex BsdLine();
    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();
}
