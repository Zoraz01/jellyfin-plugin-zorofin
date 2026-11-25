using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Companion;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Zorofin.Configuration;
using Jellyfin.Plugin.Zorofin.Requests;
using Jellyfin.Plugin.Zorofin.Stats;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.TV;

namespace Jellyfin.Plugin.Zorofin.Home;

/// <summary>One viewer's built Home, with the item JSON for every card.</summary>
/// <param name="GeneratedAt">When it was built.</param>
/// <param name="Rows">Rows in catalog order.</param>
/// <param name="Items">Client-safe item JSON by id.</param>
/// <param name="Teams">The viewer's followed teams.</param>
public sealed record HomeSnapshot(DateTime GeneratedAt, IReadOnlyList<HomeRowResult> Rows, IReadOnlyDictionary<Guid, JsonObject> Items, IReadOnlyList<string> Teams);

/// <summary>Raised when too many Home builds are running at once.</summary>
public sealed class HomeBusyException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HomeBusyException"/> class.
    /// </summary>
    public HomeBusyException()
        : base("Home is busy; try again shortly.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeBusyException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    public HomeBusyException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeBusyException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public HomeBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Builds Home for a viewer. Concurrent requests from the same viewer share one build, and a build is reused for
/// a minute so opening Home on several devices doesn't rescan the library. Nothing is shared between viewers.
/// </summary>
public class HomeRowsService
{
    /// <summary>How long one viewer's build is reused.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly HomeServices _services;
    private static readonly TimeSpan CrestsLifetime = TimeSpan.FromMinutes(5);
    private CrestsCache _crests = new(DateTime.MinValue, Array.Empty<CrestFixture>());
    private readonly CompanionClient _companion;
    private readonly HomeRowsCache _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeRowsService"/> class.
    /// </summary>
    /// <param name="library">Library manager.</param>
    /// <param name="userData">User data manager.</param>
    /// <param name="tv">Next Up.</param>
    /// <param name="dto">Item DTOs.</param>
    /// <param name="preferences">Display preferences.</param>
    /// <param name="liveTv">Live TV.</param>
    /// <param name="stats">Popular-this-week totals.</param>
    /// <param name="history">Play history.</param>
    /// <param name="seerrUsers">Seerr user map.</param>
    /// <param name="upstream">Seerr client.</param>
    /// <param name="hides">Titles viewers removed from Continue Watching.</param>
    /// <param name="companion">Account-scoped recommendation preferences.</param>
    public HomeRowsService(ILibraryManager library, IUserDataManager userData, ITVSeriesManager tv, IDtoService dto, IDisplayPreferencesManager preferences, ILiveTvManager liveTv, PlayStatsStore stats, PlayHistoryStore history, SeerrUserDirectory seerrUsers, UpstreamClient upstream, ContinueWatchingHides hides, CompanionClient companion)
    {
        _companion = companion;
        _services = new HomeServices(library, userData, tv, dto, preferences, liveTv, stats, history, seerrUsers, upstream, hides);
    }

    /// <summary>The rows this server builds: every catalog row the operator didn't switch off, and only rows whose module is on.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>Row ids.</returns>
    public static IReadOnlySet<string> WantedRows(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var off = (config.HomeRowsOff ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var wanted = HomeBuilder.Catalog.Select(r => r.Id).Except(off, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (!config.PlayStatsEnabled && !config.PlayHistoryEnabled)
        {
            wanted.Remove("popular");
        }

        if (!config.RequestsEnabled || RequestsService.Seerr(config) is null)
        {
            wanted.Remove("requests-ready");
        }

        return wanted;
    }

    /// <summary>Gets the viewer's Home, built now or reused from the last minute.</summary>
    /// <param name="user">Viewer.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="fresh">Rebuild instead of reusing a finished build older than a few seconds (after the viewer changed their teams).</param>
    /// <returns>The snapshot.</returns>
    public Task<HomeSnapshot> GetAsync(User user, PluginConfiguration config, bool fresh = false)
    {
        ArgumentNullException.ThrowIfNull(user);
        return _cache.GetAsync(user.Id, fresh, at => BuildAsync(user, config, at, CancellationToken.None));
    }

    /// <summary>Forgets a viewer's cached Home, for example after they change their teams.</summary>
    /// <param name="userId">Viewer.</param>
    public void Forget(Guid userId) => _cache.Forget(userId);

    private async Task<HomeSnapshot> BuildAsync(User user, PluginConfiguration config, DateTime now, CancellationToken cancellationToken)
    {
        var serverTeams = ReadServerTeamsAsync(user, config);
        var crests = ReadCrestsAsync(config);
        var (liked, picks) = await ReadTasteAsync(user, config).ConfigureAwait(false);
        var source = new JellyfinHomeSource(user, _services, config, liked, picks, await serverTeams.ConfigureAwait(false));
        var events = CuratedSeasons.ParseEvents(config.HomeCuratedCalendar);
        var rows = await HomeBuilder.BuildAsync(source, now, TimeZoneInfo.Local, WantedRows(config), events, cancellationToken).ConfigureAwait(false);
        rows = WithCrests(rows, await crests.ConfigureAwait(false));
        var items = source.Describe(rows.SelectMany(r => r.Cards).Select(c => c.Entry.Id));
        return new HomeSnapshot(now, rows, items, source.Teams());
    }
    /// <summary>Adds the two teams and crests to sports cards whose game is in the artwork catalogue.</summary>
    /// <param name="rows">Built rows.</param>
    /// <param name="fixtures">Catalogue.</param>
    /// <returns>Rows.</returns>
    internal static IReadOnlyList<HomeRowResult> WithCrests(IReadOnlyList<HomeRowResult> rows, IReadOnlyList<CrestFixture> fixtures) =>
        fixtures.Count == 0 ? rows : rows.Select(row => row.Id != "sports" ? row : row with
        {
            Cards = row.Cards.Select(card => card.StartsAt is { } start && SportsCrests.Match(card.Subtitle, start, fixtures) is { } game
                ? card with { Game = game } : card).ToList(),
        }).ToList();

    // The companion's sports artwork catalogue (on when the operator turned on sports art), shared by every viewer
    // for a few minutes. Optional: without it, sports cards keep the channel art.
    private async Task<IReadOnlyList<CrestFixture>> ReadCrestsAsync(PluginConfiguration config)
    {
        if (!WantedRows(config).Contains("sports") || CompanionClient.Endpoint(config) is not { } endpoint)
        {
            return Array.Empty<CrestFixture>();
        }

        var cached = _crests;
        if (DateTime.UtcNow - cached.At < CrestsLifetime)
        {
            return cached.Fixtures;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var (status, body, _) = await _companion.GetPublicAsync(endpoint, "v1/public/webUi/live-tv-logos/art/events.json", timeout.Token).ConfigureAwait(false);
            var fixtures = status == 200 ? SportsCrests.Parse(body) : Array.Empty<CrestFixture>();
            _crests = new CrestsCache(DateTime.UtcNow, fixtures);
            return fixtures;
        }
        catch (OperationCanceledException)
        {
            return cached.Fixtures;
        }
    }

    // The server's favorite teams playing today, from the companion's sports module. Viewers who haven't picked
    // their own teams see these games; without either, the sports row stays hidden.
    private async Task<IReadOnlyList<string>> ReadServerTeamsAsync(User user, PluginConfiguration config)
    {
        if (!WantedRows(config).Contains("sports") || CompanionClient.Endpoint(config) is not { } endpoint)
        {
            return Array.Empty<string>();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var features = await _companion.GetFeaturesAsync(config, timeout.Token).ConfigureAwait(false);
            if (!features.TryGetValue("sportsEvents", out var feature) || !feature.Enabled)
            {
                return Array.Empty<string>();
            }

            var feed = await _companion.SendAsync(endpoint, HttpMethod.Get, "v1/modules/sportsEvents/live?window=today", null, user.Id, false, timeout.Token).ConfigureAwait(false);
            return feed.Status == 200 ? HomeRules.ParseFavoriteTeams(feed.Body) : Array.Empty<string>();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<string>();
        }
    }

    private async Task<(IReadOnlyList<Guid> Liked, HomeTastePicks? Picks)> ReadTasteAsync(User user, PluginConfiguration config)
    {
        var wanted = WantedRows(config);
        if ((!wanted.Contains("for-you") && !wanted.Contains("because-you-watched")) || CompanionClient.Endpoint(config) is not { } endpoint)
        {
            return (Array.Empty<Guid>(), null);
        }

        // An optional taste service must never hold up ordinary library rows.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var features = await _companion.GetFeaturesAsync(config, timeout.Token).ConfigureAwait(false);
            if (!features.TryGetValue("recommendations", out var feature) || !feature.Enabled)
            {
                return (Array.Empty<Guid>(), null);
            }

            var taste = _companion.SendAsync(endpoint, HttpMethod.Get, "v1/modules/recommendations/taste", null, user.Id, false, timeout.Token);
            var library = _companion.SendAsync(endpoint, HttpMethod.Get, "v1/modules/recommendations/library", null, user.Id, false, timeout.Token);
            var likedResponse = await taste.ConfigureAwait(false);
            var picksResponse = await library.ConfigureAwait(false);
            return (likedResponse.Status == 200 ? ParseLiked(likedResponse.Body) : Array.Empty<Guid>(),
                picksResponse.Status == 200 ? ParsePicks(picksResponse.Body) : null);
        }
        catch (OperationCanceledException)
        {
            return (Array.Empty<Guid>(), null);
        }
    }

    /// <summary>Reads the companion's library picks; anything malformed is skipped.</summary>
    /// <param name="body">Companion response body.</param>
    /// <returns>Picks, or null when there are none.</returns>
    internal static HomeTastePicks? ParsePicks(JsonNode? body)
    {
        if (body is not JsonObject data)
        {
            return null;
        }

        static IReadOnlyList<Guid> Ids(JsonNode? node) => node is JsonArray ids
            ? ids.Select(id => Guid.TryParse(id?.ToString(), out var parsed) ? parsed : Guid.Empty)
                .Where(id => id != Guid.Empty).Distinct().Take(40).ToArray()
            : Array.Empty<Guid>();

        var because = (data["because"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(row => (Seed: Guid.TryParse(row["seedItemId"]?.ToString(), out var seed) ? seed : Guid.Empty,
                Name: (row["seedName"]?.ToString() ?? string.Empty).Trim(), Items: Ids(row["itemIds"])))
            .Where(row => row.Seed != Guid.Empty && row.Name.Length is > 0 and <= 200 && row.Items.Count > 0)
            .Take(2)
            .Select(row => new HomeTasteRow(row.Seed, row.Name, row.Items))
            .ToArray();
        var forYou = Ids(data["forYou"]);
        return forYou.Count == 0 && because.Length == 0 ? null : new HomeTastePicks(forYou, because);
    }

    internal static IReadOnlyList<Guid> ParseLiked(JsonNode? body) => body is JsonObject data && data["likedItemIds"] is JsonArray ids
        ? ids.Select(id => Guid.TryParse(id?.ToString(), out var parsed) ? parsed : Guid.Empty)
            .Where(id => id != Guid.Empty).Reverse().Distinct().Take(8).ToArray()
        : Array.Empty<Guid>();


    private sealed record CrestsCache(DateTime At, IReadOnlyList<CrestFixture> Fixtures);
}

/// <summary>Bounded viewer-scoped snapshots shared by both Home contracts.</summary>
internal sealed class HomeRowsCache
{
    private const int MaxBuilds = 12;
    private const int MaxCachedViewers = 500;
    private static readonly TimeSpan FreshAfter = TimeSpan.FromSeconds(5);
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<Guid, (DateTime At, Task<HomeSnapshot> Work)> _cache = new();
    private readonly object _lock = new();

    internal HomeRowsCache(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    internal Task<HomeSnapshot> GetAsync(Guid viewerId, bool fresh, Func<DateTime, Task<HomeSnapshot>> build)
    {
        var now = _utcNow();
        lock (_lock)
        {
            if (fresh && _cache.TryGetValue(viewerId, out var old) && old.Work.IsCompleted && now - old.At > FreshAfter)
            {
                _cache.Remove(viewerId);
            }

            if (_cache.TryGetValue(viewerId, out var entry) && (!entry.Work.IsCompleted || (now - entry.At < HomeRowsService.Lifetime && entry.Work.IsCompletedSuccessfully)))
            {
                return entry.Work;
            }

            foreach (var stale in _cache.Where(e => e.Value.Work.IsCompleted && now - e.Value.At >= HomeRowsService.Lifetime).Select(e => e.Key).ToList())
            {
                _cache.Remove(stale);
            }

            if (_cache.Count(e => !e.Value.Work.IsCompleted) >= MaxBuilds || _cache.Count >= MaxCachedViewers)
            {
                throw new HomeBusyException();
            }

            // The build is shared with other requests from this viewer, so one caller hanging up must not cancel it.
            var work = Task.Run(() => build(now), CancellationToken.None);
            _cache[viewerId] = (now, work);
            return work;
        }
    }

    internal void Forget(Guid viewerId)
    {
        lock (_lock)
        {
            _cache.Remove(viewerId);
        }
    }
}
