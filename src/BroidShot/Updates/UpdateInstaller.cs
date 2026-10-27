using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BroidShot.Updates;

internal sealed class UpdatePlan
{
    public string InstallDirectory { get; set; } = "";
    public string SourceDirectory { get; set; } = "";
    public int ProcessId { get; set; }
    public string Token { get; set; } = Guid.NewGuid().ToString("N");
    public string[] Arguments { get; set; } = Array.Empty<string>();
}

/// <summary>Only manifest-owned files are replaced. Unrelated files and the user profile are preserved.</summary>
internal static class UpdateInstaller
{
    internal static async Task<bool> ApplyAsync(UpdatePlan plan, string updatesRoot,
        Func<string, CancellationToken, Task<bool>> startAndCheck, CancellationToken token)
    {
        string source = Path.GetFullPath(plan.SourceDirectory), target = Path.GetFullPath(plan.InstallDirectory);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) || target.TrimEnd('\\').Equals(Path.GetPathRoot(target)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Güncelleme hedefi geçersiz.");
        PackageManifest.RejectLinks(source);
        PackageManifest.RejectLinks(target);
        string cacheRoot = Path.GetFullPath(updatesRoot).TrimEnd('\\') + Path.DirectorySeparatorChar;
        if (!source.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase) || target.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Güncelleme kaynağı veya hedefi beklenen klasörde değil.");
        PackageManifest next = PackageManifest.Load(source), current = PackageManifest.Load(target);
        string backup = Path.Combine(updatesRoot, "previous");
        PackageManifest.RejectLinks(backup);
        if (Directory.Exists(backup))
        {
            foreach (string item in Directory.EnumerateFileSystemEntries(backup, "*", SearchOption.AllDirectories)) PackageManifest.RejectLinks(item);
            Directory.Delete(backup, true);
        }
        Directory.CreateDirectory(backup);
        foreach (PackageFile file in current.Files) Copy(target, backup, file.Path);
        File.Copy(Path.Combine(target, PackageManifest.FileName), Path.Combine(backup, PackageManifest.FileName));
        // The backup is complete and verified before any installed file is changed.
        PackageManifest.Load(backup);
        bool applied = false;
        try
        {
            foreach (PackageFile file in next.Files) { token.ThrowIfCancellationRequested(); Copy(source, target, file.Path); }
            foreach (PackageFile old in current.Files.Where(f => !next.Files.Any(n => n.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))))
                File.Delete(PackageManifest.Inside(target, old.Path));
            File.Copy(Path.Combine(source, PackageManifest.FileName), Path.Combine(target, PackageManifest.FileName), true);
            PackageManifest.Load(target);
            applied = await startAndCheck(target, token).ConfigureAwait(false);
            return applied;
        }
        finally
        {
            if (!applied)
            {
                foreach (PackageFile file in next.Files.Where(f => !current.Files.Any(o => o.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))))
                    File.Delete(PackageManifest.Inside(target, file.Path));
                foreach (PackageFile file in current.Files) Copy(backup, target, file.Path);
                File.Copy(Path.Combine(backup, PackageManifest.FileName), Path.Combine(target, PackageManifest.FileName), true);
                PackageManifest.Load(target);
            }
        }
    }

    private static void Copy(string from, string to, string relative)
    {
        string target = PackageManifest.Inside(to, relative);
        PackageManifest.RejectLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(PackageManifest.Inside(from, relative), target, true);
    }
}
