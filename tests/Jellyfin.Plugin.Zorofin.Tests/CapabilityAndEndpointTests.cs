using Jellyfin.Plugin.Zorofin;
using Jellyfin.Plugin.Zorofin.Capabilities;
using Jellyfin.Plugin.Zorofin.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class CapabilityAndEndpointTests
{
    [Fact]
    public void FreshInstallAdvertisesNothing()
    {
        var manifest = CapabilityManifest.Build(new PluginConfiguration());
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Empty(manifest.Features);
    }

    [Fact]
    public void RequestsNeedsSwitchUrlAndKey()
    {
        var config = new PluginConfiguration { RequestsEnabled = true, SeerrUrl = "http://seerr:5055" };
        Assert.False(CapabilityManifest.Build(config).Features.ContainsKey("requests"));

        config.SeerrApiKey = "key";
        var feature = CapabilityManifest.Build(config).Features["requests"];
        Assert.Equal(2, feature.Version);
        Assert.True(feature.Enabled);

        config.RequestsEnabled = false;
        Assert.False(CapabilityManifest.Build(config).Features.ContainsKey("requests"));
    }

    [Fact]
    public void PopularFollowsItsSwitch()
    {
        Assert.True(CapabilityManifest.Build(new PluginConfiguration { PlayStatsEnabled = true }).Features.ContainsKey("popularThisWeek"));
    }

    [Theory]
    [InlineData("ftp://seerr")]
    [InlineData("seerr:5055")]
    [InlineData("http://user:pass@seerr")]
    [InlineData("http://seerr/?x=1")]
    [InlineData("")]
    public void RejectsUnsafeOrIncompleteUrls(string url)
    {
        Assert.False(ServiceEndpoint.TryCreate(url, "key", out _));
    }

    [Fact]
    public void BasePathKeepsSubdirectory()
    {
        Assert.True(ServiceEndpoint.TryCreate("https://host/seerr", " key ", out var endpoint));
        Assert.Equal("https://host/seerr/api/v1/request", endpoint.Resolve("api/v1/request").AbsoluteUri);
        Assert.Equal("key", endpoint.ApiKey);
        Assert.False(ServiceEndpoint.TryCreate("https://host", "  ", out _));
    }
}
