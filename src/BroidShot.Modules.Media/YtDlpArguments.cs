using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BroidShot.Modules.Media;

public enum DownloadPreset
{
    VideoBest,
    Video1080,
    Video720,
    Video480,
    AudioMp3_192,
    AudioMp3_320,
    AudioM4a,
    Advanced
}

/// <summary>Everything needed to start one yt-dlp download.</summary>
internal sealed record DownloadRequest
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public DownloadPreset Preset { get; init; } = DownloadPreset.VideoBest;
    /// <summary>Format selector for <see cref="DownloadPreset.Advanced"/> (e.g. "137+140").</summary>
    public string? FormatId { get; init; }
    public required string OutputFolder { get; init; }
    public string FileNameTemplate { get; init; } = MediaSettings.DefaultFileNameTemplate;
    /// <summary>1-based playlist index when an entry has no direct URL; null downloads a single video (--no-playlist).</summary>
    public int? PlaylistItem { get; init; }
    /// <summary>yt-dlp browser name for --cookies-from-browser, or null.</summary>
    public string? CookiesBrowser { get; init; }
}

internal sealed record PresetOption(DownloadPreset Preset, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Builds yt-dlp argument lists (never a shell string). The URL always comes last, after "--".</summary>
internal static partial class YtDlpArguments
{
    public const string ProgressPrefix = "BSPROG";
    public const string PostProcessPrefix = "BSPP";
    public const string FilePrefix = "BSFILE";

    public const string DownloadProgressTemplate =
        "download:" + ProgressPrefix + "|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s";
    public const string PostProcessProgressTemplate =
        "postprocess:" + PostProcessPrefix + "|%(progress.status)s|%(progress.postprocessor)s";
    public const string FilePrintTemplate = "after_move:" + FilePrefix + "|%(filepath)s";

    public static IReadOnlyList<string> SupportedBrowsers { get; } = new[] { "edge", "chrome", "firefox", "brave", "opera", "vivaldi", "chromium", "whale" };

    public static IReadOnlyList<PresetOption> PresetOptions { get; } = new[]
    {
        new PresetOption(DownloadPreset.VideoBest, "Video MP4 — en iyi kalite"),
        new PresetOption(DownloadPreset.Video1080, "Video MP4 — 1080p"),
        new PresetOption(DownloadPreset.Video720, "Video MP4 — 720p"),
        new PresetOption(DownloadPreset.Video480, "Video MP4 — 480p"),
        new PresetOption(DownloadPreset.AudioMp3_192, "Yalnız ses MP3 — 192 kbps"),
        new PresetOption(DownloadPreset.AudioMp3_320, "Yalnız ses MP3 — 320 kbps"),
        new PresetOption(DownloadPreset.AudioM4a, "Yalnız ses M4A"),
        new PresetOption(DownloadPreset.Advanced, "Gelişmiş — format kimliği"),
    };

    public static bool IsSupportedBrowser(string? browser) => browser is not null && SupportedBrowsers.Contains(browser);

    /// <summary>yt-dlp format selector syntax only; must not look like an option.</summary>
    public static bool IsValidFormatSelector(string? format) =>
        !string.IsNullOrWhiteSpace(format) && format.Length <= 200 && !format.StartsWith('-') && FormatSelector().IsMatch(format);

    /// <summary>Relative output template: no rooted paths, no "..", no line breaks.</summary>
    public static bool IsValidFileNameTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 300) return false;
        if (template.Any(char.IsControl) || template.StartsWith('-')) return false;
        if (Path.IsPathRooted(template) || template.Contains("..")) return false;
        return template.Contains("%(", StringComparison.Ordinal);
    }

    private static IEnumerable<string> Common() => new[] { "--ignore-config", "--no-colors", "--encoding", "utf-8" };

    /// <summary>yt-dlp -J (single JSON document). Playlists are listed flat so large lists stay fast.</summary>
    public static IReadOnlyList<string> BuildInfo(string url, string? cookiesBrowser)
    {
        if (!UrlRules.TryNormalize(url, out string normalized)) throw new ArgumentException("Yalnız http/https bağlantıları desteklenir.", nameof(url));
        var args = new List<string>(Common()) { "--no-warnings", "-J", "--flat-playlist" };
        AddCookies(args, cookiesBrowser);
        args.Add("--");
        args.Add(normalized);
        return args;
    }

    public static IReadOnlyList<string> BuildDownload(DownloadRequest request, string ffmpegLocation)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!UrlRules.TryNormalize(request.Url, out string url)) throw new ArgumentException("Yalnız http/https bağlantıları desteklenir.", nameof(request));
        if (string.IsNullOrWhiteSpace(ffmpegLocation)) throw new ArgumentException("ffmpeg konumu gerekli.", nameof(ffmpegLocation));
        if (string.IsNullOrWhiteSpace(request.OutputFolder) || !Path.IsPathRooted(request.OutputFolder))
            throw new ArgumentException("Çıktı klasörü tam bir yol olmalı.", nameof(request));
        string template = IsValidFileNameTemplate(request.FileNameTemplate) ? request.FileNameTemplate : MediaSettings.DefaultFileNameTemplate;

        var args = new List<string>(Common())
        {
            "--newline", "--progress", "--no-mtime",
            "--progress-template", DownloadProgressTemplate,
            "--progress-template", PostProcessProgressTemplate,
            "--print", FilePrintTemplate,
            "--ffmpeg-location", ffmpegLocation,
            "-P", request.OutputFolder,
            "-o", template,
        };
        if (request.PlaylistItem is { } item)
        {
            if (item < 1) throw new ArgumentOutOfRangeException(nameof(request), "Liste sırası 1 veya büyük olmalı.");
            args.Add("--playlist-items");
            args.Add(item.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else args.Add("--no-playlist");

        args.AddRange(PresetArguments(request.Preset, request.FormatId));
        AddCookies(args, request.CookiesBrowser);
        args.Add("--");
        args.Add(url);
        return args;
    }

    public static IReadOnlyList<string> PresetArguments(DownloadPreset preset, string? formatId = null) => preset switch
    {
        DownloadPreset.VideoBest => Video("res,ext:mp4:m4a"),
        DownloadPreset.Video1080 => Video("res:1080,ext:mp4:m4a"),
        DownloadPreset.Video720 => Video("res:720,ext:mp4:m4a"),
        DownloadPreset.Video480 => Video("res:480,ext:mp4:m4a"),
        DownloadPreset.AudioMp3_192 => new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "192K", "--embed-metadata" },
        DownloadPreset.AudioMp3_320 => new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "320K", "--embed-metadata" },
        DownloadPreset.AudioM4a => new[] { "-f", "ba[ext=m4a]/ba/b", "-x", "--audio-format", "m4a", "--embed-metadata" },
        DownloadPreset.Advanced => IsValidFormatSelector(formatId)
            ? new[] { "-f", formatId!.Trim() }
            : throw new ArgumentException("Geçerli bir format kimliği gir (ör. 137+140 veya bv*+ba).", nameof(formatId)),
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };

    private static string[] Video(string sort) =>
        new[] { "-f", "bv*+ba/b", "-S", sort, "--merge-output-format", "mp4", "--remux-video", "mp4", "--embed-metadata" };

    private static void AddCookies(List<string> args, string? browser)
    {
        if (browser is null) return;
        if (!IsSupportedBrowser(browser)) throw new ArgumentException("Desteklenmeyen tarayıcı: " + browser, nameof(browser));
        args.Add("--cookies-from-browser");
        args.Add(browser);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-+/\[\]<>=!*.:,?^$~|() ]+$")]
    private static partial Regex FormatSelector();
}
