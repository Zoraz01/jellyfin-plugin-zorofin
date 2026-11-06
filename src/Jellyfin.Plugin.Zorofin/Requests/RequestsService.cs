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

namespace Jellyfin.Plugin.Zorofin.Requests;

/// <summary>Requests module: acts as each viewer's linked Seerr user and returns only what the apps need.</summary>
public class RequestsService
{
    private static readonly TimeSpan SettingsLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DetailsLifetime = TimeSpan.FromHours(1);
    private readonly UpstreamClient _upstream;
    private readonly SeerrUserDirectory _users;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, JsonNode? Value)> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestsService"/> class.
    /// </summary>
    /// <param name="upstream">Upstream client.</param>
    /// <param name="users">Seerr user directory.</param>
    public RequestsService(UpstreamClient upstream, SeerrUserDirectory users)
    {
        _upstream = upstream;
        _users = users;
    }

    /// <summary>Gets the Seerr endpoint when the module is on and configured.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The endpoint or null.</returns>
    public static ServiceEndpoint? Seerr(PluginConfiguration? config) =>
        config is { RequestsEnabled: true } && ServiceEndpoint.TryCreate(config.SeerrUrl, config.SeerrApiKey, out var endpoint) ? endpoint : null;

    /// <summary>Reports whether this viewer can use Requests.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="viewer">Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Status JSON matching the apps' contract.</returns>
    public async Task<JsonObject> UserStatusAsync(PluginConfiguration config, Guid viewer, CancellationToken cancellationToken)
    {
        if (Seerr(config) is not { } seerr)
        {
            return new JsonObject { ["active"] = false, ["userFound"] = false, ["reason"] = "Requests is not set up on this server." };
        }

        var id = await _users.FindAsync(seerr, viewer, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["active"] = true,
            ["userFound"] = id is not null,
            ["reason"] = id is null ? "Your account is not linked to the server's request service. Ask your server owner to import it." : null,
        };
    }

    /// <summary>Gets 4K availability for movies and shows.</summary>
    /// <param name="seerr">Seerr endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Configuration JSON matching the apps' contract.</returns>
    public async Task<JsonObject> ConfigurationAsync(ServiceEndpoint seerr, CancellationToken cancellationToken)
    {
        var radarr = await CachedAsync(seerr, "api/v1/settings/radarr", SettingsLifetime, cancellationToken).ConfigureAwait(false);
        var sonarr = await CachedAsync(seerr, "api/v1/settings/sonarr", SettingsLifetime, cancellationToken).ConfigureAwait(false);
        static bool Has4K(JsonNode? servers) => (servers as JsonArray)?.Any(s => RequestShaper.Bool(s?["is4k"]) == true) == true;
        return new JsonObject { ["jellyseerrEnable4KRequests"] = Has4K(radarr), ["jellyseerrEnable4KTvRequests"] = Has4K(sonarr) };
    }

    /// <summary>Gets season request settings.</summary>
    /// <param name="seerr">Seerr endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Settings JSON matching the apps' contract.</returns>
    public async Task<JsonObject> SeasonSettingsAsync(ServiceEndpoint seerr, CancellationToken cancellationToken)
    {
        var main = await CachedAsync(seerr, "api/v1/settings/main", SettingsLifetime, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["partialRequestsEnabled"] = RequestShaper.Bool(main?["partialRequestsEnabled"]) ?? false,
            ["enableSpecialEpisodes"] = RequestShaper.Bool(main?["enableSpecialEpisodes"]) ?? false,
        };
    }

    /// <summary>Gets this viewer's request limits.</summary>
    /// <param name="seerr">Seerr endpoint.</param>
    /// <param name="seerrUser">Viewer's Seerr id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Quota JSON.</returns>
    public async Task<UpstreamResult> QuotaAsync(ServiceEndpoint seerr, int seerrUser, CancellationToken cancellationToken)
    {
        var result = await _upstream.SendAsync(seerr, HttpMethod.Get, Invariant($"api/v1/user/{seerrUser}/quota"), null, seerrUser, cancellationToken).ConfigureAwait(false);
        return result.Ok ? result with { Body = RequestShaper.ShapeQuota(result.Body) } : result with { Body = null };
    }

    /// <summary>Gets a discover or search page as the viewer.</summary>
    /// <param name="seerr">Seerr endpoint.</param>
    /// <param name="seerrUser">Viewer's Seerr id.</param>
    /// <param name="relativePath">Validated Seerr path with query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Shaped page.</returns>
    public async Task<UpstreamResult> PageAsync(ServiceEndpoint seerr, int seerrUser, string relativePath, CancellationToken cancellationToken)
    {
        var result = await _upstream.SendAsync(seerr, HttpMethod.Get, relativePath, null, seerrUser, cancellationToken).ConfigureAwait(false);
        return result.Ok ? result with { Body = RequestShaper.ShapePage(result.Body) } : result with { Body = null };
    }

    /// <summary>Gets Seerr's movie or show genres. They rarely change, so they are kept for an hour.</summary>
    /// <param name="seerr">Seerr endpoint.</param>
    /// <param name="kind">"movie" or "tv".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Genres, or null when Seerr could not be read.</returns>
    public async Task<JsonArray?> GenresAsync(ServiceEndpoint seerr, string kind, CancellationToken cancellationToken)
    {
        var genres = await CachedAsync(seerr, Invariant($"api/v1/genres/{kind}"), DetailsLifetime, cancellationToken).ConfigureAwait(false);
        return genres is JsonArray ? RequestShaper.ShapeGenres(genres) : null;
    }

    /// <summary>Gets one title's details as the viewer.</summary>
    /// <param name="seerr">Seerr endpoint.</param>
    /// <param name="seerrUser">Viewer's Seerr id.</param>
    /// <param name="kind">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Shaped title.</returns>
    public async Task<UpstreamResult> TitleAsync(ServiceEndpoint seerr, int seerrUser, string kind, int tmdbId, CancellationToken cancellationToken)
    {
        var result = await _upstream.SendAsync(seerr, HttpMethod.Get, Invariant($"api/v1/{kind}/{tmdbId}"), null, seerrUser, cancellationToken).ConfigureAwait(false);
        var title = result.Ok ? RequestShaper.ShapeTitle(result.Body) : null;
        return title is null ? result with { Status = result.Ok ? 502 : result.Status, Body = null } : result with { Body = title };
    }

    /// <summary>Submits a request as the viewer. Seerr applies the viewer's permissions, quota and auto-approval.</summary>
    /// <param name="seerr">Seerr endpoint.</param>
    /// <param name="seerrUser">Viewer's Seerr id.</param>
    /// <param name="intent">Validated intent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Shaped receipt.</returns>
    public async Task<UpstreamResult> SubmitAsync(ServiceEndpoint seerr, int seerrUser, RequestIntent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var result = await _upstream.SendAsync(seerr, HttpMethod.Post, "api/v1/request", intent.ToSeerrBody(), seerrUser, cancellationToken).ConfigureAwait(false);
        return result.Ok && result.Body is JsonObject receipt ? result with { Body = RequestShaper.ShapeReceipt(receipt) } : result with { Body = null };
    }

    /// <summary>Lists requests the viewer may see, newest first.</summary>
    /// <param name="seerr">Seerr endpoint.</param>
    /// <param name="seerrUser">Viewer's Seerr id.</param>
    /// <param name="mine">Only the viewer's own requests.</param>
    /// <param name="take">Page size.</param>
    /// <param name="skip">Offset.</param>
    /// <param name="filter">Seerr filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Activity page.</returns>
    public async Task<UpstreamResult> ActivityAsync(ServiceEndpoint seerr, int seerrUser, bool mine, int take, int skip, string filter, CancellationToken cancellationToken)
    {
        // Without requestedBy, Seerr itself decides whether this viewer may see other people's requests.
        var path = Invariant($"api/v1/request?take={take}&skip={skip}&filter={filter}&sort=added") + (mine ? Invariant($"&requestedBy={seerrUser}") : string.Empty);
        var result = await _upstream.SendAsync(seerr, HttpMethod.Get, path, null, seerrUser, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return result with { Body = null };
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var items = new List<JsonNode?>();
        foreach (var request in result.Body?["results"] as JsonArray ?? new JsonArray())
        {
            var type = RequestShaper.Str(request?["type"]);
            var tmdbId = RequestShaper.Int(request?["media"]?["tmdbId"]);
            JsonNode? details = null;
            if (type is "movie" or "tv" && tmdbId is > 0)
            {
                details = await CachedAsync(seerr, Invariant($"api/v1/{type}/{tmdbId}"), DetailsLifetime, cancellationToken).ConfigureAwait(false);
            }

            if (RequestShaper.ShapeActivityItem(request, details, today, includeRequester: !mine) is { } item)
            {
                items.Add(item);
            }
        }

        return result with { Body = new JsonObject { ["requests"] = new JsonArray(items.ToArray()), ["totalPages"] = RequestShaper.Int(result.Body?["pageInfo"]?["pages"]) } };
    }

    /// <summary>Lists download progress for titles covered by requests this viewer can see.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="seerr">Seerr endpoint.</param>
    /// <param name="seerrUser">Viewer's Seerr id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Queue JSON.</returns>
    public async Task<JsonObject> QueueAsync(PluginConfiguration config, ServiceEndpoint seerr, int seerrUser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var visible = await _upstream.SendAsync(seerr, HttpMethod.Get, "api/v1/request?take=100&skip=0&filter=processing&sort=added", null, seerrUser, cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<int>();
        foreach (var request in visible.Ok ? visible.Body?["results"] as JsonArray ?? new JsonArray() : new JsonArray())
        {
            if (RequestShaper.Int(request?["media"]?["tmdbId"]) is { } id)
            {
                ids.Add(id);
            }
        }

        var items = new List<JsonNode?>();
        if (ids.Count > 0)
        {
            foreach (var (source, url, key, include) in new[]
            {
                ("radarr", config.RadarrUrl, config.RadarrApiKey, "includeMovie=true"),
                ("sonarr", config.SonarrUrl, config.SonarrApiKey, "includeSeries=true"),
            })
            {
                if (!ServiceEndpoint.TryCreate(url, key, out var arr))
                {
                    continue;
                }

                var queue = await _upstream.SendAsync(arr, HttpMethod.Get, "api/v3/queue?pageSize=200&" + include, null, null, cancellationToken).ConfigureAwait(false);
                if (queue.Ok)
                {
                    items.AddRange(RequestShaper.ShapeQueue(queue.Body?["records"] as JsonArray, source, ids));
                }
            }
        }

        return new JsonObject { ["items"] = new JsonArray(items.ToArray()) };
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private async Task<JsonNode?> CachedAsync(ServiceEndpoint seerr, string path, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var key = seerr.BaseUri.AbsoluteUri + path;
        if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < lifetime)
        {
            return hit.Value;
        }

        var result = await _upstream.SendAsync(seerr, HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return hit.Value;
        }

        if (_cache.Count > 2000)
        {
            _cache.Clear();
        }

        _cache[key] = (DateTimeOffset.UtcNow, result.Body);
        return result.Body;
    }
}
