using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BroidShot.Modules.Clipboard;

/// <summary>Detects the content type of copied text (link, e-mail, color, code, path, number, plain text).</summary>
public static partial class ClipClassifier
{
    public static ClipKind Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ClipKind.Text;
        string trimmed = text.Trim();
        bool singleLine = trimmed.IndexOfAny(new[] { '\r', '\n' }) < 0;

        if (singleLine)
        {
            if (TryGetUrl(trimmed, out _)) return ClipKind.Link;
            if (EmailRegex().IsMatch(trimmed)) return ClipKind.Email;
            if (IsColor(trimmed)) return ClipKind.Color;
            if (IsFilePath(trimmed)) return ClipKind.FilePath;
            if (IsNumber(trimmed)) return ClipKind.Number;
        }
        if (LooksLikeCode(trimmed, singleLine)) return ClipKind.Code;
        return ClipKind.Text;
    }

    /// <summary>A single absolute http(s)/ftp URL, or "www." address (normalized to https://).</summary>
    public static bool TryGetUrl(string? text, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        string candidate = text.Trim();
        if (candidate.Any(char.IsWhiteSpace)) return false;
        if (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && candidate.Length > 6) candidate = "https://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp) return false;
        if (string.IsNullOrEmpty(uri.Host) || (!uri.Host.Contains('.') && uri.Host != "localhost")) return false;
        url = candidate;
        return true;
    }

    /// <summary>YouTube, Instagram, TikTok, X/Twitter, Vimeo and Twitch clip/video links.</summary>
    public static bool IsVideoUrl(string? text)
    {
        if (!TryGetUrl(text, out string url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.StartsWith("m.", StringComparison.Ordinal)) host = host[2..];
        string path = uri.AbsolutePath.ToLowerInvariant();
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        static bool Is(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

        if (host == "youtu.be") return segments.Length >= 1;
        if (Is(host, "youtube.com") || host == "youtube-nocookie.com")
            return (segments.Length >= 1 && segments[0] == "watch" && uri.Query.Contains("v=", StringComparison.OrdinalIgnoreCase))
                || (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed" or "v")
                || (segments.Length >= 1 && segments[0] == "playlist" && uri.Query.Contains("list=", StringComparison.OrdinalIgnoreCase));
        if (Is(host, "instagram.com")) return segments.Length >= 2 && segments[0] is "reel" or "reels" or "p" or "tv";
        if (host is "vm.tiktok.com" or "vt.tiktok.com") return segments.Length >= 1;
        if (Is(host, "tiktok.com")) return segments.Contains("video") || (segments.Length >= 2 && segments[0] == "t");
        if (host is "x.com" or "twitter.com" or "mobile.twitter.com" or "fxtwitter.com" or "vxtwitter.com")
            return segments.Length >= 3 && segments[1] == "status";
        if (host == "player.vimeo.com") return segments.Length >= 2 && segments[0] == "video";
        if (Is(host, "vimeo.com")) return segments.Length >= 1 && segments.Any(s => s.All(char.IsAsciiDigit));
        if (host == "clips.twitch.tv") return segments.Length >= 1;
        if (Is(host, "twitch.tv")) return segments.Length >= 2 && (segments[1] == "clip" || segments[0] == "videos");
        return false;
    }

    public static bool IsColor(string text) => HexColorRegex().IsMatch(text) || FunctionColorRegex().IsMatch(text);

    public static bool IsFilePath(string text)
    {
        string candidate = text.Trim().Trim('"');
        if (candidate.Length < 3 || candidate.Length > 32767) return false;
        bool drive = candidate.Length >= 3 && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':' && candidate[2] is '\\' or '/';
        bool unc = candidate.StartsWith(@"\\", StringComparison.Ordinal) && candidate.Length > 4 && char.IsLetterOrDigit(candidate[2]);
        bool env = candidate.StartsWith('%') && candidate.IndexOf('%', 1) > 1 && candidate.Contains('\\');
        if (!drive && !unc && !env) return false;
        string withoutDrive = drive ? candidate[2..] : candidate;
        return withoutDrive.IndexOfAny(Path.GetInvalidPathChars()) < 0 && withoutDrive.IndexOfAny(new[] { '<', '>', '|', '?', '*' }) < 0;
    }

    public static bool IsNumber(string text)
    {
        string candidate = text.Trim();
        if (candidate.Length == 0 || candidate.Length > 40) return false;
        if (NumberRegex().IsMatch(candidate) || GroupedNumberRegex().IsMatch(candidate)) return true;
        return HexNumberRegex().IsMatch(candidate);
    }

    /// <summary>Heuristic: JSON/XML documents, or text where many lines carry code punctuation or keywords.</summary>
    public static bool LooksLikeCode(string text, bool singleLine)
    {
        string trimmed = text.Trim();
        if (trimmed.Length < 4) return false;
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            try { using JsonDocument _ = JsonDocument.Parse(trimmed); return true; }
            catch (JsonException) { }
        }
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)) return true;

        string[] lines = trimmed.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0) return false;
        int signals = 0;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (CodeLineRegex().IsMatch(line) || CodeKeywordRegex().IsMatch(line)) signals++;
        }
        if (singleLine)
            return StrongSingleLineRegex().IsMatch(trimmed);
        bool indented = lines.Count(l => l.StartsWith("    ", StringComparison.Ordinal) || l.StartsWith('\t')) >= Math.Max(1, lines.Length / 4);
        double ratio = signals / (double)lines.Length;
        return ratio >= 0.5 || (ratio >= 0.3 && indented);
    }

    [GeneratedRegex(@"^(mailto:)?[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^#([0-9a-f]{3}|[0-9a-f]{4}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase)]
    private static partial Regex HexColorRegex();

    [GeneratedRegex(@"^(rgba?|hsla?)\(\s*[\d.]+%?\s*(,|\s)\s*[\d.]+%?\s*(,|\s)\s*[\d.]+%?\s*((,|/)\s*[\d.]+%?\s*)?\)$", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionColorRegex();

    [GeneratedRegex(@"^[+\-]?(\d+([.,]\d+)?|[.,]\d+)([eE][+\-]?\d+)?%?$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^[+\-]?\d{1,3}([.,' ]\d{3})+([.,]\d+)?%?$")]
    private static partial Regex GroupedNumberRegex();

    [GeneratedRegex(@"^0x[0-9a-f]{1,16}$", RegexOptions.IgnoreCase)]
    private static partial Regex HexNumberRegex();

    [GeneratedRegex(@"([;{}]$|^[{}]|=>|->|::|\)\s*\{|^#include\b|^#!|^//|^/\*|^\*|^<[a-z][\w:-]*[\s>/].*>$|^</[a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex CodeLineRegex();

    [GeneratedRegex(@"^(public|private|protected|internal|static|class|struct|interface|enum|namespace|using|import|from|export|function|def|return|var|let|const|if\s*\(|for\s*\(|while\s*\(|switch\s*\(|else\b|elif\b|try\b|catch\b|async\b|await\b|package|fn\s|func\s|SELECT\b|INSERT\b|UPDATE\b|DELETE\b|CREATE\b)", RegexOptions.IgnoreCase)]
    private static partial Regex CodeKeywordRegex();

    [GeneratedRegex(@"(^\w[\w.]*\s*\(.*\)\s*;$|^(const|let|var)\s+\w+\s*=.+;?$|^(SELECT|INSERT INTO|UPDATE|DELETE FROM)\s.+\s(FROM|VALUES|SET|WHERE)\s|^<[a-z][\w-]*(\s[^>]*)?>.*</[a-z][\w-]*>$|=>\s*\{|^\$\(|^import\s+[\w{]|^#include\s*[<""])", RegexOptions.IgnoreCase)]
    private static partial Regex StrongSingleLineRegex();

    internal static string KindLabel(ClipKind kind) => kind switch
    {
        ClipKind.Link => "Bağlantı",
        ClipKind.Email => "E-posta",
        ClipKind.Color => "Renk",
        ClipKind.Code => "Kod",
        ClipKind.FilePath => "Dosya yolu",
        ClipKind.Number => "Sayı",
        ClipKind.Image => "Görsel",
        ClipKind.Files => "Dosyalar",
        _ => "Metin",
    };

    internal static string KindGlyph(ClipKind kind) => kind switch
    {
        ClipKind.Link => "",
        ClipKind.Email => "",
        ClipKind.Color => "",
        ClipKind.Code => "",
        ClipKind.FilePath => "",
        ClipKind.Number => "",
        ClipKind.Image => "",
        ClipKind.Files => "",
        _ => "",
    };

}
