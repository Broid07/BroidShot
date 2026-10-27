using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BroidShot.Modules.Utilities;

/// <summary>
/// Expands {tarih}, {saat} and {pano}. The clock and the clipboard are injected so the result is testable.
/// <c>{tarih:yyyy-MM-dd}</c> and <c>{saat:HH.mm.ss}</c> accept a custom .NET format. Expansion is a single pass, so clipboard
/// text that itself contains "{tarih}" is inserted literally. Unknown or malformed placeholders are left untouched.
/// </summary>
internal sealed class SnippetExpander
{
    public const string DefaultDateFormat = "dd.MM.yyyy";
    public const string DefaultTimeFormat = "HH:mm";

    private static readonly Regex Placeholder = new(@"\{(tarih|saat|pano)(?::([^{}]*))?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly Func<DateTime> _clock;
    private readonly Func<string?> _clipboardText;

    public SnippetExpander(Func<DateTime> clock, Func<string?> clipboardText)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _clipboardText = clipboardText ?? throw new ArgumentNullException(nameof(clipboardText));
    }

    public string Expand(string? content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        DateTime now = _clock();
        string? clipboard = null;
        bool clipboardRead = false;
        return Placeholder.Replace(content, match =>
        {
            string name = match.Groups[1].Value.ToLowerInvariant();
            string? format = match.Groups[2].Success ? match.Groups[2].Value : null;
            switch (name)
            {
                case "tarih": return Format(now, format, DefaultDateFormat, match.Value);
                case "saat": return Format(now, format, DefaultTimeFormat, match.Value);
                default:
                    if (!clipboardRead) { clipboard = _clipboardText(); clipboardRead = true; }
                    return clipboard ?? "";
            }
        });
    }

    private static string Format(DateTime now, string? custom, string fallback, string original)
    {
        try
        {
            return string.IsNullOrEmpty(custom)
                ? now.ToString(fallback, CultureInfo.InvariantCulture)
                : now.ToString(custom, Turkish);
        }
        catch (FormatException)
        {
            return original;
        }
    }
}
