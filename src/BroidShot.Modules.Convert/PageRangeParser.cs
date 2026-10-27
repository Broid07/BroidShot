using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BroidShot.Modules.Convert;

/// <summary>Result of parsing a page selection such as "1-3,5". <see cref="Pages"/> are 1-based, in order of appearance, without duplicates.</summary>
public sealed class PageSelection
{
    internal PageSelection(IReadOnlyList<int> pages, IReadOnlyList<IReadOnlyList<int>> groups, string? error)
    {
        Pages = pages;
        Groups = groups;
        Error = error;
    }

    public bool Success => Error is null;
    public IReadOnlyList<int> Pages { get; }
    /// <summary>One group per comma-separated item (used by "split by ranges").</summary>
    public IReadOnlyList<IReadOnlyList<int>> Groups { get; }
    /// <summary>Turkish message when parsing failed.</summary>
    public string? Error { get; }

    internal static PageSelection Fail(string error) => new(Array.Empty<int>(), Array.Empty<IReadOnlyList<int>>(), error);
}

/// <summary>Parses page selections: "1-3,5", "2-" (page 2 to the end), empty text = every page.</summary>
public static class PageRangeParser
{
    private readonly record struct Token(int Start, int? End, bool OpenEnded, string Text);

    /// <summary>
    /// Parses <paramref name="text"/> against a document of <paramref name="pageCount"/> pages.
    /// Empty or white-space text selects every page. Separators are ',' and ';'. Never throws.
    /// </summary>
    public static PageSelection Parse(string? text, int pageCount)
    {
        if (pageCount < 1) return PageSelection.Fail("Belgede sayfa yok.");
        if (string.IsNullOrWhiteSpace(text))
        {
            var all = new List<int>(pageCount);
            for (int page = 1; page <= pageCount; page++) all.Add(page);
            return new PageSelection(all, new[] { (IReadOnlyList<int>)all }, null);
        }

        if (!TryTokenize(text, out List<Token> tokens, out string? error)) return PageSelection.Fail(error!);

        var pages = new List<int>();
        var seen = new HashSet<int>();
        var groups = new List<IReadOnlyList<int>>();
        foreach (Token token in tokens)
        {
            int end = token.OpenEnded ? pageCount : token.End ?? token.Start;
            if (token.Start > pageCount)
                return PageSelection.Fail($"Sayfa {token.Start} bulunamadı (belgede {pageCount} sayfa var).");
            if (end > pageCount)
                return PageSelection.Fail($"Sayfa {end} bulunamadı (belgede {pageCount} sayfa var).");
            var group = new List<int>();
            for (int page = token.Start; page <= end; page++)
            {
                group.Add(page);
                if (seen.Add(page)) pages.Add(page);
            }
            groups.Add(group);
        }
        return new PageSelection(pages, groups, null);
    }

    /// <summary>Checks only the syntax (no page count needed). Returns null when valid, otherwise a Turkish message.</summary>
    public static string? ValidateSyntax(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return TryTokenize(text, out _, out string? error) ? null : error;
    }

    /// <summary>Formats pages as a compact range text ("1-3,5"), mainly for file names and messages.</summary>
    public static string Format(IReadOnlyList<int> pages)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < pages.Count)
        {
            int start = pages[i], end = start;
            while (i + 1 < pages.Count && pages[i + 1] == end + 1) { end = pages[++i]; }
            if (sb.Length > 0) sb.Append(',');
            sb.Append(start.ToString(CultureInfo.InvariantCulture));
            if (end != start) sb.Append('-').Append(end.ToString(CultureInfo.InvariantCulture));
            i++;
        }
        return sb.ToString();
    }

    private static bool TryTokenize(string text, out List<Token> tokens, out string? error)
    {
        tokens = new List<Token>();
        error = null;
        string normalized = text.Replace('–', '-').Replace('—', '-');
        foreach (string raw in normalized.Split(new[] { ',', ';' }, StringSplitOptions.None))
        {
            string item = raw.Replace(" ", "").Replace("\t", "");
            if (item.Length == 0) continue;

            int dash = item.IndexOf('-');
            if (dash < 0)
            {
                if (!TryNumber(item, out int single)) { error = Invalid(raw); return false; }
                if (single < 1) { error = "Sayfa numarası 1 veya daha büyük olmalı."; return false; }
                tokens.Add(new Token(single, null, false, item));
                continue;
            }

            if (dash == 0 || item.IndexOf('-', dash + 1) >= 0) { error = Invalid(raw); return false; }
            string left = item[..dash], right = item[(dash + 1)..];
            if (!TryNumber(left, out int start)) { error = Invalid(raw); return false; }
            if (start < 1) { error = "Sayfa numarası 1 veya daha büyük olmalı."; return false; }
            if (right.Length == 0) { tokens.Add(new Token(start, null, true, item)); continue; } // "5-" = to the end
            if (!TryNumber(right, out int end)) { error = Invalid(raw); return false; }
            if (end < start) { error = $"Ters sayfa aralığı: {item}. Küçük numara önce yazılmalı."; return false; }
            tokens.Add(new Token(start, end, false, item));
        }
        if (tokens.Count == 0) { error = "Sayfa aralığı boş. Örnek: 1-3,5"; return false; }
        return true;
    }

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static string Invalid(string raw) => $"Geçersiz sayfa aralığı: “{raw.Trim()}”. Örnek: 1-3,5";
}
