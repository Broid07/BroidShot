using System;
using System.Linq;

namespace BroidShot.Modules.Media;

/// <summary>URL validation (only absolute http/https links reach yt-dlp) and video-site detection for clipboard toasts.</summary>
internal static class UrlRules
{
    public const int MaxUrlLength = 4096;

    /// <summary>Accepts an absolute http/https URL with a host and no whitespace/control characters.</summary>
    public static bool TryNormalize(string? text, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        string candidate = text.Trim().Trim('"', '\'', '<', '>');
        if (candidate.Length == 0 || candidate.Length > MaxUrlLength) return false;
        if (candidate.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return false;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(uri.Host) || uri.IsFile || uri.IsUnc) return false;
        url = uri.AbsoluteUri;
        return true;
    }

    public static bool IsAcceptable(string? text) => TryNormalize(text, out _);

    /// <summary>
    /// Returns the display name of a supported video site when the URL points at something that looks
    /// like a video/post (not just the site's home page), otherwise null.
    /// </summary>
    public static string? DetectVideoSite(string? text)
    {
        if (!TryNormalize(text, out string normalized)) return null;
        var uri = new Uri(normalized);
        string host = uri.Host.ToLowerInvariant().TrimEnd('.');
        string path = uri.AbsolutePath.ToLowerInvariant();
        string query = uri.Query.ToLowerInvariant();
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (Is(host, "music.youtube.com"))
            return path.StartsWith("/watch") || path.StartsWith("/playlist") ? "YouTube Music" : null;
        if (Is(host, "youtu.be"))
            return segments.Length >= 1 ? "YouTube" : null;
        if (Is(host, "youtube.com") || Is(host, "youtube-nocookie.com"))
        {
            if (path.StartsWith("/watch") && query.Contains("v=")) return "YouTube";
            if (path.StartsWith("/playlist") && query.Contains("list=")) return "YouTube";
            if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed" or "v") return "YouTube";
            return null;
        }
        if (Is(host, "instagram.com"))
            return segments.Length >= 2 && segments.Any(s => s is "p" or "reel" or "reels" or "tv") ? "Instagram" : null;
        if (Is(host, "vm.tiktok.com") || Is(host, "vt.tiktok.com"))
            return segments.Length >= 1 ? "TikTok" : null;
        if (Is(host, "tiktok.com"))
            return segments.Contains("video") || segments.Contains("photo") ? "TikTok" : null;
        if (Is(host, "x.com") || Is(host, "twitter.com"))
            return segments.Contains("status") ? "X" : null;
        if (Is(host, "vimeo.com"))
            return segments.Any(s => s.Length > 0 && s.All(char.IsAsciiDigit)) ? "Vimeo" : null;
        if (Is(host, "clips.twitch.tv"))
            return segments.Length >= 1 ? "Twitch" : null;
        if (Is(host, "twitch.tv"))
            return segments.Contains("videos") || segments.Contains("clip") ? "Twitch" : null;
        if (Is(host, "dai.ly"))
            return segments.Length >= 1 ? "Dailymotion" : null;
        if (Is(host, "dailymotion.com"))
            return segments.Contains("video") ? "Dailymotion" : null;
        if (Is(host, "v.redd.it"))
            return segments.Length >= 1 ? "Reddit" : null;
        if (Is(host, "reddit.com"))
            return segments.Contains("comments") || (segments.Length >= 3 && segments[^2] == "s") ? "Reddit" : null;
        return null;
    }

    /// <summary>Exact host or any subdomain of it (www., m., ...).</summary>
    private static bool Is(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    /// <summary>Key used to avoid showing two toasts for the same link (fragment dropped, case-insensitive host).</summary>
    public static string DedupKey(string url)
    {
        if (!TryNormalize(url, out string normalized)) return url.Trim();
        var builder = new UriBuilder(normalized) { Fragment = "" };
        if ((builder.Scheme == "https" && builder.Port == 443) || (builder.Scheme == "http" && builder.Port == 80)) builder.Port = -1;
        string host = builder.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.StartsWith("m.", StringComparison.Ordinal)) host = host[2..];
        builder.Host = host;
        builder.Scheme = "https";
        return builder.Uri.AbsoluteUri;
    }
}

/// <summary>Remembers which links already produced a toast in this session.</summary>
internal sealed class ToastGate
{
    private readonly object _gate = new();
    private readonly System.Collections.Generic.HashSet<string> _shown = new(StringComparer.Ordinal);

    /// <summary>True the first time a (normalized) link is seen; false afterwards.</summary>
    public bool TryEnter(string url)
    {
        string key = UrlRules.DedupKey(url);
        lock (_gate) return _shown.Add(key);
    }

    public int Count { get { lock (_gate) return _shown.Count; } }
}
