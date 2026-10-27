using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BroidShot.Core;

/// <summary>Minimal reader for the public GitHub "latest release" API (no token; 60 requests/hour per IP).</summary>
public static class GitHubReleases
{
    public sealed record Asset(string Name, Uri DownloadUrl, long Size, DateTimeOffset? UpdatedAt, string? Sha256Digest);
    public sealed record Release(string TagName, string? Name, DateTimeOffset? PublishedAt, IReadOnlyList<Asset> Assets, string? Notes = null);

    public static async Task<Release> GetLatestAsync(HttpClient http, string owner, string repository, CancellationToken cancellationToken)
        => await FindLatestAsync(http, owner, repository, cancellationToken).ConfigureAwait(false)
            ?? throw new ExternalToolException($"{owner}/{repository} için yayımlanmış sürüm bulunamadı.");

    public static async Task<Release?> FindLatestAsync(HttpClient http, string owner, string repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var url = new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/latest");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub sürüm bilgisi alınamadı ({owner}/{repository}, HTTP {(int)response.StatusCode}).", null, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return Parse(json);
    }

    public static Release Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        var assets = new List<Asset>();
        if (root.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                string? name = item.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                string? url = item.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                if (name is null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                long size = item.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long value) ? value : 0;
                DateTimeOffset? updated = item.TryGetProperty("updated_at", out JsonElement d) && d.TryGetDateTimeOffset(out DateTimeOffset date) ? date : null;
                string? digest = item.TryGetProperty("digest", out JsonElement g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                string? sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && ChecksumFile.IsSha256(digest[7..])
                    ? digest[7..].ToLowerInvariant() : null;
                assets.Add(new Asset(name, uri, size, updated, sha));
            }
        }
        string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
        string? title = root.TryGetProperty("name", out JsonElement r) ? r.GetString() : null;
        DateTimeOffset? published = root.TryGetProperty("published_at", out JsonElement p) && p.TryGetDateTimeOffset(out DateTimeOffset at) ? at : null;
        string? notes = root.TryGetProperty("body", out JsonElement body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null;
        return new Release(tag, title, published, assets, notes);
    }
}
