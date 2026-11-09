using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Configuration;

namespace Jellyfin.Plugin.Zorofin.Extras;

/// <summary>
/// Pure helpers for the web extras that replace Media Bar and Jellyfin Enhanced: which parts are on, the banner's
/// hand-picked titles, SponsorBlock skip points, MDBList ratings and the plugins Zorofin expects alongside it.
/// </summary>
public static class ExtrasModels
{
    /// <summary>Most hand-picked banner titles honoured.</summary>
    public const int MaxBannerItems = 30;

    /// <summary>
    /// Plugins Zorofin relies on for data it doesn't fetch itself (owner decision, November 7, 2026). Each entry is the
    /// plugin's display name in Jellyfin's catalogue and what it supplies.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Why)> Prerequisites = new[]
    {
        ("TheTVDB", "Show and episode details, so seasons and episode order match the apps."),
        ("TheSportsDB", "Details for sports recordings."),
        ("Open Subtitles", "Subtitle search from the player."),
        ("Intro Skipper", "Skip Intro and Skip Credits in every Zorofin app."),
    };

    // MDBList rating sources shown, in order, with the label viewers see.
    private static readonly (string Source, string Label)[] RatingSources =
    {
        ("imdb", "IMDb"), ("tmdb", "TMDB"), ("tomatoes", "Rotten Tomatoes"), ("popcorn", "Audience"),
        ("metacritic", "Metacritic"), ("letterboxd", "Letterboxd"), ("trakt", "Trakt"), ("myanimelist", "MyAnimeList"),
    };

    /// <summary>Parses the hand-picked banner list: valid item ids, in order, without repeats.</summary>
    /// <param name="value">Comma, space or line separated ids.</param>
    /// <returns>Ids in the "N" format.</returns>
    public static IReadOnlyList<string> BannerIds(string? value) =>
        (value ?? string.Empty)
            .Split(new[] { ',', ' ', '\n', '\r', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => Guid.TryParse(x, out var id) && id != Guid.Empty ? id.ToString("N", CultureInfo.InvariantCulture) : null)
            .Where(x => x is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(MaxBannerItems)
            .ToList();

    /// <summary>Which web extras are on for this viewer.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="admin">Whether the viewer is an administrator.</param>
    /// <returns>The manifest the web pack reads.</returns>
    public static JsonObject Manifest(PluginConfiguration config, bool admin)
    {
        ArgumentNullException.ThrowIfNull(config);
        var arr = HasArr(config);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["banner"] = new JsonObject
            {
                ["itemIds"] = new JsonArray(BannerIds(config.HomeBannerItemIds).Select(id => (JsonNode)id).ToArray()),
                ["trailers"] = config.HomeBannerTrailers,
                ["skipIntros"] = config.HomeBannerTrailers && config.HomeBannerSkipIntros,
            },
            ["calendar"] = config.CalendarEnabled && arr,
            ["downloads"] = config.DownloadsEnabled && arr,
            ["arrLinks"] = admin && config.ArrLinksEnabled && arr,
            ["ratings"] = config.RatingsEnabled && !string.IsNullOrWhiteSpace(config.MdbListApiKey),
            ["catchUp"] = config.CatchUpEnabled,
            ["catchUpLog"] = admin && config.CatchUpEnabled && config.CatchUpLogEnabled,
        };
    }

    /// <summary>Whether Sonarr or Radarr is set up.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>True when at least one is usable.</returns>
    public static bool HasArr(PluginConfiguration config) =>
        config is not null && (ServiceEndpoint.TryCreate(config.SonarrUrl, config.SonarrApiKey, out _) || ServiceEndpoint.TryCreate(config.RadarrUrl, config.RadarrApiKey, out _));

    /// <summary>
    /// Where a trailer should start: the end of an intro, sponsor or self-promotion segment that begins in the first
    /// few seconds, following chained segments. Zero when there is nothing to skip.
    /// </summary>
    /// <param name="segments">SponsorBlock's skipSegments answer.</param>
    /// <returns>Start in whole seconds.</returns>
    public static int TrailerStart(JsonArray? segments)
    {
        var spans = (segments ?? new JsonArray()).OfType<JsonObject>()
            .Select(s => s["segment"] as JsonArray)
            .Where(a => a is { Count: 2 })
            .Select(a => (Start: Number(a![0]), End: Number(a[1])))
            .Where(x => x.Start is >= 0 && x.End > x.Start)
            .OrderBy(x => x.Start)
            .ToList();
        double at = 0;
        foreach (var (start, end) in spans)
        {
            if (start <= at + 3)
            {
                at = Math.Max(at, end!.Value);
            }
        }

        return at > 0 && at < 600 ? (int)Math.Ceiling(at) : 0;
    }

    /// <summary>Keeps MDBList's ratings viewers see: known sources with a score, in a fixed order.</summary>
    /// <param name="body">MDBList's title answer.</param>
    /// <returns>Ratings and a link to the title on IMDb when known.</returns>
    public static JsonObject Ratings(JsonNode? body)
    {
        var list = new JsonArray();
        var ratings = body?["ratings"] as JsonArray ?? new JsonArray();
        foreach (var (source, label) in RatingSources)
        {
            var hit = ratings.OfType<JsonObject>().FirstOrDefault(r => string.Equals((string?)Text(r["source"]), source, StringComparison.OrdinalIgnoreCase));
            var value = Number(hit?["value"]);
            if (hit is null || value is null or <= 0)
            {
                continue;
            }

            var votes = Number(hit["votes"]);
            list.Add(new JsonObject
            {
                ["source"] = source,
                ["label"] = label,
                ["value"] = Math.Round(value.Value, 1),
                ["score"] = Number(hit["score"]) is { } score ? Math.Round(score) : null,
                ["votes"] = votes is > 0 ? (long)votes.Value : null,
            });
        }

        var imdb = Text(body?["ids"]?["imdb"]) ?? Text(body?["imdbid"]);
        return new JsonObject
        {
            ["ratings"] = list,
            ["imdbUrl"] = imdb is { Length: > 2 } && imdb.StartsWith("tt", StringComparison.Ordinal) && imdb.Skip(2).All(char.IsAsciiDigit)
                ? "https://www.imdb.com/title/" + imdb + "/" : null,
        };
    }

    /// <summary>Whether a YouTube video id looks valid (11 letters, digits, - or _).</summary>
    /// <param name="id">Video id.</param>
    /// <returns>True when valid.</returns>
    public static bool YouTubeId(string? id) =>
        id is { Length: 11 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        return value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
