using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Zorofin.Configuration;
using Jellyfin.Plugin.Zorofin.Requests;

namespace Jellyfin.Plugin.Zorofin.Arr;

/// <summary>
/// Reads Sonarr and Radarr for the Downloads tab, the Calendar and admin links, with the operator's keys. Calendar and
/// tag answers are cached briefly so a busy evening doesn't hammer the services.
/// </summary>
public class ArrService
{
    private static readonly TimeSpan CalendarLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TagsLifetime = TimeSpan.FromMinutes(30);
    private readonly UpstreamClient _upstream;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, JsonArray Body)> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrService"/> class.
    /// </summary>
    /// <param name="upstream">Upstream client.</param>
    public ArrService(UpstreamClient upstream)
    {
        _upstream = upstream;
    }

    /// <summary>Sonarr, if set up.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The endpoint or null.</returns>
    public static ServiceEndpoint? Sonarr(PluginConfiguration config) =>
        config is not null && ServiceEndpoint.TryCreate(config.SonarrUrl, config.SonarrApiKey, out var e) ? e : null;

    /// <summary>Radarr, if set up.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The endpoint or null.</returns>
    public static ServiceEndpoint? Radarr(PluginConfiguration config) =>
        config is not null && ServiceEndpoint.TryCreate(config.RadarrUrl, config.RadarrApiKey, out var e) ? e : null;

    /// <summary>Everything downloading in Sonarr and Radarr, optionally only some titles.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="visibleTmdbIds">Titles the viewer may see, or null for all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Downloads, and whether any service failed to answer.</returns>
    public async Task<JsonObject> DownloadsAsync(PluginConfiguration config, IReadOnlySet<int>? visibleTmdbIds, CancellationToken cancellationToken)
    {
        var items = new List<JsonObject>();
        var failed = false;
        foreach (var (source, endpoint, include) in new[]
        {
            ("sonarr", Sonarr(config), "includeSeries=true&includeEpisode=true"),
            ("radarr", Radarr(config), "includeMovie=true"),
        })
        {
            if (endpoint is null)
            {
                continue;
            }

            var queue = await _upstream.SendAsync(endpoint, HttpMethod.Get, "api/v3/queue?pageSize=250&" + include, null, null, cancellationToken).ConfigureAwait(false);
            if (queue.Ok)
            {
                items.AddRange(ArrShaper.Downloads(queue.Body?["records"] as JsonArray, source, visibleTmdbIds));
            }
            else
            {
                failed = true;
            }
        }

        // Active first, then by most progress.
        var order = new[] { "downloading", "importing", "stalled", "paused", "queued", "failed" };
        var sorted = items.OrderBy(i => Array.IndexOf(order, (string?)i["state"]))
            .ThenByDescending(i => (double?)i["progress"] ?? -1)
            .Select(i => (JsonNode)i)
            .ToArray();
        return new JsonObject { ["items"] = new JsonArray(sorted), ["partial"] = failed };
    }

    /// <summary>Upcoming episodes and movie releases between two dates.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="start">Window start.</param>
    /// <param name="end">Window end.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Events in date order.</returns>
    public async Task<List<JsonObject>> CalendarAsync(PluginConfiguration config, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var range = "start=" + Uri.EscapeDataString(start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
            + "&end=" + Uri.EscapeDataString(end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        var events = new List<JsonObject>();
        if (Sonarr(config) is { } sonarr)
        {
            var body = await CachedAsync(sonarr, "api/v3/calendar?includeSeries=true&unmonitored=false&" + range, CalendarLifetime, cancellationToken).ConfigureAwait(false);
            events.AddRange(ArrShaper.CalendarEpisodes(body));
        }

        if (Radarr(config) is { } radarr)
        {
            var body = await CachedAsync(radarr, "api/v3/calendar?unmonitored=false&" + range, CalendarLifetime, cancellationToken).ConfigureAwait(false);
            events.AddRange(ArrShaper.CalendarMovies(body, start, end));
        }

        return events.OrderBy(e => (string?)e["at"], StringComparer.Ordinal).ThenBy(e => (string?)e["title"], StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Admin links and tags for a title, from whichever of Sonarr or Radarr has it.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="isSeries">Whether the title is a show.</param>
    /// <param name="tvdbId">TVDB id, for shows.</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Links (zero or one).</returns>
    public async Task<JsonArray> LinksAsync(PluginConfiguration config, bool isSeries, string? tvdbId, string? tmdbId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var links = new JsonArray();
        var (source, endpoint, publicUrl, query) = isSeries
            ? ("sonarr", Sonarr(config), config.SonarrPublicUrl, Digits(tvdbId) is { } tvdb ? "api/v3/series?tvdbId=" + tvdb : null)
            : ("radarr", Radarr(config), config.RadarrPublicUrl, Digits(tmdbId) is { } tmdb ? "api/v3/movie?tmdbId=" + tmdb : null);
        if (endpoint is null || query is null)
        {
            return links;
        }

        var found = await _upstream.SendAsync(endpoint, HttpMethod.Get, query, null, null, cancellationToken).ConfigureAwait(false);
        var title = found.Ok ? (found.Body as JsonArray)?.OfType<JsonObject>().FirstOrDefault() : null;
        if (title is null)
        {
            return links;
        }

        var tags = ArrShaper.Tags(await CachedAsync(endpoint, "api/v3/tag", TagsLifetime, cancellationToken).ConfigureAwait(false));
        var browserBase = ServiceEndpoint.TryCreate(publicUrl, "x", out var pub) ? pub.BaseUri : endpoint.BaseUri;
        if (ArrShaper.Link(source, title, tags, browserBase) is { } link)
        {
            links.Add(link);
        }

        return links;
    }

    private static string? Digits(string? value) =>
        value is { Length: > 0 and < 12 } && value.All(char.IsAsciiDigit) ? value : null;

    private async Task<JsonArray?> CachedAsync(ServiceEndpoint endpoint, string path, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var key = endpoint.BaseUri.AbsoluteUri + path;
        if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < lifetime)
        {
            return hit.Body;
        }

        var result = await _upstream.SendAsync(endpoint, HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Body is not JsonArray body)
        {
            return hit.Body;
        }

        if (_cache.Count > 200)
        {
            _cache.Clear();
        }

        _cache[key] = (DateTimeOffset.UtcNow, body);
        return body;
    }
}
