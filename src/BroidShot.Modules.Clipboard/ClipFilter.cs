using System;
using System.Collections.Generic;
using System.Linq;
using BroidShot.Core;

namespace BroidShot.Modules.Clipboard;

/// <summary>Type filter chips on the page. Values above 100 are not content kinds.</summary>
public enum ClipFilterKind
{
    All = -1,
    Text = ClipKind.Text,
    Link = ClipKind.Link,
    Email = ClipKind.Email,
    Color = ClipKind.Color,
    Code = ClipKind.Code,
    FilePath = ClipKind.FilePath,
    Number = ClipKind.Number,
    Image = ClipKind.Image,
    Files = ClipKind.Files,
    Video = 101,
    Pinned = 102,
    Sensitive = 103,
}

/// <summary>Search + type + tag filtering, Turkish-aware via <see cref="SearchMatcher.Normalize"/>.</summary>
public static class ClipFilter
{
    public static IReadOnlyList<(ClipFilterKind Kind, string Label)> Chips { get; } = new[]
    {
        (ClipFilterKind.All, "Tümü"),
        (ClipFilterKind.Pinned, "Sabitlenen"),
        (ClipFilterKind.Text, "Metin"),
        (ClipFilterKind.Link, "Bağlantı"),
        (ClipFilterKind.Video, "Video"),
        (ClipFilterKind.Email, "E-posta"),
        (ClipFilterKind.Color, "Renk"),
        (ClipFilterKind.Code, "Kod"),
        (ClipFilterKind.FilePath, "Dosya yolu"),
        (ClipFilterKind.Number, "Sayı"),
        (ClipFilterKind.Image, "Görsel"),
        (ClipFilterKind.Files, "Dosyalar"),
        (ClipFilterKind.Sensitive, "Hassas"),
    };

    /// <summary>
    /// Every query word must appear in the item text, its tag names, its type label or its source app.
    /// Sensitive items are matched only on tags, type and source (never on their content).
    /// </summary>
    public static IEnumerable<ClipEntry> Apply(IEnumerable<ClipEntry> items, string? query, ClipFilterKind kind, long? tagId, IReadOnlyList<ClipTag> tags)
    {
        string[] tokens = SearchMatcher.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Dictionary<long, string> tagNames = tags.ToDictionary(t => t.Id, t => SearchMatcher.Normalize(t.Name));
        foreach (ClipEntry item in items)
        {
            if (!MatchesKind(item, kind)) continue;
            if (tagId is { } tag && !item.TagIds.Contains(tag)) continue;
            if (tokens.Length > 0 && !MatchesQuery(item, tokens, tagNames)) continue;
            yield return item;
        }
    }

    public static bool MatchesKind(ClipEntry item, ClipFilterKind kind) => kind switch
    {
        ClipFilterKind.All => true,
        ClipFilterKind.Video => item.IsVideoLink,
        ClipFilterKind.Pinned => item.Pinned,
        ClipFilterKind.Sensitive => item.Sensitive,
        _ => (int)item.Kind == (int)kind,
    };

    private static bool MatchesQuery(ClipEntry item, string[] tokens, Dictionary<long, string> tagNames)
    {
        string extra = SearchMatcher.Normalize(ClipClassifier.KindLabel(item.Kind) + " " + item.SourceApp + " " +
            string.Join(" ", item.TagIds.Select(id => tagNames.TryGetValue(id, out string? name) ? name : "")));
        foreach (string token in tokens)
        {
            bool found = (!item.Sensitive && item.SearchText.Contains(token, StringComparison.Ordinal))
                || extra.Contains(token, StringComparison.Ordinal);
            if (!found) return false;
        }
        return true;
    }

    /// <summary>Page order: pinned items first, then most recently used.</summary>
    public static IEnumerable<ClipEntry> PageOrder(IEnumerable<ClipEntry> items) =>
        items.OrderByDescending(e => e.Pinned).ThenByDescending(e => e.LastUsedAt).ThenByDescending(e => e.Id);
}
