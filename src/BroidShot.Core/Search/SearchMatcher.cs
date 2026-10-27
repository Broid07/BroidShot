using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BroidShot.Core;

/// <summary>
/// Turkish-aware fuzzy matching for the command palette (and any module list search).
/// "donustur" matches "Dönüştür", "pdf png" matches "PDF'i PNG'ye çevir", "ekal" matches "Ekran alıntısı".
/// </summary>
public static class SearchMatcher
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Lowercases with Turkish rules, folds ç ğ ı ö ş ü and other diacritics, keeps letters/digits/spaces.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string lower = text.ToLower(Turkish).Replace('ı', 'i');
        string decomposed = lower.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool space = false;
        foreach (char c in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) { builder.Append(c); space = false; }
            else if (!space && builder.Length > 0) { builder.Append(' '); space = true; }
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Score &gt; 0 when every query word matches the title, a keyword or the description. Higher is better.
    /// An empty query matches everything with score 1.
    /// </summary>
    public static int Score(string query, string title, IEnumerable<string>? keywords = null, string? description = null)
    {
        string[] tokens = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return 1;
        string normalizedTitle = Normalize(title);
        string[] titleWords = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] keywordList = (keywords ?? Array.Empty<string>()).Select(Normalize).Where(k => k.Length > 0).ToArray();
        string[] keywordWords = keywordList.SelectMany(k => k.Split(' ')).ToArray();
        string normalizedDescription = Normalize(description);
        int total = 0;
        foreach (string token in tokens)
        {
            int best = 0;
            if (normalizedTitle.StartsWith(token, StringComparison.Ordinal)) best = 100;
            else if (titleWords.Any(w => w.StartsWith(token, StringComparison.Ordinal))) best = 80;
            else if (normalizedTitle.Contains(token, StringComparison.Ordinal)) best = 60;
            else if (keywordList.Any(k => k.StartsWith(token, StringComparison.Ordinal)) || keywordWords.Any(w => w.StartsWith(token, StringComparison.Ordinal))) best = 50;
            else if (keywordList.Any(k => k.Contains(token, StringComparison.Ordinal))) best = 35;
            else if (normalizedDescription.Contains(token, StringComparison.Ordinal)) best = 20;
            else if (token.Length >= 2 && Subsequence(token, normalizedTitle) is var gaps and >= 0) best = Math.Max(5, 30 - gaps * 3);
            if (best == 0) return 0;
            total += best;
        }
        // Prefer shorter, closer titles among equal matches.
        return total * 10 - Math.Min(normalizedTitle.Length, 60) / 6;
    }

    /// <summary>Returns the number of skipped characters if <paramref name="token"/> is a subsequence, otherwise -1.</summary>
    private static int Subsequence(string token, string text)
    {
        int index = 0, gaps = 0, start = -1;
        foreach (char c in token)
        {
            int found = text.IndexOf(c, index);
            if (found < 0) return -1;
            if (start < 0) start = found;
            else gaps += found - index;
            index = found + 1;
        }
        return gaps;
    }
}
