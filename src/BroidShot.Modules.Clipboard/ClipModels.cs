using System;
using System.Collections.Generic;
using System.Linq;

namespace BroidShot.Modules.Clipboard;

/// <summary>Automatic content type of a history item.</summary>
public enum ClipKind
{
    Text = 0,
    Link = 1,
    Email = 2,
    Color = 3,
    Code = 4,
    FilePath = 5,
    Number = 6,
    Image = 7,
    Files = 8,
}

/// <summary>Why an item is treated as sensitive. Stored by name; never contains the content itself.</summary>
public enum SensitiveReason
{
    None = 0,
    SourceFlag,
    Jwt,
    ApiKey,
    PrivateKey,
    Password,
    CreditCard,
    Iban,
    Secret,
}

/// <summary>Text-like content of an item. Image bytes live in a separate encrypted file.</summary>
public sealed record ClipPayload(string? Text, string? Html, IReadOnlyList<string>? Files)
{
    public static ClipPayload Empty { get; } = new(null, null, null);

    public bool ContentEquals(ClipPayload? other) =>
        other is not null && Text == other.Text && Html == other.Html &&
        (Files ?? Array.Empty<string>()).SequenceEqual(other.Files ?? Array.Empty<string>(), StringComparer.Ordinal);
}

/// <summary>
/// What the listener read from the clipboard (or a test fed in). Exactly one of the content groups is used:
/// files &gt; image &gt; text/HTML.
/// </summary>
public sealed class ClipCapture
{
    public string? Text { get; init; }
    public string? Html { get; init; }
    public IReadOnlyList<string>? Files { get; init; }
    /// <summary>PNG bytes when the source offered a "PNG" format.</summary>
    public byte[]? ImagePng { get; init; }
    /// <summary>Raw BGRA32 pixels when only a bitmap was offered (encoded to PNG in the background).</summary>
    public byte[]? ImagePixels { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public string? SourceApp { get; init; }
    /// <summary>The source set ExcludeClipboardContentFromMonitorProcessing, CanIncludeInClipboardHistory=0 or Clipboard Viewer Ignore.</summary>
    public bool SensitiveFormat { get; init; }

    public bool HasFiles => Files is { Count: > 0 };
    public bool HasImage => ImagePng is { Length: > 0 } || (ImagePixels is { Length: > 0 } && ImageWidth > 0 && ImageHeight > 0);
    public bool HasText => !string.IsNullOrEmpty(Text) || !string.IsNullOrEmpty(Html);
    public bool IsEmpty => !HasFiles && !HasImage && !HasText;
}

/// <summary>An immutable snapshot of one history item. Sensitive items keep only DPAPI-protected content in memory.</summary>
public sealed record ClipEntry
{
    public long Id { get; init; }
    public ClipKind Kind { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastUsedAt { get; init; }
    public bool Pinned { get; init; }
    public bool Sensitive => Reason != SensitiveReason.None;
    public SensitiveReason Reason { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? SourceApp { get; init; }
    public bool IsVideoLink { get; init; }
    public string? ImageFile { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Hash { get; init; }
    /// <summary>Decrypted content; null for sensitive items (use <see cref="ClipboardHistory.GetPayload"/>).</summary>
    public ClipPayload? Payload { get; init; }
    /// <summary>DPAPI-protected payload for sensitive items only.</summary>
    internal byte[]? ProtectedPayload { get; init; }
    public IReadOnlySet<long> TagIds { get; init; } = new HashSet<long>();
    /// <summary>Normalized (SearchMatcher.Normalize) text used for searching; empty for sensitive items.</summary>
    public string SearchText { get; init; } = "";
    /// <summary>Safe one-line preview: snippet for normal items, mask for sensitive ones.</summary>
    public string Preview { get; init; } = "";
}

public sealed record ClipTag(long Id, string Name);

public enum CaptureOutcome { Added, MovedToTop, Ignored }

public sealed record CaptureResult(CaptureOutcome Outcome, ClipEntry? Entry, string? Url);
