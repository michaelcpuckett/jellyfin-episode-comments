using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.EpisodeComments;

public sealed record TraktResponse(JsonElement Data, int PageCount, int TotalItems, DateTimeOffset FetchedAt);

public sealed class TraktClient(IHttpClientFactory factory, Func<PluginConfiguration> configuration, TimeProvider clock) : IDisposable
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
    // Serializing cache misses also coalesces requests for the same page and bounds upstream concurrency.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _retryAt;

    public async Task<TraktResponse> GetAsync(string path, CancellationToken ct)
    {
        var config = configuration();
        var clientId = config.TraktClientId?.Trim() ?? "";
        if (clientId.Length == 0)
            throw new CommentsException(503, "not_configured", "Set the Trakt Client ID in the Episode Comments plugin settings.");
        // Partition cached data when credentials change. Never put the Client ID in a log or response.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId))) + ":" + path;
        if (_cache.TryGetValue(key, out TraktResponse? hit)) return hit!;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out hit)) return hit!;
            var now = clock.GetUtcNow();
            if (_retryAt > now)
                throw new CommentsException(429, "trakt_rate_limited", "Trakt is rate limiting requests. Try again later.", (int)Math.Ceiling((_retryAt - now).TotalSeconds));
            // HttpClient's timeout stops at response headers with ResponseHeadersRead.
            // Bound the body read as well, while preserving caller cancellation.
            using var upstreamTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            upstreamTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var upstreamToken = upstreamTimeout.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("trakt-api-key", clientId);
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Accept.ParseAdd("application/json");
            using var client = factory.CreateClient("EpisodeComments.Trakt");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upstreamToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - now) ?? TimeSpan.FromSeconds(60);
                var seconds = (int)Math.Clamp(Math.Ceiling(delay.TotalSeconds), 1, 86400);
                _retryAt = now.AddSeconds(seconds);
                throw new CommentsException(429, "trakt_rate_limited", "Trakt is rate limiting requests. Try again later.", seconds);
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new CommentsException(404, "trakt_not_found", "Trakt has no matching episode or comment.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CommentsException(503, "trakt_credentials_rejected", "Trakt rejected the server's API credentials.");
            if (!response.IsSuccessStatusCode)
                throw new CommentsException(502, "trakt_unavailable", "Trakt could not complete the request.");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new CommentsException(502, "trakt_invalid_response", "Trakt returned an oversized response.");
            await using var stream = await response.Content.ReadAsStreamAsync(upstreamToken).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, upstreamToken).ConfigureAwait(false)) != 0)
            {
                if (body.Length + read > MaxResponseBytes)
                    throw new CommentsException(502, "trakt_invalid_response", "Trakt returned an oversized response.");
                body.Write(buffer, 0, read);
            }
            body.Position = 0;
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: upstreamToken).ConfigureAwait(false);
            var result = new TraktResponse(json.RootElement.Clone(), Header(response, "X-Pagination-Page-Count"), Header(response, "X-Pagination-Item-Count"), clock.GetUtcNow());
            _cache.Set(key, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Clamp(config.CacheMinutes, 1, 1440)),
                Size = body.Length + key.Length * 2 + 512
            });
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CommentsException(504, "trakt_timeout", "Trakt did not respond in time.");
        }
        catch (HttpRequestException)
        {
            throw new CommentsException(502, "trakt_unavailable", "Could not connect to Trakt.");
        }
        catch (IOException)
        {
            throw new CommentsException(502, "trakt_unavailable", "Could not read Trakt's response.");
        }
        catch (JsonException)
        {
            throw new CommentsException(502, "trakt_invalid_response", "Trakt returned an invalid response.");
        }
        finally { _gate.Release(); }
    }

    private static int Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? Math.Max(value, 0) : 0;
    public void Dispose() { _cache.Dispose(); _gate.Dispose(); }
}
