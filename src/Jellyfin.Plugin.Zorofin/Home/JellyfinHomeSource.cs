using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Zorofin.Configuration;
using Jellyfin.Plugin.Zorofin.Requests;
using Jellyfin.Plugin.Zorofin.Stats;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Zorofin.Home;

/// <summary>Reads Home row data from Jellyfin as one viewer: their libraries, parental limits and user data.</summary>
public sealed class JellyfinHomeSource : IHomeSource
{
    /// <summary>Where the apps and web keep followed teams: Jellyfin display preferences "zorofin-home", client "zorofin".</summary>
    public const string TeamsPreferences = "zorofin-home";

    /// <summary>The display preferences client.</summary>
    public const string TeamsClient = "zorofin";

    /// <summary>The custom preference key holding a JSON array of team names.</summary>
    public const string TeamsKey = "zorofinTeams";

    // Only these item fields reach clients: no paths, stream URLs, provider internals or other users' data.
    private static readonly string[] ItemKeys =
    {
        "Id", "Name", "Type", "Overview", "ProductionYear", "OfficialRating", "RunTimeTicks", "CommunityRating", "ImageTags",
        "BackdropImageTags", "ParentBackdropItemId", "ParentBackdropImageTags", "ParentThumbItemId", "ParentThumbImageTag",
        "SeriesId", "SeriesName", "SeriesPrimaryImageTag", "SeasonId", "IndexNumber", "ParentIndexNumber", "Genres", "ChannelNumber",
        "PrimaryImageAspectRatio",
    };

    private static readonly string[] UserDataKeys = { "PlaybackPositionTicks", "PlayedPercentage", "IsFavorite", "Played" };

    private readonly User _user;
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly ITVSeriesManager _tv;
    private readonly IDtoService _dto;
    private readonly IDisplayPreferencesManager _preferences;
    private readonly ILiveTvManager _liveTv;
    private readonly PlayStatsStore _stats;
    private readonly PlayHistoryStore _history;
    private readonly SeerrUserDirectory _seerrUsers;
    private readonly UpstreamClient _upstream;
    private readonly PluginConfiguration _config;
    private readonly ContinueWatchingHides? _hides;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinHomeSource"/> class.
    /// </summary>
    /// <param name="user">Viewer.</param>
    /// <param name="services">Jellyfin and plugin services.</param>
    /// <param name="config">Plugin configuration.</param>
    public JellyfinHomeSource(User user, HomeServices services, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        _user = user;
        _library = services.Library;
        _userData = services.UserData;
        _tv = services.Tv;
        _dto = services.Dto;
        _preferences = services.Preferences;
        _liveTv = services.LiveTv;
        _hides = services.Hides;
        _stats = services.Stats;
        _history = services.History;
        _seerrUsers = services.SeerrUsers;
        _upstream = services.Upstream;
        _config = config;
    }

