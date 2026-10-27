using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BroidShot.Modules.Clipboard;

/// <summary>
/// Finds secrets in copied text: JWTs, API keys/tokens, private keys, "password=" pairs, card numbers (Luhn)
/// and IBANs (mod 97, TR length 26). Never logs or returns the matched text.
/// </summary>
public static partial class SensitiveDetector
{
    /// <summary>Clipboard formats that a source sets to keep content out of history and monitors.</summary>
    public const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    public const string HistoryFormat = "CanIncludeInClipboardHistory";
    public const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    private const int MaxScanLength = 64 * 1024;

    public static SensitiveReason Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return SensitiveReason.None;
        string scan = text.Length > MaxScanLength ? text[..MaxScanLength] : text;

        if (PrivateKeyRegex().IsMatch(scan)) return SensitiveReason.PrivateKey;
        if (JwtRegex().IsMatch(scan)) return SensitiveReason.Jwt;
        if (KnownTokenRegex().IsMatch(scan)) return SensitiveReason.ApiKey;
        if (PasswordPairRegex().IsMatch(scan)) return SensitiveReason.Password;
        // IBAN first: its mod-97 check is stricter than Luhn, and IBAN digits could also form a Luhn-valid run.
        if (ContainsIban(scan)) return SensitiveReason.Iban;
        if (ContainsCardNumber(scan)) return SensitiveReason.CreditCard;
        if (IsSecretLikeToken(scan.Trim())) return SensitiveReason.Secret;
        return SensitiveReason.None;
    }

    /// <summary>Mask shown instead of sensitive content. Reveals neither characters nor the exact length.</summary>
    public static string Mask(string? text) => new('•', Math.Clamp(text?.Length ?? 8, 6, 16));

    public static string ReasonLabel(SensitiveReason reason) => reason switch
    {
        SensitiveReason.SourceFlag => "Kaynak uygulama hassas işaretledi",
        SensitiveReason.Jwt => "JWT belirteci",
        SensitiveReason.ApiKey => "API anahtarı / erişim belirteci",
        SensitiveReason.PrivateKey => "Özel anahtar",
        SensitiveReason.Password => "Parola",
        SensitiveReason.CreditCard => "Kart numarası",
        SensitiveReason.Iban => "IBAN",
        SensitiveReason.Secret => "Gizli anahtara benziyor",
        _ => "",
    };

    public static bool LuhnValid(string digits)
    {
        if (digits.Length < 2 || !digits.All(char.IsAsciiDigit)) return false;
        int sum = 0;
        bool doubleIt = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (doubleIt) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }

    /// <summary>13–19 digit numbers (spaces or dashes allowed) that pass Luhn and are not all one digit.</summary>
    public static bool ContainsCardNumber(string text)
    {
        foreach (Match match in CardCandidateRegex().Matches(text))
        {
            string digits = new(match.Value.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length is < 13 or > 19) continue;
            if (digits.Distinct().Count() == 1) continue;
            if (LuhnValid(digits)) return true;
        }
        return false;
    }

    public static bool ContainsIban(string text)
    {
        foreach (Match match in IbanCandidateRegex().Matches(text))
        {
            // The candidate may run into following words ("... 26 numaralı"); try every plausible length.
            string compact = new(match.Value.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
            for (int length = Math.Min(34, compact.Length); length >= 15; length--)
                if (IsValidIban(compact[..length])) return true;
        }
        return false;
    }

    /// <summary>ISO 13616 check: country code, 2 check digits, mod 97 == 1. TR IBANs must be 26 characters.</summary>
    public static bool IsValidIban(string candidate)
    {
        string iban = new(candidate.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());
        if (iban.Length is < 15 or > 34) return false;
        if (!char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1]) || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3])) return false;
        if (!iban.All(char.IsAsciiLetterOrDigit)) return false;
        if (iban.StartsWith("TR", StringComparison.Ordinal) && iban.Length != 26) return false;
        string rearranged = iban[4..] + iban[..4];
        var numeric = new StringBuilder(rearranged.Length * 2);
        foreach (char c in rearranged)
            numeric.Append(char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
        int remainder = 0;
        foreach (char c in numeric.ToString()) remainder = (remainder * 10 + (c - '0')) % 97;
        return remainder == 1;
    }

    /// <summary>
    /// A single long token that looks generated: 32+ hex characters (except 40, the length of git commit ids),
    /// or 32+ base64/base64url characters mixing upper case, lower case and digits with high entropy.
    /// </summary>
    public static bool IsSecretLikeToken(string token)
    {
        if (token.Length < 32 || token.Length > 4096 || token.Any(char.IsWhiteSpace)) return false;
        if (token.Contains("://", StringComparison.Ordinal) || token.Contains('\\') || token.Contains('@')) return false;
        if (token.All(char.IsAsciiHexDigit)) return token.Length != 40;
        if (!Base64LikeRegex().IsMatch(token)) return false;
        string core = token.TrimEnd('=');
        bool upper = core.Any(char.IsAsciiLetterUpper), lower = core.Any(char.IsAsciiLetterLower), digit = core.Any(char.IsAsciiDigit);
        if (!(upper && lower && digit)) return false;
        return Entropy(core) >= 4.0;
    }

    private static double Entropy(string text)
    {
        double entropy = 0;
        foreach (var group in text.GroupBy(c => c))
        {
            double p = group.Count() / (double)text.Length;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    [GeneratedRegex(@"-----BEGIN ([A-Z ]+ )?PRIVATE KEY( BLOCK)?-----")]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{0,}")]
    private static partial Regex JwtRegex();

    // OpenAI/Anthropic (sk-), Stripe (sk_live_/rk_live_), GitHub (ghp_ gho_ ghu_ ghs_ ghr_ github_pat_), Slack (xoxb- xoxp- ...),
    // AWS access key ids, Google API keys, GitLab, npm and Hugging Face tokens.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(sk-[A-Za-z0-9_\-]{16,}|(sk|rk)_(live|test)_[A-Za-z0-9]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abposr]-[A-Za-z0-9\-]{10,}|(AKIA|ASIA)[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{35}|glpat-[A-Za-z0-9_\-]{20,}|npm_[A-Za-z0-9]{36}|hf_[A-Za-z0-9]{30,})")]
    private static partial Regex KnownTokenRegex();

    // No word boundary before the key so DB_PASSWORD= / x-api-key: also match; a letter right before it does not.
    [GeneratedRegex(@"(?i)(?<![a-zçğıöşü])(password|passwd|pwd|pass|secret|token|api[_\-]?key|access[_\-]?key|client[_\-]?secret|auth|şifre|sifre|parola)\b\s*[:=]\s*[""']?[^\s""']{4,}")]
    private static partial Regex PasswordPairRegex();

    [GeneratedRegex(@"(?<![\d\-])\d(?:[ \-]?\d){12,18}(?![\d\-])")]
    private static partial Regex CardCandidateRegex();

    [GeneratedRegex(@"(?i)\b[A-Z]{2}\d{2}(?:[ \-]?[A-Z0-9]){11,30}\b")]
    private static partial Regex IbanCandidateRegex();

    [GeneratedRegex(@"^[A-Za-z0-9+/_\-]+=*$")]
    private static partial Regex Base64LikeRegex();
}
