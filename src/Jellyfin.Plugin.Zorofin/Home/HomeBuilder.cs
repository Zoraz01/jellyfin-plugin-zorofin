using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Zorofin.Home;

/// <summary>Library reads for one viewer. Every read uses that viewer's access and user data.</summary>
public interface IHomeSource
{
    /// <summary>Gets the viewer's id.</summary>
    Guid ViewerId { get; }

    /// <summary>Movies and episodes with a resume position, most recently played first.</summary>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> Resumable(int limit);

    /// <summary>Jellyfin's Next Up episodes.</summary>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> NextUp(int limit);

    /// <summary>Movies, newest added first.</summary>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> Movies(int limit);

    /// <summary>Movies with a 4K source, newest first, with <see cref="HomeEntry.Formats"/> read.</summary>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> ShowcaseMovies(int limit);

    /// <summary>The viewer's favorite shows.</summary>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> FavoriteSeries(int limit);

    /// <summary>Unwatched episodes of these shows added since a time, newest first.</summary>
    /// <param name="seriesIds">Shows.</param>
    /// <param name="sinceUtc">Earliest arrival.</param>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> RecentEpisodes(IReadOnlyCollection<Guid> seriesIds, DateTime sinceUtc, int limit);

    /// <summary>Collections (box sets) the viewer can see.</summary>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> Collections(int limit);

    /// <summary>Movies in one collection.</summary>
    /// <param name="collectionId">Collection.</param>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> CollectionMovies(Guid collectionId, int limit);

    /// <summary>Most-watched movies and shows across the server this week, that the viewer can see. Null when statistics are off.</summary>
    /// <param name="days">Window.</param>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries in rank order, or null.</returns>
    IReadOnlyList<HomeEntry>? Popular(int days, int limit);

    /// <summary>Sports broadcasts in the guide between two times.</summary>
    /// <param name="fromUtc">Earliest end.</param>
    /// <param name="untilUtc">Latest start.</param>
    /// <param name="limit">Most to return.</param>
    /// <returns>Programmes.</returns>
    IReadOnlyList<GuideProgram> SportsPrograms(DateTime fromUtc, DateTime untilUtc, int limit);

    /// <summary>Channels the viewer can open.</summary>
    /// <param name="ids">Channel ids.</param>
    /// <returns>Channels by id.</returns>
    IReadOnlyDictionary<Guid, HomeEntry> Channels(IEnumerable<Guid> ids);

    /// <summary>The viewer's followed teams.</summary>
    /// <returns>Team names.</returns>
    IReadOnlyList<string> Teams();

    /// <summary>The viewer's favorite movies and shows.</summary>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> Favorites(int limit);

    /// <summary>Newest movies ("Movie") or shows with new episodes ("Series").</summary>
    /// <param name="kind">Movie or Series.</param>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> RecentlyAdded(string kind, int limit);

    /// <summary>Movies and shows the viewer finished, most recently played first.</summary>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> WatchAgain(int limit);

    /// <summary>Movies and shows the viewer watched lately (an episode counts for its show), most recent first.</summary>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> RecentlyWatched(int limit);

    /// <summary>Movies or shows (same kind as the seed) sharing a genre with it.</summary>
    /// <param name="seed">Seed title.</param>
    /// <param name="limit">Most to return.</param>
    /// <returns>Entries.</returns>
    IReadOnlyList<HomeEntry> SharingGenres(HomeEntry seed, int limit);

    /// <summary>The viewer's own Seerr requests that are on the server. Null when Requests is off or the viewer has no Seerr account.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matches with their arrival time, or null.</returns>
    Task<IReadOnlyList<(HomeEntry Entry, DateTime? Arrived)>?> AvailableRequestsAsync(CancellationToken cancellationToken);
}

/// <summary>Builds every Zorofin Home row from one viewer's library reads.</summary>
public static class HomeBuilder
{
    /// <summary>Rows in the apps' homeRows version 1 contract, in its order.</summary>
    public static readonly IReadOnlyList<string> AppRowIds = new[] { "requests-ready", "new-episodes", "short-movies", "showcase", "curated", "popular", "sports" };

