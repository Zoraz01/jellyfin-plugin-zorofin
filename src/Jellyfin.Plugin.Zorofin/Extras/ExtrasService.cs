using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Zorofin.Extras;

/// <summary>
/// Calls the two public services the web extras use, from the server so viewers' browsers never contact them:
/// SponsorBlock (where a banner trailer should start) and MDBList (ratings, with the operator's key). Answers are
/// cached, so each title costs at most one MDBList lookup a day.
/// </summary>
public class ExtrasService
{
    /// <summary>Name of the HttpClient for public services.</summary>
    public const string HttpClientName = "Zorofin.Public";

    private const int MaxBodyBytes = 1024 * 1024;
    private const int MaxCacheEntries = 5000;
    private static readonly TimeSpan TrailerLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan RatingsLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(30);
    private readonly IHttpClientFactory _http;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, int Start)> _trailers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, JsonObject Body)> _ratings = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ExtrasService"/> class.
    /// </summary>
    /// <param name="http">HTTP client factory.</param>
    public ExtrasService(IHttpClientFactory http)
    {
        _http = http;
    }

    /// <summary>Where a YouTube trailer should start so its intro is skipped.</summary>
    /// <param name="videoId">YouTube video id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Seconds from the start; 0 when nothing to skip or SponsorBlock is unreachable.</returns>
    public async Task<int> TrailerStartAsync(string videoId, CancellationToken cancellationToken)
    {
        if (!ExtrasModels.YouTubeId(videoId))
        {
            return 0;
        }

        if (_trailers.TryGetValue(videoId, out var hit) && hit.Until > DateTimeOffset.UtcNow)
        {
            return hit.Start;
        }

        var url = "https://sponsor.ajay.app/api/skipSegments?videoID=" + videoId
            + "&categories=" + Uri.EscapeDataString("[\"intro\",\"sponsor\",\"selfpromo\",\"preview\"]");
        var (status, body) = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        var start = status == 200 ? ExtrasModels.TrailerStart(body as JsonArray) : 0;
        // 404 means "no segments": a real answer, kept as long as a found one.
        var lifetime = status is 200 or 404 ? TrailerLifetime : FailureLifetime;
        Remember(_trailers, videoId, (DateTimeOffset.UtcNow + lifetime, start));
        return start;
    }

    /// <summary>Ratings for one title from MDBList.</summary>
    /// <param name="apiKey">Operator's MDBList key.</param>
    /// <param name="kind">"movie" or "show".</param>
    /// <param name="tmdbId">TMDB id, when known.</param>
    /// <param name="imdbId">IMDb id, when known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The shaped ratings, or null when MDBList has none or can't be reached.</returns>
    public virtual async Task<JsonObject?> RatingsAsync(string apiKey, string kind, string? tmdbId, string? imdbId, CancellationToken cancellationToken)
    {
        string path;
        if (tmdbId is { Length: > 0 and < 12 } && ulong.TryParse(tmdbId, out _))
        {
            path = "tmdb/" + kind + "/" + tmdbId;
        }
        else if (imdbId is { Length: > 2 and < 16 } && imdbId.StartsWith("tt", StringComparison.Ordinal) && ulong.TryParse(imdbId.AsSpan(2), out _))
        {
            path = "imdb/" + kind + "/" + imdbId;
        }
        else
        {
            return null;
        }

        if (_ratings.TryGetValue(path, out var hit) && hit.Until > DateTimeOffset.UtcNow)
        {
            return hit.Body.Count == 0 ? null : hit.Body;
        }

        var (status, body) = await GetAsync("https://api.mdblist.com/" + path + "?apikey=" + Uri.EscapeDataString(apiKey.Trim()), cancellationToken).ConfigureAwait(false);
        if (status != 200 && status != 404)
        {
            // Quota or outage: keep showing yesterday's answer if there is one.
            return hit.Body is { Count: > 0 } ? hit.Body : null;
        }

        var shaped = status == 200 ? ExtrasModels.Ratings(body) : new JsonObject();
        Remember(_ratings, path, (DateTimeOffset.UtcNow + RatingsLifetime, shaped));
        return shaped.Count == 0 ? null : shaped;
    }

    private static void Remember<T>(ConcurrentDictionary<string, T> cache, string key, T value)
    {
        if (cache.Count > MaxCacheEntries)
        {
            cache.Clear();
        }

        cache[key] = value;
    }

    private async Task<(int Status, JsonNode? Body)> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("User-Agent", "Zorofin-Jellyfin-Plugin");
            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxBodyBytes)
            {
                return (502, null);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? body = null;
            if (bytes.Length is > 0 and <= MaxBodyBytes)
            {
                try
                {
                    body = JsonNode.Parse(bytes);
                }
                catch (System.Text.Json.JsonException)
                {
                    body = null;
                }
            }

            return ((int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (0, null);
        }
    }
}
