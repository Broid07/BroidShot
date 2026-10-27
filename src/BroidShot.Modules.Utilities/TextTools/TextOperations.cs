using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BroidShot.Modules.Utilities;

/// <summary>Output text or a Turkish error message.</summary>
internal readonly record struct TextResult(string? Output, string? Error)
{
    public bool IsError => Error is not null;
    public static TextResult Ok(string output) => new(output, null);
    public static TextResult Fail(string error) => new(null, error);
}

/// <summary>Counts shown by "Say".</summary>
internal readonly record struct TextStats(int Words, int Characters, int CharactersWithoutSpaces, int Lines, int Bytes)
{
    public string ToDisplay() =>
        $"Kelime: {Words}{Environment.NewLine}Karakter: {Characters}{Environment.NewLine}Boşluksuz karakter: {CharactersWithoutSpaces}{Environment.NewLine}" +
        $"Satır: {Lines}{Environment.NewLine}Bayt (UTF-8): {Bytes}";
}

/// <summary>A button on the page and, optionally, a palette command that works on the clipboard text.</summary>
internal sealed record TextOperation(string Id, string Title, string Group, Func<string, TextResult> Run, bool InPalette = true, string Description = "")
{
    /// <summary>Operations that do not need input text (generators).</summary>
    public bool NeedsInput { get; init; } = true;

    /// <summary>Title in the command palette (defaults to <see cref="Title"/>).</summary>
    public string? PaletteTitle { get; init; }
}

