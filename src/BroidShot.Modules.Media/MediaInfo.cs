using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;

namespace BroidShot.Modules.Media;

internal sealed record MediaFormat(string FormatId, string Ext, int? Height, double? Fps, string? VideoCodec, string? AudioCodec, long? FileSize, string? Note, double? AudioBitrate)
{
    public bool HasVideo => VideoCodec is { Length: > 0 } v && v != "none";
    public bool HasAudio => AudioCodec is { Length: > 0 } a && a != "none";

    public string Label
    {
        get
        {
            var parts = new List<string> { FormatId, Ext };
            if (HasVideo) parts.Add(Height is { } h ? h + "p" + (Fps is > 30 ? ((int)Fps.Value).ToString() : "") : "video");
            if (!HasVideo && HasAudio) parts.Add("yalnız ses" + (AudioBitrate is > 0 ? $" {AudioBitrate.Value:0}k" : ""));
            if (HasVideo && !HasAudio) parts.Add("sessiz");
            if (FileSize is > 0) parts.Add(ProgressParser.FormatBytes(FileSize.Value));
            if (!string.IsNullOrWhiteSpace(Note)) parts.Add(Note!);
            return string.Join(" · ", parts);
        }
    }

    public override string ToString() => Label;
}

/// <summary>A playlist entry (from --flat-playlist). Selection is bound to a checkbox in the page.</summary>
internal sealed class MediaEntry : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public required int Index { get; init; }
    public required string Title { get; init; }
    public string? Id { get; init; }
    public string? Url { get; init; }
    public double? Duration { get; init; }
    public string? Uploader { get; init; }

    public string DurationText => Duration is { } d ? ProgressParser.FormatDuration(d) : "";
    public string DisplayText => $"{Index}. {Title}" + (DurationText.Length > 0 ? "  ·  " + DurationText : "");

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class MediaInfo
{
    public required string SourceUrl { get; init; }
    public string? Id { get; init; }
    public required string Title { get; init; }
    public string? Uploader { get; init; }
    public double? Duration { get; init; }
    public string? Thumbnail { get; init; }
    public string? WebpageUrl { get; init; }
    public string? Extractor { get; init; }
    public bool IsPlaylist { get; init; }
    public IReadOnlyList<MediaEntry> Entries { get; init; } = Array.Empty<MediaEntry>();
    public IReadOnlyList<MediaFormat> Formats { get; init; } = Array.Empty<MediaFormat>();

    public string DurationText => Duration is { } d ? ProgressParser.FormatDuration(d) : "";

    /// <summary>Distinct video heights, highest first (e.g. 2160, 1080, 720).</summary>
    public IReadOnlyList<int> VideoHeights =>
        Formats.Where(f => f.HasVideo && f.Height is > 0).Select(f => f.Height!.Value).Distinct().OrderByDescending(h => h).ToArray();

    public bool HasAudioOnly => Formats.Any(f => f.HasAudio && !f.HasVideo);

    public string QualitiesText
    {
        get
        {
            IReadOnlyList<int> heights = VideoHeights;
            if (heights.Count == 0) return Formats.Count == 0 ? "" : HasAudioOnly ? "Yalnız ses biçimleri" : "";
            return string.Join(", ", heights.Select(h => h + "p")) + (HasAudioOnly ? " · ayrı ses" : "");
        }
    }
}

internal static class MediaInfoParser
{
    /// <summary>Parses yt-dlp -J output (a video, or a playlist when "_type" is "playlist").</summary>
    public static MediaInfo Parse(string json, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new MediaException("yt-dlp boş yanıt döndürdü.");
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new MediaException("yt-dlp yanıtı okunamadı.", ex); }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new MediaException("yt-dlp yanıtı beklenen biçimde değil.");
            bool isPlaylist = String(root, "_type") is "playlist" or "multi_video";
            var entries = new List<MediaEntry>();
            if (isPlaylist && root.TryGetProperty("entries", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement entry in list.EnumerateArray())
                {
                    index++;
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    string? url = String(entry, "webpage_url") ?? String(entry, "url");
                    if (url is not null && !UrlRules.IsAcceptable(url)) url = null;
                    entries.Add(new MediaEntry
                    {
                        Index = Int(entry, "playlist_index") ?? index,
                        Id = String(entry, "id"),
                        Title = String(entry, "title") ?? String(entry, "id") ?? $"Öğe {index}",
                        Url = url,
                        Duration = Double(entry, "duration"),
                        Uploader = String(entry, "uploader") ?? String(entry, "channel")
                    });
                }
            }

            var formats = new List<MediaFormat>();
            if (root.TryGetProperty("formats", out JsonElement fs) && fs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement f in fs.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object || String(f, "format_id") is not { } id) continue;
                    string? protocol = String(f, "protocol");
                    if (protocol is "mhtml") continue; // storyboards
                    formats.Add(new MediaFormat(id, String(f, "ext") ?? "?", Int(f, "height"), Double(f, "fps"), String(f, "vcodec"), String(f, "acodec"),
                        Long(f, "filesize") ?? Long(f, "filesize_approx"), String(f, "format_note"), Double(f, "abr")));
                }
            }

            string? thumbnail = String(root, "thumbnail");
            if (thumbnail is null && root.TryGetProperty("thumbnails", out JsonElement thumbs) && thumbs.ValueKind == JsonValueKind.Array)
                thumbnail = thumbs.EnumerateArray().Select(t => t.ValueKind == JsonValueKind.Object ? String(t, "url") : null).LastOrDefault(u => u is not null);
            if (thumbnail is not null && !UrlRules.IsAcceptable(thumbnail)) thumbnail = null;

            return new MediaInfo
            {
                SourceUrl = sourceUrl,
                Id = String(root, "id"),
                Title = String(root, "title") ?? String(root, "id") ?? "(başlıksız)",
                Uploader = String(root, "uploader") ?? String(root, "channel") ?? String(root, "uploader_id"),
                Duration = Double(root, "duration"),
                Thumbnail = thumbnail,
                WebpageUrl = String(root, "webpage_url"),
                Extractor = String(root, "extractor_key") ?? String(root, "extractor"),
                IsPlaylist = isPlaylist,
                Entries = entries,
                Formats = formats
            };
        }
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static double? Double(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : null;

    private static long? Long(JsonElement e, string name) => Double(e, name) is { } d ? (long)d : null;

    private static int? Int(JsonElement e, string name) => Double(e, name) is { } d and >= 0 and <= int.MaxValue ? (int)d : null;
}

/// <summary>A user-presentable (Turkish) media module error.</summary>
internal sealed class MediaException : Exception
{
    public MediaException(string message, Exception? inner = null) : base(message, inner) { }
}
