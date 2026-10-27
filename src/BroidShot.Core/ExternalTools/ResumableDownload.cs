using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BroidShot.Core;

internal static class ResumableDownload
{
    internal static async Task<string> FetchAsync(HttpClient http, Uri url, string path, long maximum,
        string label, IProgress<ExternalToolProgress>? progress, CancellationToken token, bool freshRetry = false)
    {
        long offset = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (offset > maximum) { File.Delete(path); offset = 0; }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new ExternalToolException(label + " yalnız HTTPS üzerinden indirilebilir.");
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
        {
            if (response.Content.Headers.ContentRange?.Length == offset)
            {
                await using var complete = File.OpenRead(path);
                return Convert.ToHexString(await SHA256.HashDataAsync(complete, token).ConfigureAwait(false)).ToLowerInvariant();
            }
            File.Delete(path);
            if (!freshRetry) return await FetchAsync(http, url, path, maximum, label, progress, token, true).ConfigureAwait(false);
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{label} indirilemedi (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
        bool resume = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != offset)
        {
            if (File.Exists(path)) File.Delete(path);
            throw new HttpRequestException(label + " için sunucunun indirme aralığı tutarsız.");
        }
        if (!resume) offset = 0; // A server ignoring Range sends the entire file; replace rather than append.
        long? total = response.StatusCode == HttpStatusCode.PartialContent
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;
        if (total > maximum) throw new ExternalToolException(label + " dosyası izin verilen boyuttan büyük.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);
        byte[] buffer = new byte[81920];
        if (offset == 0) output.SetLength(0);
        else
        {
            int existing;
            while ((existing = await output.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, existing);
        }
        output.Position = offset;
        await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        long received = offset;
        var elapsed = Stopwatch.StartNew();
        var throttle = Stopwatch.StartNew();
        void Report()
        {
            double speed = (received - offset) / Math.Max(elapsed.Elapsed.TotalSeconds, 0.001);
            TimeSpan? eta = total is { } size && speed > 0 ? TimeSpan.FromSeconds(Math.Clamp((size - received) / speed, 0, 86400)) : null;
            progress?.Report(new(label, ExternalToolStage.Downloading, received, total, speed > 0 ? speed : null, eta, resume));
        }
        Report();
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            received += read;
            if (received > maximum) throw new ExternalToolException(label + " dosyası izin verilen boyuttan büyük.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            if (throttle.ElapsedMilliseconds >= 200) { Report(); throttle.Restart(); }
        }
        if (total is { } expected && received != expected)
            throw new HttpRequestException(label + " indirmesi kesildi; sonraki denemede kaldığı yerden devam edilecek.");
        Report();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
