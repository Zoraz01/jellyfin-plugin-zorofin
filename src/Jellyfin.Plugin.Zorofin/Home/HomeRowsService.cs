using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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

    private const int MaxBuilds = 12;
    private static readonly TimeSpan FreshAfter = TimeSpan.FromSeconds(5);
    private const int MaxCachedViewers = 500;
    private readonly HomeServices _services;
    private readonly Dictionary<Guid, (DateTime At, Task<HomeSnapshot> Work)> _cache = new();
    private readonly object _lock = new();

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
    /// <param name="seerrUsers">Seerr user map.</param>
    /// <param name="upstream">Seerr client.</param>
    public HomeRowsService(ILibraryManager library, IUserDataManager userData, ITVSeriesManager tv, IDtoService dto, IDisplayPreferencesManager preferences, ILiveTvManager liveTv, PlayStatsStore stats, SeerrUserDirectory seerrUsers, UpstreamClient upstream)
    {
        _services = new HomeServices(library, userData, tv, dto, preferences, liveTv, stats, seerrUsers, upstream);
    }

    /// <summary>The rows this server builds: every catalog row the operator didn't switch off, and only rows whose module is on.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>Row ids.</returns>
    public static IReadOnlySet<string> WantedRows(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var off = (config.HomeRowsOff ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var wanted = HomeBuilder.Catalog.Select(r => r.Id).Except(off, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (!config.PlayStatsEnabled)
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
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (fresh && _cache.TryGetValue(user.Id, out var old) && old.Work.IsCompleted && now - old.At > FreshAfter)
            {
                _cache.Remove(user.Id);
            }

            if (_cache.TryGetValue(user.Id, out var entry) && (!entry.Work.IsCompleted || (now - entry.At < Lifetime && entry.Work.IsCompletedSuccessfully)))
            {
                return entry.Work;
            }

            foreach (var stale in _cache.Where(e => e.Value.Work.IsCompleted && now - e.Value.At >= Lifetime).Select(e => e.Key).ToList())
            {
                _cache.Remove(stale);
            }

            if (_cache.Count(e => !e.Value.Work.IsCompleted) >= MaxBuilds || _cache.Count >= MaxCachedViewers)
            {
                throw new HomeBusyException();
            }

            // The build is shared with other requests from this viewer, so one caller hanging up must not cancel it.
            var work = Task.Run(() => BuildAsync(user, config, now, CancellationToken.None), CancellationToken.None);
            _cache[user.Id] = (now, work);
            return work;
        }
    }

    /// <summary>Forgets a viewer's cached Home, for example after they change their teams.</summary>
    /// <param name="userId">Viewer.</param>
    public void Forget(Guid userId)
    {
        lock (_lock)
        {
            _cache.Remove(userId);
        }
    }

    private async Task<HomeSnapshot> BuildAsync(User user, PluginConfiguration config, DateTime now, CancellationToken cancellationToken)
    {
        var source = new JellyfinHomeSource(user, _services, config);
        var events = CuratedSeasons.ParseEvents(config.HomeCuratedCalendar);
        var rows = await HomeBuilder.BuildAsync(source, now, TimeZoneInfo.Local, WantedRows(config), events, cancellationToken).ConfigureAwait(false);
        var items = source.Describe(rows.SelectMany(r => r.Cards).Select(c => c.Entry.Id));
        return new HomeSnapshot(now, rows, items, source.Teams());
    }
}