    /// <inheritdoc />
    public Guid ViewerId => _user.Id;

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> Resumable(int limit) => Entries(Query(q =>
    {
        q.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode };
        q.IsResumable = true;
        q.OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) };
        q.Limit = limit;
    }));

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> NextUp(int limit)
    {
        var query = new NextUpQuery
        {
            User = _user,
            Limit = limit,
            NextUpDateCutoff = DateTime.UtcNow.AddDays(-365),
            EnableResumable = false,
            EnableRewatching = false,
        };
        return Entries(_tv.GetNextUp(query, new DtoOptions(false)).Items);
    }

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> Movies(int limit) => Entries(Query(q =>
    {
        q.IncludeItemTypes = new[] { BaseItemKind.Movie };
        q.OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) };
        q.Limit = limit;
    }));

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> ShowcaseMovies(int limit) => Entries(Query(q =>
    {
        q.IncludeItemTypes = new[] { BaseItemKind.Movie };
        q.MinWidth = 3800;
        q.OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) };
        q.Limit = limit;
    }), formats: true);

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> FavoriteSeries(int limit) => Entries(Query(q =>
    {
        q.IncludeItemTypes = new[] { BaseItemKind.Series };
        q.IsFavorite = true;
        q.Limit = limit;
    }));

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> RecentEpisodes(IReadOnlyCollection<Guid> seriesIds, DateTime sinceUtc, int limit) => Entries(Query(q =>
    {
        q.IncludeItemTypes = new[] { BaseItemKind.Episode };
        q.AncestorIds = seriesIds.ToArray();
        q.IsPlayed = false;
        q.MinDateCreated = sinceUtc;
        q.OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) };
        q.Limit = limit;
    }));

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> Collections(int limit) => Entries(Query(q =>
    {
        q.IncludeItemTypes = new[] { BaseItemKind.BoxSet };
        q.Limit = limit;
    }));

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> CollectionMovies(Guid collectionId, int limit)
    {
        if (_library.GetItemById(collectionId) is not Folder collection || !collection.IsVisibleStandalone(_user))
        {
            return Array.Empty<HomeEntry>();
        }

        var children = collection.GetChildren(_user, true).OfType<MediaBrowser.Controller.Entities.Movies.Movie>().Take(limit).Cast<BaseItem>().ToList();
        return Entries(children);
    }

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry>? Popular(int days, int limit)
    {
        // Play history (with imported Playback Reporting plays) ranks the row when it's on; the anonymous
        // totals only start counting when they're switched on, so they're the fallback.
        IEnumerable<Guid> ranked;
        if (_config.PlayHistoryEnabled)
        {
            var now = DateTime.UtcNow;
            ranked = HomeRules.PopularTitles(_history.Between(now.AddDays(-days), now));
        }
        else if (_config.PlayStatsEnabled)
        {
            ranked = _stats.Top(days, DateTime.UtcNow).Select(t => t.TitleId);
        }
        else
        {
            return null;
        }

        var items = ranked
            .Select(id => _library.GetItemById(id))
            .Where(i => i is not null && i.IsVisibleStandalone(_user))
            .Take(limit).Cast<BaseItem>().ToList();
        return Entries(items);
    }

    /// <inheritdoc />
    public IReadOnlyList<GuideProgram> SportsPrograms(DateTime fromUtc, DateTime untilUtc, int limit)
    {
        var folder = _liveTv.GetInternalLiveTvFolder(CancellationToken.None);
        var query = new InternalItemsQuery(_user)
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
            TopParentIds = new[] { folder.Id },
            IsSports = true,
            MinEndDate = fromUtc,
            MaxStartDate = untilUtc,
            OrderBy = new[] { (ItemSortBy.StartDate, SortOrder.Ascending) },
            Limit = limit,
            DtoOptions = new DtoOptions(false) { EnableImages = false },
        };
        return _library.GetItemList(query).OfType<LiveTvProgram>()
            .Where(p => p.EndDate is not null)
            .Select(p => new GuideProgram(p.Id, p.ChannelId, p.Name ?? string.Empty, p.Overview, Utc(p.StartDate), Utc(p.EndDate!.Value), p.IsSports, p.Genres ?? Array.Empty<string>()))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, HomeEntry> Channels(IEnumerable<Guid> ids)
    {
        var channels = ids.Take(500).Select(id => _library.GetItemById(id)).Where(c => c is not null && c.IsVisibleStandalone(_user)).Cast<BaseItem>().ToList();
        return Entries(channels).ToDictionary(c => c.Id);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Teams()
    {
        try
        {
            var prefs = _preferences.ListCustomItemDisplayPreferences(_user.Id, TeamsPreferences.GetMD5(), TeamsClient);
            return prefs.TryGetValue(TeamsKey, out var json) ? HomeRules.ParseTeams(json) : Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> Favorites(int limit) => Entries(Query(q =>
    {
        q.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series };
        q.IsFavorite = true;
        q.OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) };
        q.Limit = limit;
    }));

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> RecentlyAdded(string kind, int limit) => Entries(Query(q =>
    {
        var series = kind == "Series";
        q.IncludeItemTypes = new[] { series ? BaseItemKind.Series : BaseItemKind.Movie };
        q.OrderBy = new[] { (series ? ItemSortBy.DateLastContentAdded : ItemSortBy.DateCreated, SortOrder.Descending) };
        q.Limit = limit;
    }));

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> WatchAgain(int limit) => Entries(Query(q =>
    {
        q.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series };
        q.IsPlayed = true;
        q.OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) };
        q.Limit = limit;
    }));

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> RecentlyWatched(int limit)
    {
        var played = Query(q =>
        {
            q.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode };
            q.IsPlayed = true;
            q.OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) };
            q.Limit = 40;
        });
        var titles = new List<BaseItem>();
        foreach (var item in played)
        {
            var title = item is Episode episode ? episode.Series : item;
            if (title is not null && title.IsVisibleStandalone(_user) && titles.All(t => t.Id != title.Id))
            {
                titles.Add(title);
            }

            if (titles.Count >= limit)
            {
                break;
            }
        }

        return Entries(titles);
    }

    /// <inheritdoc />
    public IReadOnlyList<HomeEntry> SharingGenres(HomeEntry seed, int limit)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return Entries(Query(q =>
        {
            q.IncludeItemTypes = new[] { seed.Kind == "Series" ? BaseItemKind.Series : BaseItemKind.Movie };
            q.Genres = seed.Genres.Take(4).ToArray();
            q.ExcludeItemIds = new[] { seed.Id };
            q.OrderBy = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) };
            q.Limit = limit;
        }));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(HomeEntry Entry, DateTime? Arrived)>?> AvailableRequestsAsync(CancellationToken cancellationToken)
    {
        if (!_config.RequestsEnabled || RequestsService.Seerr(_config) is not { } seerr)
        {
            return null;
        }

        var seerrUser = await _seerrUsers.FindAsync(seerr, _user.Id, cancellationToken).ConfigureAwait(false);
        if (seerrUser is not { } id)
        {
            return null;
        }

        var path = string.Create(CultureInfo.InvariantCulture, $"api/v1/request?take=100&skip=0&filter=all&sort=added&requestedBy={id}");
        var result = await _upstream.SendAsync(seerr, HttpMethod.Get, path, null, id, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            throw new UpstreamUnavailableException();
        }

        var matches = new List<(HomeEntry, DateTime?)>();
        var available = ReadAvailable(result.Body);
        // One library read, matched in memory: Jellyfin 12.4 ignores HasAnyProviderId on item queries, so a
        // per-request provider-id query with a small limit returned unrelated items and never matched.
        // Jellyfin only loads provider ids when the query asks for that field, so ask for it.
        var index = available.Count == 0 ? null : IndexByTmdb(Query(q =>
        {
            q.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series };
            q.DtoOptions = WithProviderIds;
        }));
        foreach (var request in available)
        {
            var key = (request.Type == "movie" ? BaseItemKind.Movie : BaseItemKind.Series, request.TmdbId.ToString(CultureInfo.InvariantCulture));
            if (index is null || !index.TryGetValue(key, out var found))
            {
                continue;
            }

            foreach (var entry in Entries(found, formats: request.Is4K && request.Type == "movie"))
            {
                if (!request.Is4K || request.Type != "movie" || entry.Formats.Contains("4K"))
                {
                    matches.Add((entry, request.AddedAt));
                }
            }

            if (matches.Count >= HomeRules.CardLimit * 2)
            {
                break;
            }
        }

        return matches;
    }

    /// <summary>Groups movies and shows by their TMDB id, for matching Seerr requests to library items.</summary>
    /// <param name="items">Movies and series.</param>
    /// <returns>Items per (kind, TMDB id).</returns>
    public static Dictionary<(BaseItemKind Kind, string Tmdb), List<BaseItem>> IndexByTmdb(IEnumerable<BaseItem> items)
    {
        var index = new Dictionary<(BaseItemKind, string), List<BaseItem>>();
        foreach (var item in items)
        {
            var kind = item is Series ? BaseItemKind.Series : item is Movie ? BaseItemKind.Movie : (BaseItemKind?)null;
            if (kind is null || item.ProviderIds is null || !item.ProviderIds.TryGetValue("Tmdb", out var tmdb) || string.IsNullOrEmpty(tmdb))
            {
                continue;
            }

            if (!index.TryGetValue((kind.Value, tmdb), out var list))
            {
                index[(kind.Value, tmdb)] = list = new List<BaseItem>();
            }

            list.Add(item);
        }

        return index;
    }

    /// <summary>The viewer's requests Seerr marks as available or partly available (status 4 or 5).</summary>
    /// <param name="page">Seerr request page.</param>
    /// <returns>Requests.</returns>
    public static IReadOnlyList<AvailableRequest> ReadAvailable(JsonNode? page)
    {
        var list = new List<AvailableRequest>();
        foreach (var r in page?["results"] as JsonArray ?? new JsonArray())
        {
            var type = RequestShaper.Str(r?["type"]);
            var is4k = r?["is4k"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            var media = r?["media"];
            var status = RequestShaper.Int(is4k ? media?["status4k"] : media?["status"]);
            var tmdb = RequestShaper.Int(media?["tmdbId"]);
            if (type is not ("movie" or "tv") || status is not (4 or 5) || tmdb is not > 0)
            {
                continue;
            }

            var seasons = (r?["seasons"] as JsonArray ?? new JsonArray()).Select(s => RequestShaper.Int(s?["seasonNumber"])).OfType<int>().ToList();
            DateTime? added = DateTime.TryParse(RequestShaper.Str(media?["mediaAddedAt"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : null;
            list.Add(new AvailableRequest(type, tmdb.Value, is4k, seasons, added));
        }

        return list;
    }

    /// <summary>Client-safe item JSON for the cards, in Jellyfin's own field names, with the viewer's user data.</summary>
    /// <param name="ids">Item ids on the cards.</param>
    /// <returns>Item JSON by id.</returns>
    public IReadOnlyDictionary<Guid, JsonObject> Describe(IEnumerable<Guid> ids)
    {
        var items = ids.Distinct().Select(id => _library.GetItemById(id)).Where(i => i is not null).Cast<BaseItem>().ToList();
        var options = new DtoOptions(false)
        {
            Fields = new[] { ItemFields.Overview, ItemFields.Genres, ItemFields.PrimaryImageAspectRatio },
            EnableImages = true,
            ImageTypeLimit = 1,
            EnableUserData = true,
        };
        var result = new Dictionary<Guid, JsonObject>();
        foreach (var dto in _dto.GetBaseItemDtos(items, options, _user))
        {
            if (JsonSerializer.SerializeToNode(dto, JsonDefaults.PascalCaseOptions) is JsonObject full)
            {
                result[dto.Id] = Sanitize(full);
            }
        }

        return result;
    }

    /// <summary>Keeps only the fields cards use.</summary>
    /// <param name="full">Jellyfin item JSON.</param>
    /// <returns>Reduced JSON.</returns>
    public static JsonObject Sanitize(JsonObject full)
    {
        ArgumentNullException.ThrowIfNull(full);
        var item = new JsonObject();
        foreach (var key in ItemKeys)
        {
            if (full[key] is { } value)
            {
                item[key] = value.DeepClone();
            }
        }

        if (full["UserData"] is JsonObject data)
        {
            var user = new JsonObject();
            foreach (var key in UserDataKeys)
            {
                if (data[key] is { } value)
                {
                    user[key] = value.DeepClone();
                }
            }

            item["UserData"] = user;
        }

        return item;
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    /// <inheritdoc />
    public bool HiddenFromContinueWatching(HomeEntry entry) => _hides is not null && _hides.IsHidden(_user.Id, entry);

    private static bool Playable(BaseItem item) =>
        !item.IsVirtualItem && item.LocationType != LocationType.Virtual && (item is not Video video || !video.IsPlaceHolder);

    private static readonly DtoOptions WithProviderIds = new(false) { EnableImages = false, Fields = new[] { ItemFields.ProviderIds } };

    private IReadOnlyList<BaseItem> Query(Action<InternalItemsQuery> setup)
    {
        var query = new InternalItemsQuery(_user)
        {
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false) { EnableImages = false },
        };
        setup(query);
        return _library.GetItemList(query);
    }

    private List<HomeEntry> Entries(IReadOnlyList<BaseItem> items, bool formats = false)
    {
        var list = new List<HomeEntry>(items.Count);
        foreach (var item in items)
        {
            var data = _userData.GetUserData(_user, item);
            var episode = item as Episode;
            list.Add(new HomeEntry
            {
                Id = item.Id,
                Name = item.Name ?? string.Empty,
                Kind = item.GetBaseItemKind().ToString(),
                Overview = item.Overview,
                Genres = item.Genres ?? Array.Empty<string>(),
                Tags = item.Tags ?? Array.Empty<string>(),
                RunTimeTicks = item.RunTimeTicks,
                DateCreated = Utc(item.DateCreated),
                Played = data?.Played ?? false,
                PositionTicks = data?.PlaybackPositionTicks ?? 0,
                LastPlayed = data?.LastPlayedDate is { } last ? Utc(last) : null,
                Favorite = data?.IsFavorite ?? false,
                SeriesId = episode?.SeriesId,
                SeriesName = episode?.SeriesName,
                Playable = Playable(item),
                CommunityRating = item.CommunityRating,
                Formats = formats && item is Video video
                    ? HomeRules.Formats(video.GetMediaStreams().Where(s => s.Type == MediaStreamType.Video).Select(s => (s.Width, (string?)s.VideoRangeType.ToString(), (string?)s.VideoRange.ToString())))
                    : Array.Empty<string>(),
            });
        }

        return list;
    }
}

/// <summary>Jellyfin and plugin services the Home source reads from.</summary>
/// <param name="Library">Library manager.</param>
/// <param name="UserData">User data manager.</param>
/// <param name="Tv">Next Up.</param>
/// <param name="Dto">Item DTOs.</param>
/// <param name="Preferences">Display preferences (followed teams).</param>
/// <param name="LiveTv">Live TV.</param>
/// <param name="Stats">Popular-this-week totals.</param>
/// <param name="History">Play history.</param>
/// <param name="SeerrUsers">Jellyfin to Seerr user map.</param>
/// <param name="Upstream">Seerr client.</param>
/// <param name="Hides">Titles viewers removed from Continue Watching.</param>
public sealed record HomeServices(
    ILibraryManager Library,
    IUserDataManager UserData,
    ITVSeriesManager Tv,
    IDtoService Dto,
    IDisplayPreferencesManager Preferences,
    ILiveTvManager LiveTv,
    PlayStatsStore Stats,
    PlayHistoryStore History,
    SeerrUserDirectory SeerrUsers,
    UpstreamClient Upstream,
    ContinueWatchingHides? Hides = null);
