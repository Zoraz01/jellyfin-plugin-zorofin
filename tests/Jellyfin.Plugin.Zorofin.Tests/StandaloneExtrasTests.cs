using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Arr;
using Jellyfin.Plugin.Zorofin.Configuration;
using Jellyfin.Plugin.Zorofin.Extras;
using Jellyfin.Plugin.Zorofin.Home;
using Jellyfin.Plugin.Zorofin.Requests;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class StandaloneExtrasTests
{
    [Fact]
    public void BannerIdsKeepOrderDropJunkAndRepeats()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var ids = ExtrasModels.BannerIds($"{a}\n not-an-id, {b:N};{a}  00000000-0000-0000-0000-000000000000");
        Assert.Equal(new[] { a.ToString("N"), b.ToString("N") }, ids);
    }

    [Fact]
    public void ManifestOnlyOffersWhatIsSetUp()
    {
        var config = new PluginConfiguration { CalendarEnabled = true, DownloadsEnabled = true, ArrLinksEnabled = true, RatingsEnabled = true, CatchUpEnabled = true, CatchUpLogEnabled = true };
        var none = ExtrasModels.Manifest(config, true);
        Assert.False((bool)none["calendar"]!);
        Assert.False((bool)none["ratings"]!);
        Assert.True((bool)none["catchUp"]!);

        config.SonarrUrl = "http://sonarr:8989";
        config.SonarrApiKey = "k";
        config.MdbListApiKey = "m";
        var admin = ExtrasModels.Manifest(config, true);
        var viewer = ExtrasModels.Manifest(config, false);
        Assert.True((bool)admin["calendar"]! && (bool)admin["downloads"]! && (bool)admin["arrLinks"]! && (bool)admin["ratings"]! && (bool)admin["catchUpLog"]!);
        Assert.False((bool)viewer["arrLinks"]!);
        Assert.False((bool)viewer["catchUpLog"]!);
        config.MdbListApiKey = "secret-mdb-key";
        Assert.DoesNotContain("secret-mdb-key", ExtrasModels.Manifest(config, true).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TrailerStartSkipsChainedIntroSegmentsOnly()
    {
        var chained = JsonNode.Parse("""[{"segment":[0.5,6.2],"category":"intro"},{"segment":[7,12.4],"category":"sponsor"},{"segment":[80,90],"category":"sponsor"}]""") as JsonArray;
        Assert.Equal(13, ExtrasModels.TrailerStart(chained));
        var late = JsonNode.Parse("""[{"segment":[30,40]}]""") as JsonArray;
        Assert.Equal(0, ExtrasModels.TrailerStart(late));
        Assert.Equal(0, ExtrasModels.TrailerStart(null));
    }

    [Fact]
    public void RatingsKeepKnownSourcesInOrder()
    {
        var body = JsonNode.Parse("""{"ids":{"imdb":"tt0133093"},"ratings":[{"source":"metacritic","value":73,"score":73},{"source":"imdb","value":8.7,"score":87,"votes":2100000},{"source":"tomatoes","value":null},{"source":"unknown","value":5}]}""");
        var shaped = ExtrasModels.Ratings(body);
        var list = (JsonArray)shaped["ratings"]!;
        Assert.Equal(new[] { "imdb", "metacritic" }, list.Select(r => (string)r!["source"]!));
        Assert.Equal("https://www.imdb.com/title/tt0133093/", (string?)shaped["imdbUrl"]);
    }

    [Fact]
    public void DownloadsFilterToVisibleTitlesAndHideInternals()
    {
        var records = JsonNode.Parse("""
            [{"id":5,"size":1000,"sizeleft":250,"status":"downloading","trackedDownloadState":"downloading","trackedDownloadStatus":"ok","timeleft":"00:05:00",
              "outputPath":"/secret/path","downloadClient":"sab","indexer":"geek",
              "series":{"title":"Severance","tmdbId":95396,"tvdbId":371980,"images":[{"coverType":"poster","remoteUrl":"https://image.tmdb.org/t/p/original/a.jpg"}]},
              "episode":{"seasonNumber":2,"episodeNumber":3,"title":"Who Is Alive?"},"quality":{"quality":{"name":"WEBDL-1080p"}}},
             {"id":6,"size":10,"sizeleft":10,"status":"queued","series":{"title":"Other","tmdbId":1}}]
            """) as JsonArray;
        var all = ArrShaper.Downloads(records, "sonarr", null).ToList();
        Assert.Equal(2, all.Count);
        var mine = ArrShaper.Downloads(records, "sonarr", new HashSet<int> { 95396 }).Single();
        Assert.Equal(75, (double)mine["progress"]!);
        Assert.Equal("S2:E3 · Who Is Alive?", (string?)mine["subtitle"]);
        Assert.Equal("https://image.tmdb.org/t/p/w342/a.jpg", (string?)mine["posterUrl"]);
        var text = mine.ToJsonString();
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("geek", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("downloading", "downloading", "ok", "downloading")]
    [InlineData("completed", "importPending", "warning", "stalled")]
    [InlineData("completed", "importing", "ok", "importing")]
    [InlineData("paused", "downloading", "ok", "paused")]
    [InlineData("failed", "downloading", "ok", "failed")]
    [InlineData("delay", "downloading", "ok", "queued")]
    public void DownloadStates(string status, string state, string health, string expected) =>
        Assert.Equal(expected, ArrShaper.State(status, state, health));

    [Fact]
    public void CalendarMoviesSplitIntoReleasesInsideWindow()
    {
        var movies = JsonNode.Parse("""[{"title":"Dune","year":2026,"tmdbId":9,"inCinemas":"2026-10-01T00:00:00Z","digitalRelease":"2026-11-10T00:00:00Z","physicalRelease":"2026-12-20T00:00:00Z"}]""") as JsonArray;
        var events = ArrShaper.CalendarMovies(movies, new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero)).ToList();
        Assert.Single(events);
        Assert.Equal("digital", (string?)events[0]["release"]);
        Assert.Equal("2026-11-10", (string?)events[0]["at"]);
    }

    [Fact]
    public void CalendarEpisodesSkipTbaNames()
    {
        var episodes = JsonNode.Parse("""[{"seasonNumber":1,"episodeNumber":1,"title":"TBA","airDateUtc":"2026-11-08T02:00:00Z","hasFile":false,"series":{"title":"New Show","tvdbId":7}}]""") as JsonArray;
        var e = ArrShaper.CalendarEpisodes(episodes).Single();
        Assert.Equal("S1:E1", (string?)e["subtitle"]);
        Assert.True((bool)e["premiere"]!);
    }

    [Fact]
    public void ArrLinkUsesPublicAddressAndTagLabels()
    {
        var tags = ArrShaper.Tags(JsonNode.Parse("""[{"id":1,"label":"anime"},{"id":2,"label":"kids"}]""") as JsonArray);
        var link = ArrShaper.Link("sonarr", JsonNode.Parse("""{"titleSlug":"one-piece","monitored":true,"tags":[2,9]}""") as JsonObject, tags, new Uri("https://sonarr.example.com/"));
        Assert.Equal("https://sonarr.example.com/series/one-piece", (string?)link!["url"]);
        Assert.Equal(new[] { "kids" }, ((JsonArray)link["tags"]!).Select(t => (string)t!));
    }

    [Fact]
    public void PosterRejectsUnknownHosts()
    {
        Assert.Null(ArrShaper.Poster(JsonNode.Parse("""[{"coverType":"poster","remoteUrl":"http://image.tmdb.org/x.jpg"}]""") as JsonArray));
        Assert.Null(ArrShaper.Poster(JsonNode.Parse("""[{"coverType":"poster","remoteUrl":"https://evil.example/x.jpg"}]""") as JsonArray));
    }

    [Fact]
    public void WatchlistOneWayOnlyAdds()
    {
        var plan = WatchlistSync.Plan(Set("movie:1", "tv:2"), Set("movie:3"), Set("movie:1"), Set(), fromSeerr: true, toSeerr: false);
        Assert.Equal(Set("movie:1"), plan.Like);
        Assert.Empty(plan.Add);
        Assert.Empty(plan.Unlike);
    }

    [Fact]
    public void WatchlistBothWaysCarriesRemovals()
    {
        // movie:1 was in step and is now gone from Seerr; movie:2 was in step and is now unliked in Jellyfin.
        var plan = WatchlistSync.Plan(Set("movie:2", "movie:4"), Set("movie:1", "movie:5"), Set("movie:2", "movie:4"), Set("movie:1", "movie:2"), true, true);
        Assert.Equal(Set("movie:1"), plan.Unlike);
        Assert.Equal(Set("movie:2"), plan.Remove);
        Assert.Equal(Set("movie:4"), plan.Like);
        Assert.Equal(Set("movie:5"), plan.Add);
        Assert.Equal(Set("movie:4", "movie:5"), plan.InStep);
    }

    [Fact]
    public void WatchlistKeepsSeerrEntryForTitleLeavingLibrary()
    {
        var plan = WatchlistSync.Plan(Set("movie:7"), Set(), Set(), Set("movie:7"), true, true);
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void CatchUpLogRotatesAndSummarises()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zf-catchup-" + Guid.NewGuid().ToString("N"));
        var log = new CatchUpLog(Path.Combine(dir, "catchup-log.jsonl"));
        var user = Guid.NewGuid();
        log.Add(new CatchUpEntry(DateTimeOffset.UtcNow, user, "ana", Guid.NewGuid(), "Heat", "later"));
        log.Add(new CatchUpEntry(DateTimeOffset.UtcNow, user, "ana", Guid.NewGuid(), "Up", "watched"));
        var read = log.Read(10);
        Assert.Equal("Up", (string?)read["entries"]![0]!["title"]);
        Assert.Equal(2, (int)read["summary"]![0]!["total"]!);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ContinueWatchingHideLastsUntilPlayedAgain()
    {
        var hides = new ContinueWatchingHides(null);
        var viewer = Guid.NewGuid();
        var series = Guid.NewGuid();
        var hiddenAt = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var episode = new HomeEntry { Id = Guid.NewGuid(), Kind = "Episode", SeriesId = series, LastPlayed = hiddenAt.AddHours(-1) };
        var other = new HomeEntry { Id = Guid.NewGuid(), Kind = "Episode", SeriesId = series, LastPlayed = hiddenAt.AddHours(2) };
        Assert.False(hides.IsHidden(viewer, episode));
        hides.Hide(viewer, series, hiddenAt);
        Assert.True(hides.IsHidden(viewer, episode));
        Assert.False(hides.IsHidden(viewer, other));
        Assert.False(hides.IsHidden(Guid.NewGuid(), episode));
        hides.Restore(viewer, series);
        Assert.False(hides.IsHidden(viewer, episode));
    }

    private static HashSet<string> Set(params string[] keys) => keys.ToHashSet(StringComparer.Ordinal);
}
