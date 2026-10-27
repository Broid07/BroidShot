using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace BroidShot.Updates;

internal sealed record PackageFile(string Path, string Sha256);

internal sealed class PackageManifest
{
    internal const string FileName = "BroidShot.package.json";
    public string Version { get; set; } = "";
    public List<PackageFile> Files { get; set; } = new();

    internal static PackageManifest Load(string folder, bool verify = true)
    {
        string root = System.IO.Path.GetFullPath(folder);
        RejectLinks(root);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(System.IO.Path.Combine(root, FileName)))
            ?? throw new InvalidDataException("Güncelleme paketinin dosya listesi okunamadı.");
        if (!AppUpdateService.TryVersion(manifest.Version, out _) || manifest.Files.Count is < 3 or > 5000)
            throw new InvalidDataException("Güncelleme paketinin sürümü veya dosya listesi geçersiz.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageFile file in manifest.Files)
        {
            string path = Inside(root, file.Path);
            if (!seen.Add(file.Path.Replace('/', '\\')) || !BroidShot.Core.ChecksumFile.IsSha256(file.Sha256))
                throw new InvalidDataException("Güncelleme paketinin dosya kaydı geçersiz.");
            if (verify)
            {
                RejectLinks(path);
                using var stream = File.OpenRead(path);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Güncelleme dosyası doğrulanamadı: " + file.Path);
            }
        }
        foreach (string required in new[] { "BroidShot.exe", "BroidShot.dll", "BroidShot.runtimeconfig.json" })
            if (!seen.Contains(required)) throw new InvalidDataException("Güncelleme paketinde gerekli dosya eksik: " + required);
        return manifest;
    }

    internal static string Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("Güncelleme paketinde geçersiz dosya yolu var.");
        string prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Dosya paket klasörünün dışında.");
        return full;
    }

    internal static void RejectLinks(string path)
    {
        for (string? item = System.IO.Path.GetFullPath(path); item is not null; item = System.IO.Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Güncelleme klasörü bağlantı içermemeli.");
    }
}
