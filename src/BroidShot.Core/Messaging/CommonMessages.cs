using System;
using System.Collections.Generic;

namespace BroidShot.Core;

// Cross-module message contracts. A module may define private message types in its own project,
// but anything another module must understand belongs here (added at integration time).

/// <summary>
/// Published by the clipboard module when a URL is copied (sensitive clipboard content is never published).
/// The media module decides whether it can download it and offers an "İndir" toast; nothing downloads automatically.
/// </summary>
public sealed record UrlCopiedMessage(string Url, bool LooksLikeVideo, string? SourceApplication, DateTimeOffset CopiedAt);

/// <summary>Asks the media module to open its page with this URL prefilled (the user still confirms).</summary>
public sealed record MediaDownloadRequestedMessage(string Url);

/// <summary>Asks the convert module to open its page with these files queued.</summary>
public sealed record FilesToConvertMessage(IReadOnlyList<string> Paths);
