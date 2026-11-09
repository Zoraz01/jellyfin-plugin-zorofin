using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Capabilities;
using Jellyfin.Plugin.Zorofin.Configuration;
using Jellyfin.Plugin.Zorofin.Home;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class HomeRowsTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static HomeEntry Movie(string name, int minutes = 120, bool played = false, int addedDaysAgo = 1, params string[] genres) => new()
    {
        Id = Guid.NewGuid(), Name = name, Kind = "Movie", RunTimeTicks = minutes * 600_000_000L, Played = played,
        DateCreated = Now.AddDays(-addedDaysAgo), Genres = genres,
    };

    private static HomeEntry Episode(Guid series, string name, int addedDaysAgo = 1, long position = 0, bool played = false, DateTime? lastPlayed = null) => new()
    {
        Id = Guid.NewGuid(), Name = name, Kind = "Episode", SeriesId = series, SeriesName = "Show", DateCreated = Now.AddDays(-addedDaysAgo),
        PositionTicks = position, Played = played, LastPlayed = lastPlayed, RunTimeTicks = 30 * 600_000_000L,
    };

    [Fact]
    public void ContinueWatchingKeepsOneCardPerShowAndPrefersTheUnfinishedEpisode()
    {
        var show = Guid.NewGuid();
        var unfinished = Episode(show, "E5", position: 1000, lastPlayed: Now.AddHours(-1));
        var next = Episode(show, "E6");
        var movie = Movie("Film") with { PositionTicks = 5 };
        var cards = HomeRules.ContinueWatching(new[] { movie, unfinished }, new[] { next });
        Assert.Equal(new[] { movie.Id, unfinished.Id }, cards.Select(c => c.Id));
        Assert.Equal("Resume", cards[1].Subtitle);

        // A finished rewatch does not hide the server's next unwatched episode.
        var finished = Episode(show, "E5", position: 30 * 600_000_000L, played: true);
        cards = HomeRules.ContinueWatching(new[] { finished }, new[] { next });
        Assert.Equal(next.Id, Assert.Single(cards).Id);
        Assert.Equal("Next up", cards[0].Subtitle);
    }

    [Fact]
    public void NewEpisodesAreRecentUnwatchedFavoritesNewestFirst()
    {
        var fav = Guid.NewGuid();
        var other = Guid.NewGuid();
        var newest = Episode(fav, "new", addedDaysAgo: 1);
        var older = Episode(fav, "older", addedDaysAgo: 5);
        var cards = HomeRules.NewEpisodes(
            new[] { older, newest, Episode(fav, "old", addedDaysAgo: 20), Episode(other, "not followed"), Episode(fav, "seen", played: true) },
            new HashSet<Guid> { fav }, Now);
        Assert.Equal(new[] { newest.Id, older.Id }, cards.Select(c => c.Id));
        Assert.Equal("New episode", cards[0].Badges.Single());
    }

    [Fact]
    public void ShortMoviesAndShowcaseFollowTheAppRules()
    {
        var shortOne = Movie("Short", 85);
        var cards = HomeRules.ShortMovies(new[] { shortOne, Movie("Long", 91), Movie("Seen", 80, played: true), Movie("Unknown", 0) });
        Assert.Equal("85 min", Assert.Single(cards).Subtitle);

        Assert.Equal(new[] { "4K", "Dolby Vision", "HDR10" }, HomeRules.Formats(new (int?, string?, string?)[] { (3840, "DOVIWithHDR10", "HDR"), (1920, "HDR10", "HDR"), (1920, "SDR", "SDR") }));
        Assert.Empty(HomeRules.Formats(new (int?, string?, string?)[] { (1920, "SDR", "SDR") }));
        var uhd = Movie("UHD") with { Formats = new[] { "4K" } };
        Assert.Equal(uhd.Id, Assert.Single(HomeRules.Showcase(new[] { Movie("HD"), uhd })).Id);
    }

    [Fact]
    public void SportsFiltersToTeamsAndHidesReplays()
    {
        var channel = new HomeEntry { Id = Guid.NewGuid(), Name = "Sports 1", Kind = "TvChannel" };
        var channels = new Dictionary<Guid, HomeEntry> { [channel.Id] = channel };
        GuideProgram P(string name, double startHours, bool sports = true) =>
            new(Guid.NewGuid(), channel.Id, name, null, Now.AddHours(startHours), Now.AddHours(startHours + 3), sports, Array.Empty<string>());
        var live = P("New York Knicks at Boston Celtics", -1);
        var soon = P("Chicago Bulls at New York Knicks", 2);
        var programs = new[] { soon, live, P("Knicks classic replay", 1), P("New York Knicks talk", 1), P("Lakers at Suns", 1), P("Late game", 13), P("Cooking", 1, false) };

        var all = HomeRules.Sports(programs, channels, Array.Empty<string>(), Now);
        Assert.Equal(new[] { live.Id, programs[4].Id, soon.Id }, all.Select(c => c.Id));
        Assert.Equal("On now", all[0].Badges.Single());

        var mine = HomeRules.Sports(programs, channels, new[] { "New York Knicks" }, Now);
        Assert.Equal(new[] { live.Id, soon.Id }, mine.Select(c => c.Id));
        Assert.Equal("Starting soon", mine[1].Badges.Single());
        Assert.Equal(channel.Id, mine[1].Entry.Id);
        Assert.False(HomeRules.TeamMatches("New York Knicksville", new[] { "New York Knicks" }));
    }

    [Fact]
    public void TeamsComeFromTheSharedDisplayPreference()
    {
        Assert.Equal(new[] { "New York Knicks", "Cubs" }, HomeRules.ParseTeams("[\" New York Knicks \",\"Cubs\",\"cubs\",\"NY\"]"));
        Assert.Empty(HomeRules.ParseTeams("not json"));
        Assert.Empty(HomeRules.ParseTeams(null));
    }

    [Fact]
    public void MyRequestsPutsUnwatchedArrivalsFirst()
    {
        var watched = Movie("Watched", played: true);
        var older = Movie("Older");
        var newer = Movie("Newer");
        var cards = HomeRules.MyRequests(new (HomeEntry, DateTime?)[] { (watched, Now), (older, Now.AddDays(-3)), (newer, Now.AddDays(-1)), (newer, Now) });
        Assert.Equal(new[] { newer.Id, older.Id, watched.Id }, cards.Select(c => c.Id));
        Assert.Equal("Ready", cards[0].Badges.Single());
        Assert.Empty(cards[2].Badges);
    }

    [Fact]
    public void ReadsOnlyAvailableRequestsFromSeerr()
    {
        var page = JsonNode.Parse("""
            {"results":[
              {"type":"movie","is4k":false,"media":{"tmdbId":10,"status":5,"mediaAddedAt":"2026-11-01T10:00:00.000Z"}},
              {"type":"movie","is4k":true,"media":{"tmdbId":11,"status":5,"status4k":3}},
              {"type":"tv","is4k":false,"media":{"tmdbId":12,"status":4},"seasons":[{"seasonNumber":1},{"seasonNumber":2}]},
              {"type":"person","media":{"tmdbId":13,"status":5}},
              {"type":"movie","media":{"tmdbId":14,"status":2}}]}
            """);
        var list = JellyfinHomeSource.ReadAvailable(page);
        Assert.Equal(new[] { 10, 12 }, list.Select(r => r.TmdbId));
        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), list[0].AddedAt);
        Assert.Equal(new[] { 1, 2 }, list[1].Seasons);
    }

    [Fact]
    public void RequestsMatchLibraryItemsByTmdbId()
    {
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "The Notebook" };
        movie.ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "11036" };
        var show = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), Name = "Show" };
        show.ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "11036" };
        var other = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Other" };
        other.ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "500" };
        var untagged = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "No ids" };

        var index = JellyfinHomeSource.IndexByTmdb(new MediaBrowser.Controller.Entities.BaseItem[] { movie, show, other, untagged });

        Assert.Equal(movie.Id, Assert.Single(index[(Jellyfin.Data.Enums.BaseItemKind.Movie, "11036")]).Id);
        Assert.Equal(show.Id, Assert.Single(index[(Jellyfin.Data.Enums.BaseItemKind.Series, "11036")]).Id);
        Assert.Equal(3, index.Count);
    }

    [Fact]
    public void SimilarRanksSharedGenres()
    {
        var seed = Movie("Seed", genres: new[] { "Action", "Comedy" });
        var both = Movie("Both", genres: new[] { "Comedy", "Action" });
        var one = Movie("One", genres: new[] { "Action" });
        var none = Movie("None", genres: new[] { "Drama" });
        var seen = Movie("Seen", played: true, genres: new[] { "Action", "Comedy" });
        Assert.Equal(new[] { both.Id, one.Id, seen.Id }, HomeRules.Similar(seed, new[] { seen, one, none, both, seed }).Select(c => c.Id));
    }

    [Fact]
    public void SanitizeKeepsOnlyCardFields()
    {
        var full = JsonNode.Parse("""{"Id":"a","Name":"N","Path":"/media/x.mkv","MediaSources":[{"Path":"/x"}],"ImageTags":{"Primary":"t"},"UserData":{"Played":true,"Key":"k","PlaybackPositionTicks":5}}""")!.AsObject();
        var item = JellyfinHomeSource.Sanitize(full);
        Assert.Null(item["Path"]);
        Assert.Null(item["MediaSources"]);
        Assert.Equal("t", item["ImageTags"]!["Primary"]!.GetValue<string>());
        Assert.Null(item["UserData"]!["Key"]);
        Assert.True(item["UserData"]!["Played"]!.GetValue<bool>());
    }

    [Fact]
    public void ManifestAdvertisesHomeOnlyWhenOn()
    {
        Assert.False(CapabilityManifest.Build(new PluginConfiguration()).Features.ContainsKey("homeRows"));
        var manifest = CapabilityManifest.Build(new PluginConfiguration { HomeRowsEnabled = true });
        // Version 1 meant the older /support/home/rows feed, so today's apps ignore this until they learn the new path.
        Assert.Equal(2, manifest.Features["homeRows"].Version);
        Assert.Equal(1, manifest.Features["homeSections"].Version);
    }

    [Fact]
    public void WantedRowsFollowModulesAndOperatorSwitches()
    {
        var config = new PluginConfiguration { HomeRowsEnabled = true, HomeRowsOff = "watch-again, showcase" };
        var wanted = HomeRowsService.WantedRows(config);
        Assert.DoesNotContain("watch-again", wanted);
        Assert.DoesNotContain("showcase", wanted);
        Assert.DoesNotContain("popular", wanted);
        Assert.DoesNotContain("requests-ready", wanted);
        Assert.Contains("continue-watching", wanted);
        config.PlayStatsEnabled = true;
        config.RequestsEnabled = true;
        config.SeerrUrl = "http://seerr:5055";
        config.SeerrApiKey = "key";
        wanted = HomeRowsService.WantedRows(config);
        Assert.Contains("popular", wanted);
        Assert.Contains("requests-ready", wanted);
        config.PlayStatsEnabled = false;
        config.PlayHistoryEnabled = true;
        Assert.Contains("popular", HomeRowsService.WantedRows(config));
    }

    [Fact]
    public void PopularRanksShowsAndMoviesFromHistory()
    {
        var show = Guid.NewGuid();
        var movie = Guid.NewGuid();
        var trailer = Guid.NewGuid();
        var plays = new[]
        {
            new Jellyfin.Plugin.Zorofin.Stats.PlayEvent { ItemId = Guid.NewGuid(), SeriesId = show, ItemType = "Episode", Seconds = 1500 },
            new Jellyfin.Plugin.Zorofin.Stats.PlayEvent { ItemId = Guid.NewGuid(), SeriesId = show, ItemType = "Episode", Seconds = 1500 },
            new Jellyfin.Plugin.Zorofin.Stats.PlayEvent { ItemId = movie, ItemType = "Movie", Seconds = 2400 },
            new Jellyfin.Plugin.Zorofin.Stats.PlayEvent { ItemId = trailer, ItemType = "Movie", Seconds = 30 },
            new Jellyfin.Plugin.Zorofin.Stats.PlayEvent { ItemId = Guid.NewGuid(), ItemType = "Audio", Seconds = 3000 },
        };
        Assert.Equal(new[] { show, movie }, HomeRules.PopularTitles(plays));
    }

    [Fact]
    public async Task BuilderKeepsRowsApartAndFeedsMatchTheContracts()
    {
        var source = new FakeSource();
        var show = Guid.NewGuid();
        source.Favorite.Add(new HomeEntry { Id = show, Name = "Show", Kind = "Series" });
        var episode = Episode(show, "Pilot");
        source.Episodes.Add(episode);
        source.MovieList.Add(Movie("Short", 80, genres: "Comedy"));
        source.Fail = "RecentlyAdded";
        var wanted = HomeBuilder.Catalog.Select(r => r.Id).ToHashSet();
        var rows = await HomeBuilder.BuildAsync(source, Now, TimeZoneInfo.Utc, wanted, null, CancellationToken.None);

        Assert.Equal("ready", rows.Single(r => r.Id == "new-episodes").State);
        Assert.Equal("unavailable", rows.Single(r => r.Id == "recently-added-movies").State);
        Assert.Equal("unavailable", rows.Single(r => r.Id == "requests-ready").State);
        Assert.Equal("empty", rows.Single(r => r.Id == "sports").State);

        var items = rows.SelectMany(r => r.Cards).Select(c => c.Entry.Id).Distinct()
            .ToDictionary(id => id, id => new JsonObject { ["Id"] = id.ToString("N"), ["Name"] = "x" });
        var snapshot = new HomeSnapshot(Now, rows, items, new[] { "Cubs" });

        var app = HomeFeeds.AppRows(snapshot);
        Assert.Equal(1, app["schemaVersion"]!.GetValue<int>());
        var appRows = app["rows"]!.AsArray();
        Assert.True(appRows.Count <= 7);
        Assert.All(appRows, r => Assert.Contains(r!["id"]!.GetValue<string>(), HomeBuilder.AppRowIds));
        Assert.Equal("Cubs", app["preferences"]!["teams"]![0]!.GetValue<string>());

        var web = HomeFeeds.Sections(snapshot)["sections"]!.AsArray();
        Assert.All(web, s => Assert.NotEmpty(s!["cards"]!.AsArray()));
        var episodes = web.Single(s => s!["id"]!.GetValue<string>() == "new-episodes")!;
        Assert.Equal("personal", episodes["placement"]!.GetValue<string>());
        Assert.Equal(episode.Id.ToString("N"), episodes["cards"]![0]!["id"]!.GetValue<string>());
        Assert.DoesNotContain(web, s => s!["id"]!.GetValue<string>() == "recently-added-movies");
    }

    [Fact]
    public void CuratedPrefersMatchingCollectionsThenMetadata()
    {
        var source = new FakeSource();
        var day = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var horror = Movie("Night", genres: "Horror");
        var (title, cards) = HomeBuilder.Curated(source, new[] { Movie("Comedy", genres: "Comedy"), horror }, day, null);
        Assert.Equal("Spooky season", title);
        Assert.Equal(horror.Id, Assert.Single(cards).Id);

        var box = new HomeEntry { Id = Guid.NewGuid(), Name = "Halloween Classics", Kind = "BoxSet" };
        var inBox = Movie("Boxed");
        source.Boxes[box] = new[] { inBox };
        (title, cards) = HomeBuilder.Curated(source, new[] { horror }, day, null);
        Assert.Equal(inBox.Id, Assert.Single(cards).Id);
    }

    [Fact]
    public void CuratedPlanMatchesTheServerAndApps()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "seasonal-plan-parity.json")))!;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(fixture["timeZone"]!.GetValue<string>());
        var cases = fixture["cases"]!.AsArray();
        Assert.True(cases.Count > 700);
        foreach (var c in cases)
        {
            var at = DateTime.Parse(c!["at"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            var expected = c["plan"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray();
            Assert.Equal(expected, CuratedSeasons.ThemePlan(CuratedSeasons.CalendarDay(at, zone)));
        }
    }

    [Fact]
    public void CuratedRanksAndMatchesLikeTheServer()
    {
        // Expected values from support-agent/curated-seasons.js with the same inputs.
        HomeEntry E(string id, bool played = false) => new() { Id = Guid.Parse(id), Name = id, Played = played };
        var picks = CuratedSeasons.RankPicks(
            new[] { E("aaaa1111aaaa1111aaaa1111aaaa1111"), E("bbbb2222bbbb2222bbbb2222bbbb2222", true), E("cccc3333cccc3333cccc3333cccc3333"), E("dddd4444dddd4444dddd4444dddd4444") },
            "halloween", Guid.Parse("0123456789abcdef0123456789abcdef"), new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new[] { "aaaa1111", "cccc3333", "dddd4444", "bbbb2222" }, picks.Select(p => p.Id.ToString("N")[..8]));

        Assert.True(CuratedSeasons.MetadataMatches(new HomeEntry { Name = "A summer road trip" }, "summer"));
        Assert.True(CuratedSeasons.MetadataMatches(new HomeEntry { Name = "x", Tags = new[] { "Pride Month" } }, "pride"));
        Assert.False(CuratedSeasons.MetadataMatches(new HomeEntry { Name = "Pride and Prejudice" }, "pride"));
    }

    [Fact]
    public void CalendarEventsAreValidatedLikeTheServer()
    {
        var events = CuratedSeasons.ParseEvents("""{"schemaVersion":1,"events":[{"theme":"eid","start":"2027-03-09","end":"2027-03-12"}]}""");
        Assert.Equal("eid", Assert.Single(events).Theme);
        Assert.Equal("eid", CuratedSeasons.ThemePlan(new DateTime(2027, 3, 10, 0, 0, 0, DateTimeKind.Utc), events)[0]);
        Assert.Empty(CuratedSeasons.ParseEvents("""{"schemaVersion":1,"events":[{"theme":"nope","start":"2027-03-09","end":"2027-03-12"}]}"""));
        Assert.Empty(CuratedSeasons.ParseEvents("""{"schemaVersion":1,"events":[{"theme":"eid","start":"2027-03-09","end":"2027-06-12"}]}"""));
        Assert.Empty(CuratedSeasons.ParseEvents("{"));
    }

    private sealed class FakeSource : IHomeSource
    {
        public List<HomeEntry> Favorite { get; } = new();

        public List<HomeEntry> Episodes { get; } = new();

        public List<HomeEntry> MovieList { get; } = new();

        public Dictionary<HomeEntry, HomeEntry[]> Boxes { get; } = new();

        public string? Fail { get; set; }

        public Guid ViewerId { get; } = Guid.NewGuid();

        public IReadOnlyList<HomeEntry> Resumable(int limit) => Array.Empty<HomeEntry>();

        public IReadOnlyList<HomeEntry> NextUp(int limit) => Array.Empty<HomeEntry>();

        public IReadOnlyList<HomeEntry> Movies(int limit) => MovieList;

        public IReadOnlyList<HomeEntry> ShowcaseMovies(int limit) => Array.Empty<HomeEntry>();

        public IReadOnlyList<HomeEntry> FavoriteSeries(int limit) => Favorite;

        public IReadOnlyList<HomeEntry> RecentEpisodes(IReadOnlyCollection<Guid> seriesIds, DateTime sinceUtc, int limit) => Episodes;

        public IReadOnlyList<HomeEntry> Collections(int limit) => Boxes.Keys.ToList();

        public IReadOnlyList<HomeEntry> CollectionMovies(Guid collectionId, int limit) => Boxes.First(b => b.Key.Id == collectionId).Value;

        public IReadOnlyList<HomeEntry>? Popular(int days, int limit) => null;

        public IReadOnlyList<GuideProgram> SportsPrograms(DateTime fromUtc, DateTime untilUtc, int limit) => Array.Empty<GuideProgram>();

        public IReadOnlyDictionary<Guid, HomeEntry> Channels(IEnumerable<Guid> ids) => new Dictionary<Guid, HomeEntry>();

        public IReadOnlyList<string> Teams() => Array.Empty<string>();

        public IReadOnlyList<HomeEntry> Favorites(int limit) => Favorite;

        public IReadOnlyList<HomeEntry> RecentlyAdded(string kind, int limit) => Fail == "RecentlyAdded" ? throw new InvalidOperationException("library down") : MovieList;

        public IReadOnlyList<HomeEntry> WatchAgain(int limit) => Array.Empty<HomeEntry>();

        public IReadOnlyList<HomeEntry> RecentlyWatched(int limit) => MovieList;

        public IReadOnlyList<HomeEntry> SharingGenres(HomeEntry seed, int limit) => Array.Empty<HomeEntry>();

        public Task<IReadOnlyList<(HomeEntry Entry, DateTime? Arrived)>?> AvailableRequestsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(HomeEntry Entry, DateTime? Arrived)>?>(null);
    }
}
