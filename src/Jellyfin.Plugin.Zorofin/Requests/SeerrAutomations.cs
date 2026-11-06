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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Zorofin.Requests;

/// <summary>
/// Background jobs that keep Seerr in step with Jellyfin:
/// <list type="bullet">
/// <item>Next season requests: when a viewer nears the end of the newest season this server has, the next
/// season is requested in Seerr as that viewer, so Seerr's permissions, quotas and approvals apply.</item>
/// <item>Recently-added scans: when movies or episodes arrive in the library, Seerr's Jellyfin
/// recently-added scan runs (once per batch), so requests turn Available without waiting for Seerr's timer.</item>
/// </list>
/// </summary>
public sealed class SeerrAutomations : IHostedService, IDisposable
{
    /// <summary>Seerr's job id for its Jellyfin recently-added scan.</summary>
    public const string RecentlyAddedScanJob = "jellyfin-recently-added-scan";

    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(12);
    private static readonly TimeSpan MaxScanWait = TimeSpan.FromMinutes(10);
    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly UpstreamClient _upstream;
    private readonly SeerrUserDirectory _users;
    private readonly RequestsService _requests;
    private readonly ILogger<SeerrAutomations> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _attempted = new(StringComparer.Ordinal);
    private readonly object _scanLock = new();
    private Timer? _scanTimer;
    private DateTime? _firstPendingAdd;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrAutomations"/> class.
    /// </summary>
    /// <param name="sessions">Session manager.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="upstream">Upstream client.</param>
    /// <param name="users">Seerr user directory.</param>
    /// <param name="requests">Requests service.</param>
    /// <param name="logger">Logger.</param>
    public SeerrAutomations(ISessionManager sessions, ILibraryManager library, UpstreamClient upstream, SeerrUserDirectory users, RequestsService requests, ILogger<SeerrAutomations> logger)
    {
        _sessions = sessions;
        _library = library;
        _upstream = upstream;
        _users = users;
        _requests = requests;
        _logger = logger;
    }

    /// <summary>Gets the Seerr endpoint for background jobs. Needs only the address and key, not the Requests switch.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The endpoint or null.</returns>
    public static ServiceEndpoint? Seerr(PluginConfiguration? config) =>
        config is not null && ServiceEndpoint.TryCreate(config.SeerrUrl, config.SeerrApiKey, out var endpoint) ? endpoint : null;

    /// <summary>
    /// Decides whether finishing this episode should ask for the next season.
    /// </summary>
    /// <param name="season">Season of the episode watched.</param>
    /// <param name="episode">Episode number watched.</param>
    /// <param name="library">Every (season, episode) the server has for the series, specials included.</param>
    /// <param name="episodesLeft">Ask when this many or fewer episodes of the season remain after this one.</param>
    /// <returns>The season to request, or null.</returns>
    public static int? NextSeason(int season, int episode, IEnumerable<(int Season, int Episode)> library, int episodesLeft)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (season < 1 || episode < 1)
        {
            return null;
        }

        var list = library.ToList();
        // Only the newest season on the server: an older season's next one is already here.
        if (list.Any(e => e.Season > season))
        {
            return null;
        }

