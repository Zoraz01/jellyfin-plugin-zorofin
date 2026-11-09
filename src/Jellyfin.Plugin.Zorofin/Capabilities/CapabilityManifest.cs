using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Zorofin.Configuration;

namespace Jellyfin.Plugin.Zorofin.Capabilities;

/// <summary>One advertised feature.</summary>
/// <param name="Version">Contract version the client must understand.</param>
/// <param name="Enabled">Whether the feature is available to this viewer.</param>
public sealed record CapabilityFeature(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>The schema-version-1 manifest clients read after sign-in.</summary>
/// <param name="SchemaVersion">Manifest schema version.</param>
/// <param name="Features">Advertised features keyed by name.</param>
public sealed record CapabilityManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("features")] IReadOnlyDictionary<string, CapabilityFeature> Features)
{
    /// <summary>Requests through the Zorofin routes. Version 1 means the older Jellyfin Enhanced proxy.</summary>
    public const int RequestsVersion = 2;

    /// <summary>Popular-this-week statistics.</summary>
    public const int PopularVersion = 1;

    /// <summary>Home rows at /Zorofin/Home/Rows. Version 1 meant the older /support/home/rows feed.</summary>
    public const int HomeRowsVersion = 2;

    /// <summary>Every web Home row at /Zorofin/Home/Sections.</summary>
    public const int HomeSectionsVersion = 1;

    /// <summary>The announcement banner at /Zorofin/Announcement.</summary>
    public const int AnnouncementsVersion = 1;

    /// <summary>
    /// Features the Zorofin apps knew before the plugin existed, all at version 1. A server that still serves them
    /// itself can keep listing them (<see cref="PluginConfiguration.LegacyAppFeatures"/>); the plugin serves none.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyFeatureNames = new[]
    {
        "support", "notifications", "requests", "homeRows", "liveArtwork",
        "enhancedSettings", "accountApprovals", "notificationSettings", "liveTVAvailability",
    };

    /// <summary>Reads the operator's older app features, keeping only known names, in the order of <see cref="LegacyFeatureNames"/>.</summary>
    /// <param name="value">Comma-separated names.</param>
    /// <returns>The known names that were listed.</returns>
    public static IReadOnlyList<string> ParseLegacyFeatures(string? value)
    {
        var listed = new HashSet<string>(
            (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);
        return LegacyFeatureNames.Where(listed.Contains).ToArray();
    }

    /// <summary>
    /// Builds the manifest. Only enabled, fully configured modules are listed; listing is not authorization,
    /// and every route still checks the caller.
    /// </summary>
    /// <param name="config">Current plugin configuration.</param>
    /// <param name="companionFeatures">Features the optional companion advertises; they never replace plugin features.</param>
    /// <returns>The manifest.</returns>
    public static CapabilityManifest Build(PluginConfiguration config, IReadOnlyDictionary<string, CapabilityFeature>? companionFeatures = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var features = new Dictionary<string, CapabilityFeature>(StringComparer.Ordinal);
        if (config.RequestsEnabled && ServiceEndpoint.TryCreate(config.SeerrUrl, config.SeerrApiKey, out _))
        {
            features["requests"] = new CapabilityFeature(RequestsVersion, true);
        }

        if (config.PlayStatsEnabled)
        {
            features["popularThisWeek"] = new CapabilityFeature(PopularVersion, true);
        }

        if (config.HomeRowsEnabled)
        {
            features["homeRows"] = new CapabilityFeature(HomeRowsVersion, true);
            features["homeSections"] = new CapabilityFeature(HomeSectionsVersion, true);
        }

        if (config.AnnouncementEnabled)
        {
            features["announcements"] = new CapabilityFeature(AnnouncementsVersion, true);
        }

        foreach (var (name, feature) in companionFeatures ?? new Dictionary<string, CapabilityFeature>())
        {
            features.TryAdd(name, feature);
        }

        // Older apps only understand version 1 of these and stop asking the server's own adapter once this
        // manifest exists, so a listed name wins over the plugin's or the companion's newer version.
        foreach (var name in ParseLegacyFeatures(config.LegacyAppFeatures))
        {
            features[name] = new CapabilityFeature(1, true);
        }

        return new CapabilityManifest(1, features);
    }
}
