using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BroidShot.Core;

namespace BroidShot.Updates;

internal sealed record AppUpdate(string Version, string Notes, ExternalToolRelease Release);

internal sealed class AppUpdateService : IDisposable
{
    internal const string Owner = "Broid07";
    internal const string Repository = "BroidShot";
    internal const string AssetName = "BroidShot-win-x64.zip";
    internal static string UpdatesRoot => Path.Combine(Core.DataPaths.Root, "updates");
    private readonly HttpClient _http;
    private readonly ExternalToolManager _packages;
    private readonly Version _current;
    private readonly string _updatesRoot;
    internal string Status { get; private set; } = "Kontrol ediliyor";

    internal AppUpdateService(HttpMessageHandler? handler = null, string? updatesRoot = null, Version? current = null)
    {
        _http = handler is null ? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.All }) : new HttpClient(handler, false);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BroidShot/0.4");
        _updatesRoot = updatesRoot ?? UpdatesRoot;
        _packages = new ExternalToolManager(Path.Combine(_updatesRoot, "packages"), handler);
        _current = current ?? typeof(App).Assembly.GetName().Version ?? new Version(0, 4, 0, 0);
    }

    internal async Task<AppUpdate?> CheckAsync(CancellationToken token)
    {
        GitHubReleases.Release? release = await GitHubReleases.FindLatestAsync(_http, Owner, Repository, token).ConfigureAwait(false);
        if (release is null) { Status = "İlk sürüm bekleniyor"; return null; }
        if (!TryVersion(release.TagName, out Version? latest)) throw new ExternalToolException("BroidShot sürüm etiketi okunamadı.");
        if (latest <= _current) { Status = "Güncel"; return null; }
        string failed = Path.Combine(_updatesRoot, "failed-version.txt");
        if (File.Exists(failed) && File.ReadAllText(failed) == release.TagName.TrimStart('v', 'V'))
        { Status = "Önceki sürüm korundu"; return null; }
        GitHubReleases.Asset asset = release.Assets.SingleOrDefault(a => a.Name == AssetName)
            ?? throw new ExternalToolException("Yeni BroidShot sürümünde Windows paketi bulunamadı.");
        string expectedPrefix = $"https://github.com/{Owner}/{Repository}/releases/download/";
        if (!asset.DownloadUrl.AbsoluteUri.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ExternalToolException("BroidShot paketi beklenen GitHub deposundan gelmiyor.");
        GitHubReleases.Asset? sums = release.Assets.SingleOrDefault(a => a.Name == AssetName + ".sha256");
        if (asset.Sha256Digest is null && (sums is null || !sums.DownloadUrl.AbsoluteUri.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)))
            throw new ExternalToolException("BroidShot güncellemesinin doğrulama özeti bulunamadı.");
        return new AppUpdate(release.TagName.TrimStart('v', 'V'), (release.Notes ?? "").Trim(), new ExternalToolRelease
        {
            Version = release.TagName.TrimStart('v', 'V'), DownloadUrl = asset.DownloadUrl, AssetFileName = AssetName,
            Kind = ExternalToolPackageKind.Zip, Sha256 = asset.Sha256Digest, ChecksumFileUrl = sums?.DownloadUrl
        });
    }

    internal async Task<string> DownloadAsync(AppUpdate update, IProgress<ExternalToolProgress> progress, CancellationToken token)
    {
        var tool = new ExternalToolDefinition("broidshot", "BroidShot.exe", (_, _) => Task.FromResult(update.Release))
        { DisplayName = "BroidShot", StripSingleRootFolder = false, MaxDownloadBytes = 1024L * 1024 * 1024 };
        if (_packages.GetStatus(tool).Version != update.Version)
            await _packages.InstallAsync(tool, update.Release, progress, token).ConfigureAwait(false);
        string folder = _packages.GetInstallDirectory(tool);
        PackageManifest package = PackageManifest.Load(folder);
        if (package.Version != update.Version) throw new ExternalToolException("Paket sürümü yayımlanan sürümle eşleşmiyor.");
        return folder;
    }

    internal static bool TryVersion(string value, out Version? version)
    {
        version = null;
        if (!System.Version.TryParse(value.TrimStart('v', 'V'), out Version? parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }

    public void Dispose() { _http.Dispose(); _packages.Dispose(); }
}
