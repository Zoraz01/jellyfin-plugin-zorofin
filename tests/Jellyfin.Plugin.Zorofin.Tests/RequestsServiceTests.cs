using System.Net.Http;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Requests;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class RequestsServiceTests
{
    private static readonly Guid Viewer = Guid.Parse("0f6c2a8e5b1d4c3f9a7e6d5c4b3a2918");

    private sealed class FakeUpstream : UpstreamClient
    {
        public FakeUpstream()
            : base(null!)
        {
        }

        public List<(HttpMethod Method, string Path, int? ActAs, JsonNode? Body)> Calls { get; } = new();

        public Func<string, UpstreamResult> Respond { get; set; } = _ => new UpstreamResult(200, new JsonObject());

        public override Task<UpstreamResult> SendAsync(ServiceEndpoint endpoint, HttpMethod method, string relativePath, JsonNode? body, int? actAsSeerrUser, CancellationToken cancellationToken)
        {
            Calls.Add((method, relativePath, actAsSeerrUser, body));
            return Task.FromResult(Respond(relativePath));
        }
    }

    private static ServiceEndpoint Seerr()
    {
        Assert.True(ServiceEndpoint.TryCreate("http://seerr:5055", "key", out var endpoint));
        return endpoint;
    }

    [Fact]
    public async Task FindsLinkedSeerrUserByJellyfinId()
    {
        var upstream = new FakeUpstream
        {
            Respond = path => path.Contains("skip=0", StringComparison.Ordinal)
                ? new UpstreamResult(200, JsonNode.Parse("""{"pageInfo":{"pages":2},"results":[{"id":1,"jellyfinUserId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}"""))
                : new UpstreamResult(200, JsonNode.Parse($$"""{"pageInfo":{"pages":2},"results":[{"id":42,"jellyfinUserId":"{{Viewer:N}}"},{"id":43}]}""")),
        };
        var users = new SeerrUserDirectory(upstream);
        Assert.Equal(42, await users.FindAsync(Seerr(), Viewer, CancellationToken.None));
        Assert.Equal(2, upstream.Calls.Count);
        Assert.All(upstream.Calls, c => Assert.Null(c.ActAs));

        // Cached: a second lookup does not page through Seerr again.
        Assert.Equal(42, await users.FindAsync(Seerr(), Viewer, CancellationToken.None));
        Assert.Equal(2, upstream.Calls.Count);
    }

    [Fact]
    public async Task UnreachableSeerrIsReportedNotTreatedAsUnlinked()
    {
        var users = new SeerrUserDirectory(new FakeUpstream { Respond = _ => new UpstreamResult(0, null) });
        await Assert.ThrowsAsync<UpstreamUnavailableException>(() => users.FindAsync(Seerr(), Viewer, CancellationToken.None));
    }

    [Fact]
    public async Task SubmitActsAsViewerAndShapesReceipt()
    {
        var upstream = new FakeUpstream
        {
            Respond = _ => new UpstreamResult(201, JsonNode.Parse("""{"id":9,"status":1,"is4k":false,"requestedBy":{"email":"x@example.com"},"seasons":[{"seasonNumber":1}]}""")),
        };
        var service = new RequestsService(upstream, new SeerrUserDirectory(upstream));
        var result = await service.SubmitAsync(Seerr(), 42, new RequestIntent { MediaType = "tv", MediaId = 7, Seasons = new[] { 1 } }, CancellationToken.None);
        var call = Assert.Single(upstream.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("api/v1/request", call.Path);
        Assert.Equal(42, call.ActAs);
        Assert.True(result.Ok);
        Assert.Equal("""{"id":9,"status":1,"is4k":false,"seasons":[{"seasonNumber":1}]}""", result.Body!.ToJsonString());
    }

    [Fact]
    public async Task OwnActivityFiltersByRequesterSharedLetsSeerrDecide()
    {
        var upstream = new FakeUpstream { Respond = _ => new UpstreamResult(200, JsonNode.Parse("""{"pageInfo":{"pages":1},"results":[]}""")) };
        var service = new RequestsService(upstream, new SeerrUserDirectory(upstream));
        await service.ActivityAsync(Seerr(), 42, mine: true, 24, 0, "all", CancellationToken.None);
        await service.ActivityAsync(Seerr(), 42, mine: false, 24, 0, "all", CancellationToken.None);
        Assert.Contains("requestedBy=42", upstream.Calls[0].Path, StringComparison.Ordinal);
        Assert.DoesNotContain("requestedBy", upstream.Calls[1].Path, StringComparison.Ordinal);
        Assert.All(upstream.Calls, c => Assert.Equal(42, c.ActAs));
    }

    [Fact]
    public async Task GenresAreShapedAndKeptForAnHour()
    {
        var upstream = new FakeUpstream { Respond = _ => new UpstreamResult(200, JsonNode.Parse("""[{"id":28,"name":"Action","backdrops":["/x.jpg"]}]""")) };
        var service = new RequestsService(upstream, new SeerrUserDirectory(upstream));
        var first = await service.GenresAsync(Seerr(), "movie", CancellationToken.None);
        var second = await service.GenresAsync(Seerr(), "movie", CancellationToken.None);
        Assert.Equal("""[{"id":28,"name":"Action"}]""", first!.ToJsonString());
        Assert.Equal(first.ToJsonString(), second!.ToJsonString());
        Assert.Single(upstream.Calls);
        Assert.Equal("api/v1/genres/movie", upstream.Calls[0].Path);
    }

    [Fact]
    public async Task UnlinkedViewerGetsClearStatus()
    {
        var upstream = new FakeUpstream { Respond = _ => new UpstreamResult(200, JsonNode.Parse("""{"pageInfo":{"pages":1},"results":[]}""")) };
        var service = new RequestsService(upstream, new SeerrUserDirectory(upstream));
        var config = new Configuration.PluginConfiguration { RequestsEnabled = true, SeerrUrl = "http://seerr:5055", SeerrApiKey = "key" };
        var status = await service.UserStatusAsync(config, Viewer, CancellationToken.None);
        Assert.True((bool)status["active"]!);
        Assert.False((bool)status["userFound"]!);
    }
}
