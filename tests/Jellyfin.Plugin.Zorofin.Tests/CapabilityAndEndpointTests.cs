using System.Collections.Generic;
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
    public void LegacyAppFeaturesAreListedAtVersionOneAndWin()
    {
        var config = new PluginConfiguration
        {
            RequestsEnabled = true,
            SeerrUrl = "http://seerr:5055",
            SeerrApiKey = "key",
            HomeRowsEnabled = true,
            LegacyAppFeatures = " support, requests ,homeRows,unknown,,support",
        };
        var features = CapabilityManifest.Build(config, new Dictionary<string, CapabilityFeature> { ["support"] = new(3, true) }).Features;
        Assert.Equal(new CapabilityFeature(1, true), features["support"]);
        Assert.Equal(new CapabilityFeature(1, true), features["requests"]);
        Assert.Equal(new CapabilityFeature(1, true), features["homeRows"]);
        Assert.Equal(new CapabilityFeature(CapabilityManifest.HomeSectionsVersion, true), features["homeSections"]);
        Assert.False(features.ContainsKey("unknown"));
        Assert.Equal(new[] { "support", "requests", "homeRows" }, CapabilityManifest.ParseLegacyFeatures(config.LegacyAppFeatures));
    }

    [Fact]
    public void NoLegacyAppFeaturesByDefault()
    {
        Assert.Empty(CapabilityManifest.ParseLegacyFeatures(new PluginConfiguration().LegacyAppFeatures));
        Assert.Empty(CapabilityManifest.ParseLegacyFeatures(null));
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