    /// <summary>Every row, in a stable order: pinned rows, then personal rows, then library rows.</summary>
    public static readonly IReadOnlyList<(string Id, string Title, HomeRowPlacement Placement)> Catalog = new[]
    {
        ("continue-watching", "Continue Watching", HomeRowPlacement.Pinned),
        ("requests-ready", "My requests", HomeRowPlacement.Pinned),
        ("new-episodes", "New episodes for you", HomeRowPlacement.Personal),
        ("short-movies", "Watch in 90 minutes", HomeRowPlacement.Personal),
        ("showcase", "4K / HDR showcase", HomeRowPlacement.Personal),
        ("curated", "Seasonal picks", HomeRowPlacement.Personal),
        ("popular", "Popular this week", HomeRowPlacement.Personal),
        ("sports", "Your teams / starting soon", HomeRowPlacement.Personal),
        ("because-you-watched", "Because you watched", HomeRowPlacement.Personal),
        ("favorites", "Favorites", HomeRowPlacement.Library),
        ("recently-added-movies", "Recently added movies", HomeRowPlacement.Library),
        ("recently-added-shows", "Recently added shows", HomeRowPlacement.Library),
        ("watch-again", "Watch again", HomeRowPlacement.Library),
    };

    /// <summary>Builds the rows. A row that fails is marked unavailable; it never takes Home down with it.</summary>
    /// <param name="source">Viewer's library reads.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="zone">Calendar time zone for the seasonal row.</param>
    /// <param name="wanted">Row ids to build; others are skipped.</param>
    /// <param name="events">Operator's dated curated calendar.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows in catalog order. "because-you-watched" can appear up to twice, as because-you-watched-1 and -2.</returns>
    public static async Task<IReadOnlyList<HomeRowResult>> BuildAsync(IHomeSource source, DateTime nowUtc, TimeZoneInfo zone, IReadOnlySet<string> wanted, IReadOnlyList<CuratedSeasons.CalendarEvent>? events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(wanted);
        IReadOnlyList<HomeEntry>? movies = null;
        IReadOnlyList<HomeEntry> Movies() => movies ??= source.Movies(2000).Where(m => m.Playable).ToList();
        var rows = new List<HomeRowResult>();
        foreach (var (id, title, placement) in Catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!wanted.Contains(id))
            {
                continue;
            }

            try
            {
                switch (id)
                {
                    case "continue-watching":
                        rows.Add(Row(id, title, placement, HomeRules.ContinueWatching(source.Resumable(40), source.NextUp(40))));
                        break;
                    case "requests-ready":
                        var requests = await source.AvailableRequestsAsync(cancellationToken).ConfigureAwait(false);
                        rows.Add(requests is null ? Unavailable(id, title, placement) : Row(id, title, placement, HomeRules.MyRequests(requests)));
                        break;
                    case "new-episodes":
                        var favorites = source.FavoriteSeries(100).Select(s => s.Id).ToHashSet();
                        var episodes = favorites.Count == 0 ? Array.Empty<HomeEntry>() : source.RecentEpisodes(favorites, nowUtc.AddDays(-HomeRules.RecentDays), 200);
                        rows.Add(Row(id, title, placement, HomeRules.NewEpisodes(episodes, favorites, nowUtc)));
                        break;
                    case "short-movies":
                        rows.Add(Row(id, title, placement, HomeRules.ShortMovies(Movies())));
                        break;
                    case "showcase":
                        rows.Add(Row(id, title, placement, HomeRules.Showcase(source.ShowcaseMovies(60))));
                        break;
                    case "curated":
                        var (themeTitle, picks) = Curated(source, Movies(), CuratedSeasons.CalendarDay(nowUtc, zone), events);
                        rows.Add(Row(id, themeTitle, placement, picks));
                        break;
                    case "popular":
                        var popular = source.Popular(7, HomeRules.CardLimit);
                        rows.Add(popular is null ? Unavailable(id, title, placement)
                            : Row(id, title, placement, popular.Where(p => p.Playable).Take(HomeRules.CardLimit)
                                .Select((p, n) => new HomeCard(p.Id, p, "#" + (n + 1) + " this week", new[] { "#" + (n + 1) })).ToList()));
                        break;
                    case "sports":
                        var programs = source.SportsPrograms(nowUtc, nowUtc + HomeRules.SportsWindow, 1500);
                        var channels = source.Channels(programs.Select(p => p.ChannelId).Distinct());
                        rows.Add(Row(id, title, placement, HomeRules.Sports(programs, channels, source.Teams(), nowUtc)));
                        break;
                    case "because-you-watched":
                        var n = 0;
                        foreach (var seed in source.RecentlyWatched(8).Where(s => s.Genres.Count > 0).Take(2))
                        {
                            n++;
                            rows.Add(Row(id + "-" + n, title + " " + seed.Name, placement, HomeRules.Similar(seed, source.SharingGenres(seed, 100))));
                        }

                        break;
                    case "favorites":
                        rows.Add(Row(id, title, placement, HomeRules.Shelf(source.Favorites(40))));
                        break;
                    case "recently-added-movies":
                        rows.Add(Row(id, title, placement, HomeRules.Shelf(source.RecentlyAdded("Movie", 40))));
                        break;
                    case "recently-added-shows":
                        rows.Add(Row(id, title, placement, HomeRules.Shelf(source.RecentlyAdded("Series", 40))));
                        break;
                    case "watch-again":
                        rows.Add(Row(id, title, placement, HomeRules.Shelf(source.WatchAgain(40))));
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                rows.Add(Unavailable(id, title, placement));
            }
        }

        return rows;
    }

