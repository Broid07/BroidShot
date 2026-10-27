using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BroidShot.Core;

public enum ExternalToolPackageKind
{
    /// <summary>The asset is the executable itself (e.g. yt-dlp.exe).</summary>
    Executable,
    /// <summary>The asset is a zip archive that is extracted into the install folder.</summary>
    Zip
}

/// <summary>A concrete downloadable build of a tool, produced by a definition's resolver.</summary>
public sealed class ExternalToolRelease
{
    /// <summary>Informational version, e.g. "2026.10.26" or "n9.0 · 2026-10-26".</summary>
    public required string Version { get; init; }
    /// <summary>HTTPS download URL.</summary>
    public required Uri DownloadUrl { get; init; }
    /// <summary>File name as listed in the checksum file (e.g. "yt-dlp.exe"); no path separators.</summary>
    public required string AssetFileName { get; init; }
    /// <summary>Expected SHA-256 (hex). Either this or <see cref="ChecksumFileUrl"/> is required.</summary>
    public string? Sha256 { get; init; }
    /// <summary>HTTPS URL of a sha256sum-style file ("&lt;hex&gt;  &lt;file name&gt;" per line).</summary>
    public Uri? ChecksumFileUrl { get; init; }
    public ExternalToolPackageKind Kind { get; init; } = ExternalToolPackageKind.Executable;
}

/// <summary>Describes how to obtain and locate one external tool. Modules may create their own.</summary>
public sealed class ExternalToolDefinition
{
    /// <param name="name">Folder name under &lt;DataRoot&gt;/tools (lowercase id, e.g. "yt-dlp").</param>
    /// <param name="executableRelativePath">Executable path inside the install folder, e.g. "yt-dlp.exe" or "bin/ffmpeg.exe".</param>
    /// <param name="resolveLatest">Finds the newest release (network allowed; use the given HttpClient).</param>
    public ExternalToolDefinition(string name, string executableRelativePath, Func<HttpClient, CancellationToken, Task<ExternalToolRelease>> resolveLatest)
    {
        Name = DataPaths.ValidateId(name, nameof(name));
        if (string.IsNullOrWhiteSpace(executableRelativePath) || System.IO.Path.IsPathRooted(executableRelativePath) || executableRelativePath.Contains(".."))
            throw new ArgumentException("Çalıştırılabilir dosya yolu kurulum klasörüne göreli olmalı.", nameof(executableRelativePath));
        ExecutableRelativePath = executableRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);
        ResolveLatest = resolveLatest ?? throw new ArgumentNullException(nameof(resolveLatest));
    }

    public string Name { get; }
    public string ExecutableRelativePath { get; }
    public Func<HttpClient, CancellationToken, Task<ExternalToolRelease>> ResolveLatest { get; }
    /// <summary>Turkish display name for progress texts.</summary>
    public string DisplayName { get; init; } = "";
    /// <summary>For zips whose entries share one top folder (ffmpeg builds), drop that folder.</summary>
    public bool StripSingleRootFolder { get; init; } = true;
    /// <summary>Downloads larger than this are rejected.</summary>
    public long MaxDownloadBytes { get; init; } = 512L * 1024 * 1024;
    /// <summary>How often <see cref="IExternalToolManager.EnsureUpToDateAsync"/> checks for a newer build.</summary>
    public TimeSpan UpdateInterval { get; init; } = TimeSpan.FromDays(7);

    internal string Label => DisplayName.Length > 0 ? DisplayName : Name;
}

public enum ExternalToolStage { Resolving, Downloading, Verifying, Extracting, Completed }

public sealed record ExternalToolProgress(string ToolName, ExternalToolStage Stage, long BytesReceived, long? TotalBytes,
    double? BytesPerSecond = null, TimeSpan? EstimatedRemaining = null, bool IsResuming = false)
{
    /// <summary>0..1 while downloading with a known size, otherwise null.</summary>
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;

    public string Describe() => Stage switch
    {
        ExternalToolStage.Resolving => ToolName + ": sürüm bilgisi alınıyor…",
        ExternalToolStage.Downloading => ToolName + (IsResuming ? ": kaldığı yerden indiriliyor" : ": indiriliyor") + (Fraction is { } f ? $" %{f * 100:0}" : "…"),
        ExternalToolStage.Verifying => ToolName + ": SHA-256 doğrulanıyor…",
        ExternalToolStage.Extracting => ToolName + ": açılıyor…",
        _ => ToolName + " hazır."
    };
}

public sealed record ExternalToolStatus(string Name, bool IsInstalled, string? Version, string? Sha256, string? ExecutablePath,
    DateTimeOffset? InstalledAt, DateTimeOffset? LastUpdateCheck);

public sealed record ExternalToolUpdateInfo(string Name, string? InstalledVersion, string LatestVersion, bool UpdateAvailable, ExternalToolRelease Release, string LatestSha256);

/// <summary>Download, verification or installation failure. The message is Turkish and user-presentable.</summary>
public sealed class ExternalToolException : Exception
{
    public ExternalToolException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Installs external tools under &lt;DataRoot&gt;/tools/&lt;name&gt;/ with mandatory HTTPS and SHA-256 verification.</summary>
public interface IExternalToolManager
{
    string ToolsRoot { get; }

    ExternalToolStatus GetStatus(ExternalToolDefinition tool);

    /// <summary>Installed executable path, or null (no network).</summary>
    string? TryGetExecutable(ExternalToolDefinition tool);

    /// <summary>&lt;ToolsRoot&gt;/&lt;name&gt;/current — e.g. to find ffprobe.exe next to ffmpeg.exe.</summary>
    string GetInstallDirectory(ExternalToolDefinition tool);

    /// <summary>Returns the executable, downloading the latest verified release on first use.</summary>
    Task<string> EnsureAsync(ExternalToolDefinition tool, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like EnsureAsync, and when the last check is older than the definition's UpdateInterval also checks
    /// for and installs an update. Update failures are logged; the installed version is still returned.
    /// </summary>
    Task<string> EnsureUpToDateAsync(ExternalToolDefinition tool, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Resolves the latest release and compares its SHA-256 with the installed one.</summary>
    Task<ExternalToolUpdateInfo> CheckForUpdateAsync(ExternalToolDefinition tool, CancellationToken cancellationToken = default);

    /// <summary>Downloads, verifies and atomically installs a specific release.</summary>
    Task<string> InstallAsync(ExternalToolDefinition tool, ExternalToolRelease release, IProgress<ExternalToolProgress>? progress = null, CancellationToken cancellationToken = default);
}
