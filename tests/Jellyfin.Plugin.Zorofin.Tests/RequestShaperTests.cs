using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Requests;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class RequestShaperTests
{
    private const string Details = """
    {
      "id": 1399, "name": "Show", "firstAirDate": "2011-04-17", "posterPath": "/p.jpg", "overview": "o",
      "serviceUrl": "http://sonarr.internal/series/1", "credits": {"cast": []},
      "externalIds": {"tvdbId": 121361, "imdbId": "tt1"},
      "seasons": [{"id": 3624, "seasonNumber": 1, "name": "Season 1", "episodeCount": 10, "overview": "x"}],
      "mediaInfo": {
        "status": 4, "jellyfinMediaId": "abc", "serviceUrl": "http://sonarr.internal",
        "seasons": [{"seasonNumber": 1, "status": 5}],
        "requests": [{"id": 7, "status": 2, "is4k": false, "seasons": [{"id": 9, "seasonNumber": 2}],
                      "requestedBy": {"email": "someone@example.com", "displayName": "Someone"}}]
      }
    }
    """;

    [Fact]
    public void TitleKeepsOnlyAppFields()
    {
        var shaped = RequestShaper.ShapeTitle(JsonNode.Parse(Details))!;
        var json = shaped.ToJsonString();
        Assert.DoesNotContain("example.com", json);
        Assert.DoesNotContain("Someone", json);
        Assert.DoesNotContain("internal", json);
        Assert.DoesNotContain("imdbId", json);
        Assert.Equal(121361, (int)shaped["externalIds"]!["tvdbId"]!);
        Assert.Equal(10, (int)shaped["seasons"]![0]!["episodeCount"]!);
        Assert.Equal(2, (int)shaped["mediaInfo"]!["requests"]![0]!["seasons"]![0]!["seasonNumber"]!);
        Assert.Equal("abc", (string)shaped["mediaInfo"]!["jellyfinMediaId"]!);
    }

    [Fact]
    public void TitleKeepsGenresAndEpisodeLengthForTheWebHub()
    {
        var shaped = RequestShaper.ShapeTitle(JsonNode.Parse("""
            {"id":5,"name":"S","genres":[{"id":18,"name":"Drama","extra":"x"},{"id":0,"name":"Bad"},{"id":3}],"episodeRunTime":[42,0,"x"]}
            """))!;
        Assert.Equal("""[{"id":18,"name":"Drama"}]""", shaped["genres"]!.ToJsonString());
        Assert.Equal("[42]", shaped["episodeRunTime"]!.ToJsonString());
    }

    [Fact]
    public void PagesDropPeopleAndMalformedResults()
    {
        var page = RequestShaper.ShapePage(JsonNode.Parse("""
            {"page":1,"totalPages":3,"results":[{"id":1,"mediaType":"movie","title":"A"},{"id":2,"mediaType":"person","name":"P"},{"mediaType":"tv"}]}
            """));
        Assert.Single(page["results"]!.AsArray());
        Assert.Equal(3, (int)page["totalPages"]!);
    }

    [Fact]
    public void ActivityHidesRequesterOnOwnList()
    {
        var request = JsonNode.Parse("""
            {"id":5,"status":2,"is4k":false,"type":"movie","media":{"tmdbId":11,"status":3},
             "requestedBy":{"displayName":"Friend","email":"f@example.com"}}
            """);
        var details = JsonNode.Parse("""{"id":11,"title":"Future","releaseDate":"2031-01-01","posterPath":"/x.jpg"}""");
        var mine = RequestShaper.ShapeActivityItem(request, details, new DateOnly(2026, 10, 28), includeRequester: false)!;
        Assert.False(mine.ContainsKey("requestedBy"));
        Assert.Equal("Processing", (string)mine["mediaStatus"]!);
        Assert.True((bool)mine["isComingSoon"]!);
        Assert.Equal(2031, (int)mine["year"]!);
        Assert.Equal("https://image.tmdb.org/t/p/w342/x.jpg", (string)mine["posterUrl"]!);

        var shared = RequestShaper.ShapeActivityItem(request, details, new DateOnly(2026, 10, 28), includeRequester: true)!;
        Assert.Equal("Friend", (string)shared["requestedBy"]!);
        Assert.DoesNotContain("example.com", shared.ToJsonString());
    }

    [Fact]
    public void QueueOnlyShowsVisibleTitles()
    {
        var records = JsonNode.Parse("""
            [{"movie":{"tmdbId":11},"size":1000,"sizeleft":250,"status":"downloading","trackedDownloadState":"downloading","trackedDownloadStatus":"ok","outputPath":"/data/secret"},
             {"movie":{"tmdbId":99},"size":10,"sizeleft":0,"status":"completed"}]
            """)!.AsArray();
        var rows = RequestShaper.ShapeQueue(records, "radarr", new HashSet<int> { 11 }).ToList();
        var row = Assert.Single(rows);
        Assert.Equal(75.0, (double)row["progress"]!);
        Assert.Equal("downloading downloading", (string)row["status"]!);
        Assert.DoesNotContain("secret", row.ToJsonString());
    }

    [Theory]
    [InlineData("/abc.jpg", true)]
    [InlineData("/a-b_c.webp", true)]
    [InlineData("//evil.example/x.jpg", false)]
    [InlineData("/../x.jpg", false)]
    [InlineData("/x.svg", false)]
    [InlineData(null, false)]
    public void PosterPathsAreStrict(string? path, bool allowed)
    {
        Assert.Equal(allowed, RequestShaper.PosterUrl(path) is not null);
    }

    [Fact]
    public void IntentValidation()
    {
        Assert.True(new RequestIntent { MediaType = "movie", MediaId = 1 }.IsValid());
        Assert.False(new RequestIntent { MediaType = "movie", MediaId = 1, Seasons = new[] { 1 } }.IsValid());
        Assert.False(new RequestIntent { MediaType = "tv", MediaId = 1 }.IsValid());
        Assert.False(new RequestIntent { MediaType = "tv", MediaId = 1, Seasons = new[] { 1, 1 } }.IsValid());
        Assert.False(new RequestIntent { MediaType = "person", MediaId = 1 }.IsValid());
        Assert.False(new RequestIntent { MediaType = "movie", MediaId = 0 }.IsValid());
        var body = new RequestIntent { MediaType = "tv", MediaId = 2, Seasons = new[] { 1, 2 }, TvdbId = 5 }.ToSeerrBody();
        Assert.Equal("""{"mediaType":"tv","mediaId":2,"is4k":false,"seasons":[1,2],"tvdbId":5}""", body.ToJsonString());
    }
}