    /// <summary>The seasonal row: the first theme with picks, from matching collections, else from item metadata.</summary>
    /// <param name="source">Viewer's library reads.</param>
    /// <param name="movies">Movie catalogue.</param>
    /// <param name="today">Calendar day.</param>
    /// <param name="events">Operator's dated calendar.</param>
    /// <returns>Row title and cards.</returns>
    public static (string Title, IReadOnlyList<HomeCard> Cards) Curated(IHomeSource source, IReadOnlyList<HomeEntry> movies, DateTime today, IReadOnlyList<CuratedSeasons.CalendarEvent>? events)
    {
        ArgumentNullException.ThrowIfNull(source);
        IReadOnlyList<HomeEntry>? collections = null;
        var children = new Dictionary<Guid, IReadOnlyList<HomeEntry>>();
        var remaining = 6;
        foreach (var theme in CuratedSeasons.ThemePlan(today, events))
        {
            var rule = CuratedSeasons.Themes[theme];
            var picks = new List<HomeEntry>();
            collections ??= source.Collections(200);
            foreach (var collection in collections.Where(c => rule.Collections.IsMatch(c.Name)).Take(2))
            {
                if (!children.TryGetValue(collection.Id, out var list))
                {
                    if (remaining == 0)
                    {
                        break;
                    }

                    remaining--;
                    list = source.CollectionMovies(collection.Id, 40);
                    children[collection.Id] = list;
                }

                picks.AddRange(list.Where(m => m.Playable));
            }

            // Metadata fills only an empty themed selection; a missing holiday falls through to the season or a mood.
            if (picks.Count == 0)
            {
                picks.AddRange(movies.Where(m => m.Playable && CuratedSeasons.MetadataMatches(m, theme)));
            }

            if (picks.Count > 0)
            {
                return (rule.Title, CuratedSeasons.RankPicks(picks, theme, source.ViewerId, today)
                    .Select(m => new HomeCard(m.Id, m, rule.Title, Array.Empty<string>())).ToList());
            }
        }

        return ("Seasonal picks", Array.Empty<HomeCard>());
    }

    private static HomeRowResult Row(string id, string title, HomeRowPlacement placement, IReadOnlyList<HomeCard> cards) =>
        new(id, title, placement, cards.Count > 0 ? "ready" : "empty", cards);

    private static HomeRowResult Unavailable(string id, string title, HomeRowPlacement placement) =>
        new(id, title, placement, "unavailable", Array.Empty<HomeCard>());
}