        var remaining = list.Count(e => e.Season == season && e.Episode > episode);
        return remaining <= Math.Max(0, episodesLeft) ? season + 1 : null;
    }

    /// <summary>
    /// Whether Seerr would take a request for this season: it exists in Seerr's details and is not already
    /// available, being processed, or in any earlier request (declined ones included, so a "no" sticks).
    /// </summary>
    /// <param name="title">Seerr's raw <c>/api/v1/tv/{id}</c> body.</param>
    /// <param name="season">Season number.</param>
    /// <param name="todayUtc">Today, to skip seasons with no aired or announced episodes.</param>
    /// <returns>True when the season can be requested.</returns>
    public static bool SeasonIsOpen(JsonNode? title, int season, DateTime todayUtc)
    {
        if (title?["seasons"] is not JsonArray seasons)
        {
            return false;
        }

        var listed = seasons.OfType<JsonObject>().FirstOrDefault(s => Int(s["seasonNumber"]) == season);
        if (listed is null || Int(listed["episodeCount"]) is not > 0)
        {
            return false;
        }

        // Unknown air date is fine (announced seasons); one more than a year out is not worth a request yet.
        if (listed["airDate"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            && DateTime.TryParse((string?)listed["airDate"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var airs)
            && airs > todayUtc.AddYears(1))
        {
            return false;
        }

        if (title["mediaInfo"] is not JsonObject media)
        {
            return true;
        }

        // Seerr media status: 1 unknown, 2 pending, 3 processing, 4 partially available, 5 available.
        if (media["seasons"] is JsonArray mediaSeasons
            && mediaSeasons.OfType<JsonObject>().Any(s => Int(s["seasonNumber"]) == season && Int(s["status"]) is > 1))
        {
            return false;
        }

        return media["requests"] is not JsonArray requests
            || !requests.OfType<JsonObject>().Any(r => r["seasons"] is JsonArray rs && rs.OfType<JsonObject>().Any(s => Int(s["seasonNumber"]) == season));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped += OnStopped;
        _library.ItemAdded += OnItemAdded;
        _scanTimer = new Timer(_ => _ = RunScanAsync(), null, Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped -= OnStopped;
        _library.ItemAdded -= OnItemAdded;
        _scanTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _scanTimer?.Dispose();

    /// <summary>Asks for the next season if this play qualifies. Public for tests.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="userId">Viewer.</param>
    /// <param name="episode">Episode watched.</param>
    /// <param name="watchedFraction">How much of it was watched, 0 to 1.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The season requested, or null.</returns>
    public async Task<int?> MaybeRequestNextSeasonAsync(PluginConfiguration config, Guid userId, Episode episode, double watchedFraction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(episode);
        if (!config.AutoSeasonRequestsEnabled || Seerr(config) is not { } seerr || watchedFraction < 0.5
            || episode.ParentIndexNumber is not { } season || episode.IndexNumber is not { } number || episode.Series is not { } series)
        {
            return null;
        }

        var have = series.GetRecursiveChildren(i => i is Episode && !i.IsVirtualItem)
            .Cast<Episode>()
            .Where(e => e.ParentIndexNumber is not null && e.IndexNumber is not null)
            .Select(e => (e.ParentIndexNumber!.Value, e.IndexNumber!.Value));
        if (NextSeason(season, number, have, config.AutoSeasonEpisodesLeft) is not { } next
            || !int.TryParse(series.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var tmdb))
        {
            return null;
        }

        var key = userId.ToString("N") + "|" + series.Id.ToString("N") + "|" + next.ToString(CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        if (_attempted.TryGetValue(key, out var last) && now - last < RetryAfter)
        {
            return null;
        }

        _attempted[key] = now;
        if (_attempted.Count > 5000)
        {
            foreach (var old in _attempted.Where(a => now - a.Value >= RetryAfter).Select(a => a.Key).ToList())
            {
                _attempted.TryRemove(old, out _);
            }
        }

        if (await _users.FindAsync(seerr, userId, cancellationToken).ConfigureAwait(false) is not { } seerrUser)
        {
            return null;
        }

        var title = await _upstream.SendAsync(seerr, HttpMethod.Get, "api/v1/tv/" + tmdb.ToString(CultureInfo.InvariantCulture), null, seerrUser, cancellationToken).ConfigureAwait(false);
        if (!title.Ok || !SeasonIsOpen(title.Body, next, now))
        {
            return null;
        }

        int? tvdb = int.TryParse(series.GetProviderId(MetadataProvider.Tvdb), NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : null;
        var intent = new RequestIntent { MediaType = "tv", MediaId = tmdb, Seasons = new[] { next }, TvdbId = tvdb };
        var result = await _requests.SubmitAsync(seerr, seerrUser, intent, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            _logger.LogInformation("Requested season {Season} of {Series} for a viewer nearing the end of season {Current}", next, series.Name, season);
            return next;
        }

        _logger.LogInformation("Seerr did not take the request for season {Season} of {Series} (HTTP {Status})", next, series.Name, result.Status);
        return null;
    }

    /// <summary>Runs Seerr's recently-added scan now. Public for tests.</summary>
    /// <returns>Whether Seerr accepted it.</returns>
    public async Task<bool> RunScanAsync()
    {
        lock (_scanLock)
        {
            _firstPendingAdd = null;
        }

        var config = Plugin.Instance?.Configuration;
        if (config?.SeerrScanOnLibraryAdd != true || Seerr(config) is not { } seerr)
        {
            return false;
        }

        try
        {
            var result = await _upstream.SendAsync(seerr, HttpMethod.Post, "api/v1/settings/jobs/" + RecentlyAddedScanJob + "/run", null, null, CancellationToken.None).ConfigureAwait(false);
            if (!result.Ok)
            {
                _logger.LogWarning("Seerr's recently-added scan did not start (HTTP {Status})", result.Status);
            }

            return result.Ok;
        }
        catch (UpstreamUnavailableException)
        {
            _logger.LogWarning("Seerr is unreachable; its recently-added scan was not started");
            return false;
        }
    }

    /// <summary>
    /// When the scan should run after an addition: <paramref name="delay"/> after the latest addition,
    /// but never more than ten minutes after the first one of the batch.
    /// </summary>
    /// <param name="firstAdd">First addition of the batch.</param>
    /// <param name="nowUtc">Now (the latest addition).</param>
    /// <param name="delay">Quiet time to wait for.</param>
    /// <returns>How long to wait from now.</returns>
    public static TimeSpan ScanDue(DateTime firstAdd, DateTime nowUtc, TimeSpan delay)
    {
        var latest = firstAdd + MaxScanWait - nowUtc;
        var wait = delay < latest ? delay : latest;
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    private static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.SeerrScanOnLibraryAdd != true || e.Item is not (Movie or Episode) || e.Item.IsVirtualItem || Seerr(config) is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var delay = TimeSpan.FromSeconds(Math.Clamp(config.SeerrScanDelaySeconds, 10, 3600));
        lock (_scanLock)
        {
            _firstPendingAdd ??= now;
            _scanTimer?.Change(ScanDue(_firstPendingAdd.Value, now, delay), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.AutoSeasonRequestsEnabled != true || e.Item is not Episode episode || e.Session is not { } session || session.UserId == Guid.Empty)
        {
            return;
        }

        var runtime = episode.RunTimeTicks ?? 0;
        var fraction = e.PlayedToCompletion ? 1 : runtime > 0 && e.PlaybackPositionTicks is { } position ? (double)position / runtime : 0;
        _ = Task.Run(async () =>
        {
            try
            {
                await MaybeRequestNextSeasonAsync(config, session.UserId, episode, fraction, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UpstreamUnavailableException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogInformation("Next season request skipped: Seerr is unreachable");
            }
        });
    }
}