/// <summary>All text tools as pure functions. Turkish culture is used for casing and sorting.</summary>
internal static class TextOperations
{
    public static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions ParseOptions = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 256 };

    // ---- JSON ---------------------------------------------------------------------------------------------------

    public static TextResult JsonFormat(string input) => JsonRewrite(input, indented: true);

    public static TextResult JsonMinify(string input) => JsonRewrite(input, indented: false);

    public static TextResult JsonValidate(string input)
    {
        TextResult result = JsonRewrite(input, indented: false);
        return result.IsError ? result : TextResult.Ok("Geçerli JSON.");
    }

    private static TextResult JsonRewrite(string input, bool indented)
    {
        if (string.IsNullOrWhiteSpace(input)) return TextResult.Fail("JSON metni boş.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(input, ParseOptions);
            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true
            }))
                document.RootElement.WriteTo(writer);
            return TextResult.Ok(Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch (JsonException ex)
        {
            return TextResult.Fail(DescribeJsonError(ex));
        }
    }

    /// <summary>Turkish message with the 1-based line and column of the problem when the parser reports them.</summary>
    public static string DescribeJsonError(JsonException ex)
    {
        if (ex.LineNumber is long line && ex.BytePositionInLine is long column)
            return $"Geçersiz JSON: satır {line + 1}, sütun {column + 1}. ({ShortReason(ex.Message)})";
        return "Geçersiz JSON. (" + ShortReason(ex.Message) + ")";
    }

    private static string ShortReason(string message)
    {
        int cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        if (cut < 0) cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        return (cut > 0 ? message[..cut] : message).Trim();
    }

    // ---- encodings ----------------------------------------------------------------------------------------------

    public static TextResult Base64Encode(string input) => TextResult.Ok(Convert.ToBase64String(Encoding.UTF8.GetBytes(input)));

    public static TextResult Base64Decode(string input)
    {
        // Accept whitespace, URL-safe alphabet and missing padding.
        string cleaned = new string(input.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/');
        if (cleaned.Length == 0) return TextResult.Fail("Base64 metni boş.");
        int remainder = cleaned.Length % 4;
        if (remainder == 1) return TextResult.Fail("Geçersiz Base64: uzunluk uygun değil.");
        if (remainder != 0) cleaned = cleaned.PadRight(cleaned.Length + 4 - remainder, '=');
        byte[] bytes;
        try { bytes = Convert.FromBase64String(cleaned); }
        catch (FormatException) { return TextResult.Fail("Geçersiz Base64: izin verilmeyen karakter var."); }
        try { return TextResult.Ok(StrictUtf8.GetString(bytes)); }
        catch (DecoderFallbackException) { return TextResult.Fail("Çözülen veri metin değil (ikili veri)."); }
    }

    public static TextResult UrlEncode(string input) => TextResult.Ok(Uri.EscapeDataString(input));

    public static TextResult UrlDecode(string input)
    {
        try { return TextResult.Ok(System.Net.WebUtility.UrlDecode(input) ?? ""); }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException) { return TextResult.Fail("Geçersiz URL kodlaması."); }
    }

    // ---- case ---------------------------------------------------------------------------------------------------

    /// <summary>i→İ, ı→I.</summary>
    public static TextResult Upper(string input) => TextResult.Ok(input.ToUpper(Turkish));

    /// <summary>I→ı, İ→i.</summary>
    public static TextResult Lower(string input) => TextResult.Ok(input.ToLower(Turkish));

    /// <summary>Each word starts upper case, the rest lower case, with Turkish i/ı rules. Apostrophes stay inside the word ("ali'nin" → "Ali'nin").</summary>
    public static TextResult Title(string input)
    {
        var builder = new StringBuilder(input.Length);
        bool startOfWord = true;
        foreach (char c in input)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(startOfWord ? char.ToUpper(c, Turkish) : char.ToLower(c, Turkish));
                startOfWord = false;
            }
            else
            {
                builder.Append(c);
                if (c is not ('\'' or '’')) startOfWord = true;
            }
        }
        return TextResult.Ok(builder.ToString());
    }

    // ---- lines --------------------------------------------------------------------------------------------------

    private static (string[] Lines, string Newline) SplitLines(string input)
    {
        string newline = input.Contains("\r\n") ? "\r\n" : input.Contains('\n') ? "\n" : Environment.NewLine;
        return (input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'), newline);
    }

    public static TextResult TrimLines(string input)
    {
        var (lines, newline) = SplitLines(input);
        return TextResult.Ok(string.Join(newline, lines.Select(l => l.Trim())));
    }

    public static TextResult RemoveEmptyLines(string input)
    {
        var (lines, newline) = SplitLines(input);
        return TextResult.Ok(string.Join(newline, lines.Where(l => l.Trim().Length > 0)));
    }

    /// <summary>Keeps the first occurrence of every line (exact, case-sensitive comparison) in the original order.</summary>
    public static TextResult DedupeLines(string input)
    {
        var (lines, newline) = SplitLines(input);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return TextResult.Ok(string.Join(newline, lines.Where(seen.Add)));
    }

    /// <summary>Alphabetical order with Turkish collation (ç, ğ, ı, ö, ş, ü in their places), case-insensitive.</summary>
    public static TextResult SortLines(string input) => SortLines(input, descending: false);

    public static TextResult SortLines(string input, bool descending)
    {
        var (lines, newline) = SplitLines(input);
        StringComparer comparer = StringComparer.Create(Turkish, ignoreCase: true);
        IEnumerable<string> sorted = descending ? lines.OrderByDescending(l => l, comparer) : lines.OrderBy(l => l, comparer);
        return TextResult.Ok(string.Join(newline, sorted));
    }

    // ---- counts / hashes / generators ---------------------------------------------------------------------------

    public static TextStats Count(string input)
    {
        int words = 0;
        bool inWord = false;
        int withoutSpaces = 0;
        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c)) { inWord = false; continue; }
            withoutSpaces++;
            if (!inWord) { words++; inWord = true; }
        }
        int characters = new StringInfo(input).LengthInTextElements;
        int lines = input.Length == 0 ? 0 : input.Replace("\r\n", "\n").Split('\n').Length;
        return new TextStats(words, characters, withoutSpaces, lines, Encoding.UTF8.GetByteCount(input));
    }

    public static TextResult CountResult(string input) => TextResult.Ok(Count(input).ToDisplay());

    public static string Hash(string algorithm, string input)
    {
        byte[] data = Encoding.UTF8.GetBytes(input);
        byte[] hash = algorithm switch
        {
            "MD5" => MD5.HashData(data),
            "SHA1" => SHA1.HashData(data),
            "SHA256" => SHA256.HashData(data),
            _ => throw new ArgumentException("Bilinmeyen özet algoritması.", nameof(algorithm))
        };
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>One UUID, or N (1–100) when the input is just that number.</summary>
    public static TextResult Uuids(string input)
    {
        int count = int.TryParse(input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 100 ? n : 1;
        return TextResult.Ok(string.Join(Environment.NewLine, Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString())));
    }

    // ---- timestamps ---------------------------------------------------------------------------------------------

    /// <summary>10-digit seconds or 13-digit milliseconds (also shorter/longer values: up to 11 digits are seconds) → UTC and local time.</summary>
    public static TextResult TimestampToDate(string input, TimeZoneInfo? zone = null)
    {
        string text = input.Trim();
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
            return TextResult.Fail("Zaman damgası bir sayı olmalı (saniye veya milisaniye).");
        bool milliseconds = text.TrimStart('-', '+').Length > 11;
        try
        {
            DateTimeOffset utc = milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
            DateTimeOffset local = TimeZoneInfo.ConvertTime(utc, zone ?? TimeZoneInfo.Local);
            return TextResult.Ok(
                $"UTC: {utc:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"Yerel: {local:yyyy-MM-dd HH:mm:ss} (UTC{(local.Offset < TimeSpan.Zero ? "-" : "+")}{local.Offset:hh\\:mm})" +
                $"{Environment.NewLine}Birim: {(milliseconds ? "milisaniye" : "saniye")}");
        }
        catch (ArgumentOutOfRangeException) { return TextResult.Fail("Zaman damgası desteklenen aralığın dışında."); }
    }

    /// <summary>Parses a date/time (ISO 8601 or Turkish formats; text without an offset is read in <paramref name="zone"/>) → Unix seconds and milliseconds.</summary>
    public static TextResult DateToTimestamp(string input, TimeZoneInfo? zone = null)
    {
        string text = input.Trim();
        if (text.Length == 0) return TextResult.Fail("Tarih boş.");
        zone ??= TimeZoneInfo.Local;
        DateTimeOffset parsed;
        bool ok = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out parsed)
                  || DateTimeOffset.TryParse(text, Turkish, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out parsed);
        if (!ok) return TextResult.Fail("Tarih anlaşılamadı. Örnek: 2024-05-31 14:30:00 veya 31.05.2024 14:30");
        bool hasOffset = text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}$");
        if (!hasOffset)
        {
            // TryParse assumed the machine's zone; reinterpret the wall-clock value in the requested zone.
            var wall = DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified);
            parsed = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        }
        return TextResult.Ok($"Saniye: {parsed.ToUnixTimeSeconds()}{Environment.NewLine}Milisaniye: {parsed.ToUnixTimeMilliseconds()}");
    }

    public static TextResult Now(string input)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return TextResult.Ok($"Saniye: {now.ToUnixTimeSeconds()}{Environment.NewLine}Milisaniye: {now.ToUnixTimeMilliseconds()}");
    }

    // ---- catalogue ----------------------------------------------------------------------------------------------

    public static IReadOnlyList<TextOperation> All { get; } = new TextOperation[]
    {
        new("json.format", "JSON biçimlendir", "JSON", JsonFormat, true, "JSON metnini girintili hale getirir"),
        new("json.minify", "JSON küçült", "JSON", JsonMinify, true, "JSON metnindeki boşlukları kaldırır"),
        new("json.validate", "JSON doğrula", "JSON", JsonValidate, true, "JSON'u denetler; hata varsa satırı gösterir"),
        new("base64.encode", "Base64 kodla", "Kodlama", Base64Encode, true, "Metni UTF-8 Base64'e çevirir"),
        new("base64.decode", "Base64 çöz", "Kodlama", Base64Decode, true, "Base64 metnini çözer"),
        new("url.encode", "URL kodla", "Kodlama", UrlEncode, true, "Özel karakterleri %XX biçimine çevirir"),
        new("url.decode", "URL çöz", "Kodlama", UrlDecode, true, "%XX dizilerini çözer"),
        new("case.upper", "BÜYÜK HARF", "Harf", Upper, true, "Türkçe kurallarla büyük harfe çevirir (i → İ)") { PaletteTitle = "Büyük harfe çevir" },
        new("case.lower", "küçük harf", "Harf", Lower, true, "Türkçe kurallarla küçük harfe çevirir (I → ı)") { PaletteTitle = "Küçük harfe çevir" },
        new("case.title", "Başlık Düzeni", "Harf", Title, true, "Her kelimenin ilk harfini büyütür") { PaletteTitle = "Başlık düzenine çevir" },
        new("lines.trim", "Satırları kırp", "Satır", TrimLines, true, "Her satırın başındaki ve sonundaki boşlukları siler"),
        new("lines.empty", "Boş satırları sil", "Satır", RemoveEmptyLines, true, "Boş satırları kaldırır"),
        new("lines.dedupe", "Yinelenen satırları sil", "Satır", DedupeLines, true, "Aynı satırların ilkini bırakır"),
        new("lines.sort", "Satırları sırala (A-Z)", "Satır", s => SortLines(s), true, "Türkçe alfabe sırasıyla sıralar"),
        new("lines.sort-desc", "Satırları sırala (Z-A)", "Satır", s => SortLines(s, true), true, "Ters alfabe sırasıyla sıralar"),
        new("count", "Say", "Sayım", CountResult, true, "Kelime, karakter, satır ve bayt sayısı") { PaletteTitle = "Kelime ve karakter say" },
        new("hash.md5", "MD5", "Özet", s => TextResult.Ok(Hash("MD5", s)), true, "MD5 özeti (güvenlik için kullanma)") { PaletteTitle = "MD5 özeti al" },
        new("hash.sha1", "SHA1", "Özet", s => TextResult.Ok(Hash("SHA1", s)), true, "SHA-1 özeti") { PaletteTitle = "SHA1 özeti al" },
        new("hash.sha256", "SHA256", "Özet", s => TextResult.Ok(Hash("SHA256", s)), true, "SHA-256 özeti") { PaletteTitle = "SHA256 özeti al" },
        new("uuid", "UUID oluştur", "Üret / zaman", Uuids, true, "Yeni UUID; girdi 1–100 arası sayıysa o kadar üretir") { NeedsInput = false },
        new("time.to-date", "Zaman damgası → tarih", "Üret / zaman", s => TimestampToDate(s), true, "Unix zaman damgasını tarihe çevirir"),
        new("time.to-stamp", "Tarih → zaman damgası", "Üret / zaman", s => DateToTimestamp(s), true, "Tarihi Unix zaman damgasına çevirir"),
        new("time.now", "Şimdiki zaman damgası", "Üret / zaman", Now, false, "Şu anki Unix zaman damgası") { NeedsInput = false },
    };
}
