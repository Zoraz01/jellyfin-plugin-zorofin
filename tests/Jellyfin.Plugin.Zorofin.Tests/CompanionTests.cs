using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Capabilities;
using Jellyfin.Plugin.Zorofin.Companion;
using Jellyfin.Plugin.Zorofin.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class CompanionTests
{
    // Shared with the companion's node tests; see docs/COMPANION_BRIDGE.md.
    private const string Secret = "zorofin-test-secret-0123456789abcdef";
    private const long Timestamp = 1793232000;
    private static readonly Guid Viewer = Guid.Parse("4f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f");

    [Fact]
    public void SignerMatchesSharedTestVectors()
    {
        Assert.Equal(
            "726ef4520137fb3f18099e1e3c84f8579b1ff2a6c9fcbda68a6a1cc718254a0d",
            CompanionSigner.Sign(Secret, "post", "/v1/modules/recommendations/rows?limit=10", Timestamp, "00112233445566778899aabbccddeeff", Viewer.ToString("N"), false, Encoding.UTF8.GetBytes("{\"seed\":\"abc\"}")));
        Assert.Equal(
            "69898191872a06870b424cc2aabe76e7cd18600d85224a389a40cfc746edb71c",
            CompanionSigner.Sign(Secret, "GET", "/v1/capabilities", Timestamp, "ffeeddccbbaa99887766554433221100", string.Empty, false, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public async Task SendsSignedHeadersAndNeverTheJellyfinToken()
    {
        var client = new RecordingClient(HttpStatusCode.OK, "{\"rows\":[]}") { Nonce = "00112233445566778899aabbccddeeff" };
        var endpoint = CompanionClient.Endpoint(Config())!;
        var response = await client.SendAsync(endpoint, HttpMethod.Post, "v1/modules/recommendations/rows?limit=10", Encoding.UTF8.GetBytes("{\"seed\":\"abc\"}"), Viewer, false, CancellationToken.None);

        Assert.Equal(200, response.Status);
        var sent = Assert.Single(client.Sent);
        Assert.Equal("http://companion:9310/v1/modules/recommendations/rows?limit=10", sent.Uri);
        Assert.Equal("726ef4520137fb3f18099e1e3c84f8579b1ff2a6c9fcbda68a6a1cc718254a0d", sent.Headers["X-Zorofin-Signature"]);
        Assert.Equal(Viewer.ToString("N"), sent.Headers["X-Zorofin-User"]);
        Assert.Equal("0", sent.Headers["X-Zorofin-User-Admin"]);
        Assert.DoesNotContain(sent.Headers.Keys, key => key.Contains("Authorization", StringComparison.OrdinalIgnoreCase) || key.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AdminFlagIsNeverSentWithoutAViewer()
    {
        var client = new RecordingClient(HttpStatusCode.OK, "{}");
        await client.SendAsync(CompanionClient.Endpoint(Config())!, HttpMethod.Get, "v1/capabilities", null, null, true, CancellationToken.None);
        Assert.Equal("0", client.Sent[0].Headers["X-Zorofin-User-Admin"]);
        Assert.Equal(string.Empty, client.Sent[0].Headers["X-Zorofin-User"]);
    }

    [Fact]
    public void EndpointNeedsUrlLongSecretAndNoPathPrefix()
    {
        Assert.Null(CompanionClient.Endpoint(new PluginConfiguration()));
        Assert.Null(CompanionClient.Endpoint(Config(secret: "too-short")));
        Assert.Null(CompanionClient.Endpoint(Config(url: "http://proxy/companion/")));
        Assert.Null(CompanionClient.Endpoint(Config(url: "ftp://companion")));
        Assert.NotNull(CompanionClient.Endpoint(Config()));
    }

    [Fact]
    public async Task FeaturesAreParsedCachedAndDroppedWhenUnreachable()
    {
        var client = new RecordingClient(HttpStatusCode.OK, "{\"schemaVersion\":1,\"features\":{\"recommendations\":{\"version\":1,\"enabled\":true},\"Bad-Name\":{\"version\":1},\"recap\":{\"version\":0},\"notifications\":{\"version\":2,\"enabled\":false}}}");
        var features = await client.GetFeaturesAsync(Config(), CancellationToken.None);
        Assert.Equal(new[] { "notifications", "recommendations" }, features.Keys.Order());
        Assert.False(features["notifications"].Enabled);

        await client.GetFeaturesAsync(Config(), CancellationToken.None);
        Assert.Single(client.Sent); // cached

        client.Now += TimeSpan.FromSeconds(61);
        client.Fail = true;
        Assert.Empty(await client.GetFeaturesAsync(Config(), CancellationToken.None));
        Assert.Empty(await client.GetFeaturesAsync(new PluginConfiguration(), CancellationToken.None));
    }

    [Fact]
    public void ParseIgnoresOtherSchemaVersions()
    {
        Assert.Empty(CompanionClient.ParseFeatures(JsonNode.Parse("{\"schemaVersion\":2,\"features\":{\"recap\":{\"version\":1}}}")));
        Assert.Empty(CompanionClient.ParseFeatures(JsonNode.Parse("[]")));
        Assert.Empty(CompanionClient.ParseFeatures(null));
    }

    [Fact]
    public void CompanionFeaturesCannotReplacePluginFeatures()
    {
        var config = new PluginConfiguration { PlayStatsEnabled = true };
        var companion = new Dictionary<string, CapabilityFeature>
        {
            ["popularThisWeek"] = new(9, true),
            ["recommendations"] = new(1, true),
        };
        var manifest = CapabilityManifest.Build(config, companion);
        Assert.Equal(CapabilityManifest.PopularVersion, manifest.Features["popularThisWeek"].Version);
        Assert.Equal(1, manifest.Features["recommendations"].Version);
    }

    [Theory]
    [InlineData(null, "v1/modules/recap")]
    [InlineData("rows/home", "v1/modules/recap/rows/home")]
    [InlineData("a b/c?d", "v1/modules/recap/a%20b/c%3Fd")]
    [InlineData("rows//home/", "v1/modules/recap/rows/home")]
    public void PathsAreEscapedPerSegment(string? path, string expected) =>
        Assert.Equal(expected, CompanionController.BuildPath("recap", path));

    [Theory]
    [InlineData("../capabilities")]
    [InlineData("rows/./x")]
    [InlineData("rows\\..\\x")]
    public void DotSegmentsAreRefused(string path) => Assert.Null(CompanionController.BuildPath("recap", path));

    [Fact]
    public void QueryDropsJellyfinCredentials()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["limit"] = "10",
            ["api_key"] = "secret-token",
            ["ApiKey"] = "secret-token",
            ["tag"] = new StringValues(new[] { "a", "b c" }),
        });
        var forwarded = CompanionController.ForwardedQuery(query);
        Assert.DoesNotContain("secret-token", forwarded, StringComparison.Ordinal);
        Assert.Equal("?limit=10&tag=a&tag=b%20c", forwarded);
        Assert.Equal(string.Empty, CompanionController.ForwardedQuery(new QueryCollection()));
    }

    [Theory]
    [InlineData("connections", false, "v1/admin/settings/connections")]
    [InlineData("recommendations", true, "v1/admin/settings/recommendations/test")]
    [InlineData("liveTvGuide", false, "v1/admin/settings/liveTvGuide")]
    public void SettingsSectionsMapToCompanionPaths(string section, bool test, string expected) =>
        Assert.Equal(expected, CompanionSettingsController.SectionPath(section, test));

    [Theory]
    [InlineData("../modules")]
    [InlineData("a/b")]
    [InlineData("Recommendations")]
    [InlineData("")]
    [InlineData(null)]
    public void SettingsSectionsMustBePlainIds(string? section) => Assert.Null(CompanionSettingsController.SectionPath(section, false));

    [Theory]
    [InlineData(200, 200)]
    [InlineData(404, 404)]
    [InlineData(429, 429)]
    [InlineData(401, 503)]
    [InlineData(0, 503)]
    [InlineData(500, 502)]
    public void StatusesMapToWhatAppsUnderstand(int companion, int expected)
    {
        var result = CompanionController.Map(new CompanionResponse(companion, JsonNode.Parse("{\"ok\":true}")));
        var status = result switch
        {
            ContentResult content => content.StatusCode,
            StatusCodeResult code => code.StatusCode,
            _ => null,
        };
        Assert.Equal(expected, status);
    }

    private static PluginConfiguration Config(string url = "http://companion:9310", string secret = Secret) =>
        new() { CompanionUrl = url, CompanionSecret = secret };

    private sealed class RecordingClient : CompanionClient
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public RecordingClient(HttpStatusCode status, string body)
            : base(null!)
        {
            _status = status;
            _body = body;
            Clock = () => Now;
            NewNonce = () => Nonce;
        }

        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(Timestamp);

        public string Nonce { get; set; } = "ffeeddccbbaa99887766554433221100";

        public bool Fail { get; set; }

        public List<(string Uri, Dictionary<string, string> Headers)> Sent { get; } = new();

        protected override Task<HttpResponseMessage> TransmitAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((request.RequestUri!.ToString(), request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));
            if (Fail)
            {
                throw new HttpRequestException("down");
            }

            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
        }
    }
}
