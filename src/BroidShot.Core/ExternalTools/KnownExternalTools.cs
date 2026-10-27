using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BroidShot.Core;

/// <summary>
/// Default definitions for official builds. Exact asset names are discovered through the GitHub API
/// at run time; the "latest/download" URLs are only a fallback when the API is unavailable.
/// Modules may build their own <see cref="ExternalToolDefinition"/> instead.
/// </summary>
public static partial class KnownExternalTools
{
    public const string YtDlpOwner = "yt-dlp";
    public const string YtDlpRepository = "yt-dlp";
    public const string YtDlpAsset = "yt-dlp.exe";
    public const string YtDlpChecksumAsset = "SHA2-256SUMS";
    public const string YtDlpLatestExeUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string YtDlpLatestChecksumUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";

    public const string FFmpegOwner = "BtbN";
    public const string FFmpegRepository = "FFmpeg-Builds";
    public const string FFmpegChecksumAsset = "checksums.sha256";
    public const string FFmpegLatestDownloadBase = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/";

    /// <summary>yt-dlp.exe (single file) from github.com/yt-dlp/yt-dlp, verified with SHA2-256SUMS.</summary>
    public static ExternalToolDefinition YtDlp { get; } = new("yt-dlp", YtDlpAsset, ResolveYtDlpAsync)
    {
        DisplayName = "yt-dlp",
        StripSingleRootFolder = false,
        MaxDownloadBytes = 128L * 1024 * 1024,
        UpdateInterval = TimeSpan.FromDays(3)
    };

    /// <summary>ffmpeg/ffprobe win64 GPL static build from BtbN/FFmpeg-Builds (newest release branch), verified with checksums.sha256.</summary>
    public static ExternalToolDefinition FFmpeg { get; } = CreateFFmpeg(lgpl: false);

    /// <summary>GPL builds include x264/x265; LGPL builds do not. Installed as "ffmpeg" or "ffmpeg-lgpl".</summary>
    public static ExternalToolDefinition CreateFFmpeg(bool lgpl) =>
        new(lgpl ? "ffmpeg-lgpl" : "ffmpeg", "bin/ffmpeg.exe", (http, token) => ResolveFFmpegAsync(http, lgpl ? "lgpl" : "gpl", token))
        {
            DisplayName = "FFmpeg",
            StripSingleRootFolder = true,
            MaxDownloadBytes = 768L * 1024 * 1024,
            UpdateInterval = TimeSpan.FromDays(14)
        };

    public static async Task<ExternalToolRelease> ResolveYtDlpAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            GitHubReleases.Release release = await GitHubReleases.GetLatestAsync(http, YtDlpOwner, YtDlpRepository, cancellationToken).ConfigureAwait(false);
            GitHubReleases.Asset exe = release.Assets.FirstOrDefault(a => a.Name == YtDlpAsset)
                ?? throw new ExternalToolException("yt-dlp sürümünde " + YtDlpAsset + " bulunamadı.");
            GitHubReleases.Asset? sums = release.Assets.FirstOrDefault(a => a.Name == YtDlpChecksumAsset);
            if (sums is null && exe.Sha256Digest is null) throw new ExternalToolException("yt-dlp sürümünde özet dosyası bulunamadı.");
            return new ExternalToolRelease
            {
                Version = release.TagName,
                DownloadUrl = exe.DownloadUrl,
                AssetFileName = YtDlpAsset,
                ChecksumFileUrl = sums?.DownloadUrl,
                Sha256 = sums is null ? exe.Sha256Digest : null,
                Kind = ExternalToolPackageKind.Executable
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or ExternalToolException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            ErrorLog.Write("tools", "yt-dlp GitHub API kullanılamadı; latest/download bağlantısı deneniyor.", ex);
            return new ExternalToolRelease
            {
                Version = "latest",
                DownloadUrl = new Uri(YtDlpLatestExeUrl),
                AssetFileName = YtDlpAsset,
                ChecksumFileUrl = new Uri(YtDlpLatestChecksumUrl),
                Kind = ExternalToolPackageKind.Executable
            };
        }
    }

    public static async Task<ExternalToolRelease> ResolveFFmpegAsync(HttpClient http, string license, CancellationToken cancellationToken)
    {
        if (license is not ("gpl" or "lgpl")) throw new ArgumentOutOfRangeException(nameof(license));
        string masterAsset = $"ffmpeg-master-latest-win64-{license}.zip";
        try
        {
            GitHubReleases.Release release = await GitHubReleases.GetLatestAsync(http, FFmpegOwner, FFmpegRepository, cancellationToken).ConfigureAwait(false);
            GitHubReleases.Asset? sums = release.Assets.FirstOrDefault(a => a.Name == FFmpegChecksumAsset);
            // Prefer the newest stable release branch, e.g. "ffmpeg-n9.0-latest-win64-gpl-9.0.zip" (not "-shared").
            GitHubReleases.Asset? asset = release.Assets
                .Select(a => (Asset: a, Match: ReleaseBranchAsset().Match(a.Name)))
                .Where(x => x.Match.Success && x.Match.Groups["license"].Value == license && Version.TryParse(x.Match.Groups["version"].Value, out _))
                .OrderByDescending(x => Version.Parse(x.Match.Groups["version"].Value))
                .Select(x => x.Asset)
                .FirstOrDefault()
                ?? release.Assets.FirstOrDefault(a => a.Name == masterAsset);
            if (asset is null) throw new ExternalToolException("FFmpeg sürümünde uygun win64 " + license.ToUpperInvariant() + " arşivi bulunamadı.");
            if (sums is null && asset.Sha256Digest is null) throw new ExternalToolException("FFmpeg sürümünde özet dosyası bulunamadı.");
            return new ExternalToolRelease
            {
                Version = asset.Name.Replace(".zip", "", StringComparison.OrdinalIgnoreCase) + (asset.UpdatedAt is { } at ? " · " + at.ToString("yyyy-MM-dd") : ""),
                DownloadUrl = asset.DownloadUrl,
                AssetFileName = asset.Name,
                ChecksumFileUrl = sums?.DownloadUrl,
                Sha256 = sums is null ? asset.Sha256Digest : null,
                Kind = ExternalToolPackageKind.Zip
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or ExternalToolException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            ErrorLog.Write("tools", "FFmpeg GitHub API kullanılamadı; latest/download bağlantısı deneniyor.", ex);
            return new ExternalToolRelease
            {
                Version = "master-latest",
                DownloadUrl = new Uri(FFmpegLatestDownloadBase + masterAsset),
                AssetFileName = masterAsset,
                ChecksumFileUrl = new Uri(FFmpegLatestDownloadBase + FFmpegChecksumAsset),
                Kind = ExternalToolPackageKind.Zip
            };
        }
    }

    [GeneratedRegex(@"^ffmpeg-n(?<version>\d+(?:\.\d+)+)-latest-win64-(?<license>gpl|lgpl)-[\d.]+\.zip$")]
    private static partial Regex ReleaseBranchAsset();
}
