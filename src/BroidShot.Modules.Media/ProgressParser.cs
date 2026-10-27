using System;
using System.Globalization;

namespace BroidShot.Modules.Media;

/// <summary>One machine-readable progress line from yt-dlp (see <see cref="YtDlpArguments.DownloadProgressTemplate"/>).</summary>
internal sealed record DownloadProgress(string Status, long? DownloadedBytes, long? TotalBytes, double? Speed, int? EtaSeconds, string? PostProcessor = null)
{
    public bool IsPostProcessing => PostProcessor is not null;

    /// <summary>0..1 when the size is known, otherwise null.</summary>
    public double? Fraction => Status == "finished" && !IsPostProcessing ? 1
        : DownloadedBytes is { } done && TotalBytes is > 0 ? Math.Clamp((double)done / TotalBytes.Value, 0, 1) : null;

    public string SpeedText => Speed is > 0 ? ProgressParser.FormatBytes((long)Speed.Value) + "/sn" : "";
    public string EtaText => EtaSeconds is >= 0 ? "kalan " + ProgressParser.FormatDuration(EtaSeconds.Value) : "";

    public string Describe()
    {
        if (IsPostProcessing) return PostProcessorText(PostProcessor!);
        if (Status == "finished") return "İndirildi, işleniyor…";
        string sizes = DownloadedBytes is { } d
            ? ProgressParser.FormatBytes(d) + (TotalBytes is > 0 ? " / " + ProgressParser.FormatBytes(TotalBytes.Value) : "")
            : "";
        string percent = Fraction is { } f ? $"%{f * 100:0.0}" : "İndiriliyor";
        return sizes.Length > 0 ? percent + " · " + sizes : percent;
    }

    private static string PostProcessorText(string name) => name switch
    {
        "Merger" => "Video ve ses birleştiriliyor…",
        "ExtractAudio" => "Ses dönüştürülüyor…",
        "VideoRemuxer" or "VideoConvertor" => "MP4'e dönüştürülüyor…",
        "FFmpegMetadata" or "Metadata" => "Bilgiler ekleniyor…",
        "MoveFiles" => "Dosya taşınıyor…",
        _ => "İşleniyor (" + name + ")…"
    };
}

internal static class ProgressParser
{
    /// <summary>Parses "BSPROG|status|downloaded|total|estimate|speed|eta" or "BSPP|status|postprocessor". "NA"/"None" = unknown.</summary>
    public static bool TryParse(string? line, out DownloadProgress progress)
    {
        progress = null!;
        if (string.IsNullOrEmpty(line)) return false;
        string[] parts = line.Trim().Split('|');
        if (parts[0] == YtDlpArguments.ProgressPrefix && parts.Length >= 7)
        {
            long? total = Long(parts[3]) ?? Long(parts[4]);
            progress = new DownloadProgress(parts[1].Trim(), Long(parts[2]), total, Double(parts[5]), Int(parts[6]));
            return true;
        }
        if (parts[0] == YtDlpArguments.PostProcessPrefix && parts.Length >= 3)
        {
            progress = new DownloadProgress(parts[1].Trim(), null, null, null, null, parts[2].Trim());
            return true;
        }
        return false;
    }

    /// <summary>The final file path printed by "--print after_move:BSFILE|%(filepath)s".</summary>
    public static bool TryParseFile(string? line, out string path)
    {
        path = "";
        const string prefix = YtDlpArguments.FilePrefix + "|";
        if (line is null || !line.StartsWith(prefix, StringComparison.Ordinal)) return false;
        path = line[prefix.Length..].Trim();
        return path.Length > 0 && path != "NA";
    }

    /// <summary>"ERROR: [youtube] xyz: Video unavailable" → "[youtube] xyz: Video unavailable".</summary>
    public static bool TryParseError(string? line, out string message)
    {
        message = "";
        if (line is null) return false;
        string trimmed = line.Trim();
        if (!trimmed.StartsWith("ERROR:", StringComparison.Ordinal)) return false;
        message = trimmed[6..].Trim();
        return message.Length > 0;
    }

    private static long? Long(string text)
    {
        text = text.Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)) return value;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)) return (long)d;
        return null;
    }

    private static double? Double(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;

    private static int? Int(string text) => Long(text) is { } value and >= int.MinValue and <= int.MaxValue ? (int)value : null;

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.0", Turkish) + " " + units[unit];
    }

    public static string FormatDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "";
        var span = TimeSpan.FromSeconds(Math.Round(seconds));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
    }
}
