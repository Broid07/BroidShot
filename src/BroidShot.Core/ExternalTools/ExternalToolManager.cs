using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BroidShot.Core;

/// <summary>
/// Layout: &lt;ToolsRoot&gt;/&lt;name&gt;/current/… (installed files) and &lt;ToolsRoot&gt;/&lt;name&gt;/tool.json (manifest).
/// Every URL must be HTTPS (redirects included) and every download must match a SHA-256 from the release or
/// its checksum file; otherwise nothing is installed. Installation is staged and swapped in atomically.
/// </summary>
public sealed class ExternalToolManager : IExternalToolManager, IDisposable
{
    private const string CurrentFolder = "current";
    private const string ManifestFile = "tool.json";
    private const long MaxChecksumFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <param name="toolsRoot">Defaults to &lt;DataRoot&gt;/tools.</param>
    /// <param name="handler">Custom HTTP handler (tests use a fake one; it is not disposed by the manager).</param>
    public ExternalToolManager(string? toolsRoot = null, HttpMessageHandler? handler = null)
    {
        ToolsRoot = Path.GetFullPath(toolsRoot ?? DataPaths.ToolsRoot);
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = true, // .NET never follows an HTTPS → HTTP redirect; the final URI is checked again below.
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(20)
            })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BroidShot/0.3");
    }

    public string ToolsRoot { get; }

    public string GetInstallDirectory(ExternalToolDefinition tool) => Path.Combine(ToolFolder(tool), CurrentFolder);

    public ExternalToolStatus GetStatus(ExternalToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        Manifest? manifest = ReadManifest(tool);
        string? exe = TryGetExecutable(tool);
        return new ExternalToolStatus(tool.Name, exe is not null, manifest?.Version, manifest?.Sha256, exe, manifest?.InstalledAt, manifest?.LastUpdateCheck);
    }

    public string? TryGetExecutable(ExternalToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        string path = Path.Combine(GetInstallDirectory(tool), tool.ExecutableRelativePath);
        return File.Exists(path) && File.Exists(ManifestPath(tool)) ? path : null;
    }

    public async Task<string> EnsureAsync(ExternalToolDefinition tool, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (TryGetExecutable(tool) is { } existing) return existing;
        SemaphoreSlim gate = Gate(tool);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetExecutable(tool) is { } installed) return installed;
            ExternalToolRelease release = await ResolveAsync(tool, progress, cancellationToken).ConfigureAwait(false);
            return await InstallCoreAsync(tool, release, null, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<string> EnsureUpToDateAsync(ExternalToolDefinition tool, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string exe = await EnsureAsync(tool, progress, cancellationToken).ConfigureAwait(false);
        Manifest? manifest = ReadManifest(tool);
        DateTimeOffset lastCheck = manifest?.LastUpdateCheck ?? manifest?.InstalledAt ?? DateTimeOffset.MinValue;
        if (DateTimeOffset.UtcNow - lastCheck < tool.UpdateInterval) return exe;
        SemaphoreSlim gate = Gate(tool);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ExternalToolUpdateInfo info = await CheckCoreAsync(tool, progress, cancellationToken).ConfigureAwait(false);
            if (info.UpdateAvailable) return await InstallCoreAsync(tool, info.Release, info.LatestSha256, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ErrorLog.Write("tools", tool.Label + " güncellemesi yapılamadı; kurulu sürüm kullanılıyor.", ex);
        }
        finally { gate.Release(); }
        return TryGetExecutable(tool) ?? exe;
    }

    public async Task<ExternalToolUpdateInfo> CheckForUpdateAsync(ExternalToolDefinition tool, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        SemaphoreSlim gate = Gate(tool);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CheckCoreAsync(tool, null, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<string> InstallAsync(ExternalToolDefinition tool, ExternalToolRelease release, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(release);
        SemaphoreSlim gate = Gate(tool);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await InstallCoreAsync(tool, release, null, progress, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<ExternalToolUpdateInfo> CheckCoreAsync(ExternalToolDefinition tool, IProgress<ExternalToolProgress>? progress, CancellationToken cancellationToken)
    {
        ExternalToolRelease release = await ResolveAsync(tool, progress, cancellationToken).ConfigureAwait(false);
        string latest = await ExpectedSha256Async(tool, release, cancellationToken).ConfigureAwait(false);
        Manifest? manifest = ReadManifest(tool);
        if (manifest is not null)
        {
            manifest.LastUpdateCheck = DateTimeOffset.UtcNow;
            WriteManifest(tool, manifest);
        }
        bool installed = TryGetExecutable(tool) is not null;
        bool update = !installed || !string.Equals(manifest?.Sha256, latest, StringComparison.OrdinalIgnoreCase);
        return new ExternalToolUpdateInfo(tool.Name, manifest?.Version, release.Version, update, release, latest);
    }

    private async Task<ExternalToolRelease> ResolveAsync(ExternalToolDefinition tool, IProgress<ExternalToolProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ExternalToolProgress(tool.Label, ExternalToolStage.Resolving, 0, null));
        try
        {
            return await tool.ResolveLatest(_http, cancellationToken).ConfigureAwait(false)
                ?? throw new ExternalToolException(tool.Label + " için sürüm bilgisi bulunamadı.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ExternalToolException(tool.Label + " sürüm bilgisi alınamadı. İnternet bağlantını kontrol et.", ex);
        }
    }

    private async Task<string> InstallCoreAsync(ExternalToolDefinition tool, ExternalToolRelease release, string? knownSha256,
        IProgress<ExternalToolProgress>? progress, CancellationToken cancellationToken)
    {
        RequireHttps(release.DownloadUrl, tool);
        if (string.IsNullOrWhiteSpace(release.AssetFileName) || release.AssetFileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            throw new ExternalToolException(tool.Label + " için geçersiz dosya adı.");
        string folder = ToolFolder(tool);
        Directory.CreateDirectory(folder);
        CleanupLeftovers(folder);

        string expected = knownSha256 ?? await ExpectedSha256Async(tool, release, cancellationToken).ConfigureAwait(false);
        string token = Guid.NewGuid().ToString("N");
        string download = Path.Combine(folder, ".download-" + expected.ToLowerInvariant() + ".part");
        string staging = Path.Combine(folder, ".staging-" + token);
        bool keepPartial = false;
        bool downloaded = false;
        foreach (string oldPartial in Directory.EnumerateFiles(folder, ".download-*.part"))
            if (!string.Equals(oldPartial, download, StringComparison.OrdinalIgnoreCase)) TryDeleteFile(oldPartial);
        try
        {
            string actual = await ResumableDownload.FetchAsync(_http, release.DownloadUrl, download, tool.MaxDownloadBytes, tool.Label, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            downloaded = true;
            progress?.Report(new ExternalToolProgress(tool.Label, ExternalToolStage.Verifying, 0, null));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new ExternalToolException($"{tool.Label} indirmesi SHA-256 doğrulamasından geçmedi; dosya kurulmadı.");

            Directory.CreateDirectory(staging);
            if (release.Kind == ExternalToolPackageKind.Zip)
            {
                progress?.Report(new ExternalToolProgress(tool.Label, ExternalToolStage.Extracting, 0, null));
                ExtractZip(download, staging, tool.StripSingleRootFolder, tool.MaxDownloadBytes * 4, tool.Label);
            }
            else
            {
                string target = Path.Combine(staging, tool.ExecutableRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(download, target);
            }
            if (!File.Exists(Path.Combine(staging, tool.ExecutableRelativePath)))
                throw new ExternalToolException($"{tool.Label} paketinde {tool.ExecutableRelativePath} bulunamadı.");

            string current = Path.Combine(folder, CurrentFolder);
            string old = Path.Combine(folder, ".old-" + token);
            try
            {
                if (Directory.Exists(current)) Directory.Move(current, old);
                Directory.Move(staging, current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!Directory.Exists(current) && Directory.Exists(old)) Directory.Move(old, current);
                throw new ExternalToolException($"{tool.Label} şu anda kullanımda olduğu için güncellenemedi. İşlemler bitince tekrar dene.", ex);
            }
            TryDeleteDirectory(old);
            var now = DateTimeOffset.UtcNow;
            WriteManifest(tool, new Manifest
            {
                Version = release.Version,
                Sha256 = expected.ToLowerInvariant(),
                AssetFileName = release.AssetFileName,
                DownloadUrl = release.DownloadUrl.AbsoluteUri,
                InstalledAt = now,
                LastUpdateCheck = now
            });
            progress?.Report(new ExternalToolProgress(tool.Label, ExternalToolStage.Completed, 0, null));
            return Path.Combine(current, tool.ExecutableRelativePath);
        }
        catch (InvalidDataException ex)
        {
            throw new ExternalToolException(tool.Label + " arşivi açılamadı.", ex);
        }
        catch (HttpRequestException ex)
        {
            keepPartial = true;
            throw new ExternalToolException(tool.Label + " indirilemedi. İnternet bağlantını kontrol et.", ex);
        }
        catch (OperationCanceledException)
        {
            keepPartial = true;
            throw;
        }
        catch (IOException ex) when (!downloaded)
        {
            keepPartial = true;
            throw new ExternalToolException(tool.Label + " indirmesi kesildi; yeniden denediğinde devam edecek.", ex);
        }
        finally
        {
            if (!keepPartial) TryDeleteFile(download);
            TryDeleteDirectory(staging);
        }
    }

    private async Task<string> ExpectedSha256Async(ExternalToolDefinition tool, ExternalToolRelease release, CancellationToken cancellationToken)
    {
        if (release.Sha256 is { } given)
        {
            if (!ChecksumFile.IsSha256(given)) throw new ExternalToolException(tool.Label + " için geçersiz SHA-256 değeri.");
            return given.ToLowerInvariant();
        }
        if (release.ChecksumFileUrl is null) throw new ExternalToolException(tool.Label + " için doğrulama özeti yok; indirme yapılmadı.");
        RequireHttps(release.ChecksumFileUrl, tool);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(release.ChecksumFileUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            RequireHttps(response.RequestMessage?.RequestUri, tool);
            if (!response.IsSuccessStatusCode)
                throw new ExternalToolException($"{tool.Label} özet dosyası alınamadı (HTTP {(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength > MaxChecksumFileBytes) throw new ExternalToolException(tool.Label + " özet dosyası beklenenden büyük.");
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaxChecksumFileBytes) throw new ExternalToolException(tool.Label + " özet dosyası beklenenden büyük.");
            return ChecksumFile.FindSha256(System.Text.Encoding.UTF8.GetString(bytes), release.AssetFileName)
                ?? throw new ExternalToolException($"{tool.Label} özet dosyasında {release.AssetFileName} bulunamadı; indirme yapılmadı.");
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalToolException(tool.Label + " özet dosyası indirilemedi. İnternet bağlantını kontrol et.", ex);
        }
    }

    /// <summary>Extracts with path-traversal protection and an uncompressed size limit.</summary>
    internal static void ExtractZip(string zipPath, string destination, bool stripSingleRoot, long maxUncompressedBytes, string label)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Sum(e => e.Length) > maxUncompressedBytes) throw new ExternalToolException(label + " arşivi beklenenden büyük.");
        string? root = stripSingleRoot ? SingleRoot(archive) : null;
        string destinationFull = Path.GetFullPath(destination);
        string prefix = destinationFull.EndsWith(Path.DirectorySeparatorChar) ? destinationFull : destinationFull + Path.DirectorySeparatorChar;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = entry.FullName.Replace('\\', '/');
            if (root is not null) relative = relative.Length > root.Length ? relative[(root.Length + 1)..] : "";
            if (relative.Length == 0) continue;
            string target = Path.GetFullPath(Path.Combine(destinationFull, relative));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ExternalToolException(label + " arşivi güvenli olmayan bir dosya yolu içeriyor; kurulmadı.");
            if (relative.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static string? SingleRoot(ZipArchive archive)
    {
        string? root = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            int slash = name.IndexOf('/');
            if (slash <= 0) return null; // a file at the archive root
            string first = name[..slash];
            if (root is null) root = first;
            else if (!string.Equals(root, first, StringComparison.Ordinal)) return null;
        }
        return root;
    }

    private static void RequireHttps(Uri? uri, ExternalToolDefinition tool)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            throw new ExternalToolException(tool.Label + " yalnız HTTPS üzerinden indirilebilir; güvenli olmayan bağlantı reddedildi.");
    }

    private string ToolFolder(ExternalToolDefinition tool) => Path.Combine(ToolsRoot, tool.Name);
    private string ManifestPath(ExternalToolDefinition tool) => Path.Combine(ToolFolder(tool), ManifestFile);
    private SemaphoreSlim Gate(ExternalToolDefinition tool) => _locks.GetOrAdd(tool.Name, _ => new SemaphoreSlim(1, 1));

    private Manifest? ReadManifest(ExternalToolDefinition tool)
    {
        try
        {
            string path = ManifestPath(tool);
            return File.Exists(path) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private void WriteManifest(ExternalToolDefinition tool, Manifest manifest)
    {
        string path = ManifestPath(tool);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(path + ".tmp", path, true);
    }

    private static void CleanupLeftovers(string folder)
    {
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(folder).Where(d => Path.GetFileName(d).StartsWith('.')).ToArray())
                TryDeleteDirectory(directory);
            foreach (string file in Directory.EnumerateFiles(folder, ".download-*.tmp").ToArray())
                TryDeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();

    private sealed class Manifest
    {
        public string Version { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string AssetFileName { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public DateTimeOffset InstalledAt { get; set; }
        public DateTimeOffset? LastUpdateCheck { get; set; }
    }
}
