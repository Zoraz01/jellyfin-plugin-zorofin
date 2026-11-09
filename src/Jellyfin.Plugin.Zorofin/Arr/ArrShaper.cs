using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Requests;

namespace Jellyfin.Plugin.Zorofin.Arr;

/// <summary>
/// Turns Sonarr and Radarr answers into the small shapes the web Downloads tab, Calendar and admin links use.
/// Only display fields leave the server: no paths, download client names, indexers or API keys.
/// </summary>
public static class ArrShaper
{
    private static readonly string[] ImageHosts = { "image.tmdb.org", "artworks.thetvdb.com", "www.thetvdb.com", "thetvdb.com" };

    /// <summary>Shapes queue records for the Downloads tab.</summary>
    /// <param name="records">Queue records.</param>
    /// <param name="source">"sonarr" or "radarr".</param>
    /// <param name="visibleTmdbIds">Titles the viewer may see, or null for every title.</param>
    /// <returns>Downloads, one per record.</returns>
    public static IEnumerable<JsonObject> Downloads(JsonArray? records, string source, IReadOnlySet<int>? visibleTmdbIds)
    {
        var owner = source == "radarr" ? "movie" : "series";
        foreach (var record in records?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var title = record[owner] as JsonObject;
            var tmdbId = RequestShaper.Int(title?["tmdbId"]);
            if (visibleTmdbIds is not null && (tmdbId is null || !visibleTmdbIds.Contains(tmdbId.Value)))
            {
                continue;
            }

            var size = ExtrasNumber(record["size"]);
            var left = ExtrasNumber(record["sizeleft"]);
            double? progress = size is > 0 && left is not null ? Math.Round(Math.Clamp((size.Value - left.Value) / size.Value * 100, 0, 100), 1) : null;
            var episode = record["episode"] as JsonObject;
            var state = RequestShaper.Str(record["trackedDownloadState"]) ?? string.Empty;
            var health = RequestShaper.Str(record["trackedDownloadStatus"]) ?? string.Empty;
            var status = RequestShaper.Str(record["status"]) ?? string.Empty;
            yield return new JsonObject
            {
                ["id"] = source + ":" + (RequestShaper.Int(record["id"])?.ToString(CultureInfo.InvariantCulture) ?? "0"),
                ["source"] = source,
                ["kind"] = source == "radarr" ? "movie" : "episode",
                ["title"] = RequestShaper.Str(title?["title"]) ?? RequestShaper.Str(record["title"]) ?? "Unknown",
                ["year"] = RequestShaper.Int(title?["year"]),
                ["subtitle"] = episode is null ? null : EpisodeLabel(episode),
                ["tmdbId"] = tmdbId,
                ["tvdbId"] = RequestShaper.Int(title?["tvdbId"]),
                ["posterUrl"] = Poster(title?["images"] as JsonArray),
                ["quality"] = RequestShaper.Str(record["quality"]?["quality"]?["name"]),
                ["progress"] = progress,
                ["size"] = size is > 0 ? (long)size.Value : null,
                ["sizeLeft"] = left is >= 0 ? (long)left.Value : null,
                ["timeLeft"] = RequestShaper.Str(record["timeleft"]),
                ["eta"] = RequestShaper.Str(record["estimatedCompletionTime"]),
                ["state"] = State(status, state, health),
            };
        }
    }

    /// <summary>One word for where a download is: queued, downloading, paused, importing, stalled or failed.</summary>
    /// <param name="status">Queue status.</param>
    /// <param name="state">Tracked download state.</param>
    /// <param name="health">Tracked download status.</param>
    /// <returns>The state.</returns>
    public static string State(string status, string state, string health)
    {
        if (string.Equals(health, "error", StringComparison.OrdinalIgnoreCase) || string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        if (state.StartsWith("import", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(health, "warning", StringComparison.OrdinalIgnoreCase) ? "stalled" : "importing";
        }

        if (string.Equals(health, "warning", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase))
        {
            return "stalled";
        }

        return status.ToLowerInvariant() switch
        {
            "paused" => "paused",
            "queued" or "delay" or "downloadclientunavailable" => "queued",
            "completed" => "importing",
            _ => "downloading",
        };
    }

    /// <summary>Shapes Sonarr calendar episodes.</summary>
    /// <param name="episodes">Sonarr's calendar answer (with series).</param>
    /// <returns>Events.</returns>
    public static IEnumerable<JsonObject> CalendarEpisodes(JsonArray? episodes)
    {
        foreach (var e in episodes?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var series = e["series"] as JsonObject;
            var at = RequestShaper.Str(e["airDateUtc"]);
            if (series is null || at is null || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            {
                continue;
            }

            yield return new JsonObject
            {
                ["kind"] = "episode",
                ["title"] = RequestShaper.Str(series["title"]) ?? "Unknown",
                ["subtitle"] = EpisodeLabel(e),
                ["at"] = when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["allDay"] = false,
                ["release"] = "airing",
                ["hasFile"] = RequestShaper.Bool(e["hasFile"]) == true,
                ["premiere"] = RequestShaper.Int(e["episodeNumber"]) == 1,
                ["tmdbId"] = RequestShaper.Int(series["tmdbId"]),
                ["tvdbId"] = RequestShaper.Int(series["tvdbId"]),
                ["posterUrl"] = Poster(series["images"] as JsonArray),
                ["network"] = RequestShaper.Str(series["network"]),
            };
        }
    }

    /// <summary>Shapes Radarr calendar movies: one event per release (cinema, digital, physical) inside the window.</summary>
    /// <param name="movies">Radarr's calendar answer.</param>
    /// <param name="start">Window start.</param>
    /// <param name="end">Window end.</param>
    /// <returns>Events.</returns>
    public static IEnumerable<JsonObject> CalendarMovies(JsonArray? movies, DateTimeOffset start, DateTimeOffset end)
    {
        foreach (var m in movies?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            foreach (var (field, release) in new[] { ("inCinemas", "cinema"), ("digitalRelease", "digital"), ("physicalRelease", "physical") })
            {
                var at = RequestShaper.Str(m[field]);
                if (at is null || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) || when < start.AddDays(-1) || when > end)
                {
                    continue;
                }

                yield return new JsonObject
                {
                    ["kind"] = "movie",
                    ["title"] = RequestShaper.Str(m["title"]) ?? "Unknown",
                    ["subtitle"] = RequestShaper.Int(m["year"])?.ToString(CultureInfo.InvariantCulture),
                    ["at"] = when.UtcDateTime.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["allDay"] = true,
                    ["release"] = release,
                    ["hasFile"] = RequestShaper.Bool(m["hasFile"]) == true,
                    ["premiere"] = false,
                    ["tmdbId"] = RequestShaper.Int(m["tmdbId"]),
                    ["tvdbId"] = null,
                    ["posterUrl"] = Poster(m["images"] as JsonArray),
                    ["network"] = RequestShaper.Str(m["studio"]),
                };
            }
        }
    }

    /// <summary>Admin links and tags for one title in Sonarr or Radarr.</summary>
    /// <param name="source">"sonarr" or "radarr".</param>
    /// <param name="title">The series or movie record.</param>
    /// <param name="tags">Tag id to label map.</param>
    /// <param name="publicBase">Address the admin opens, ending with a slash.</param>
    /// <returns>The link and tag labels, or null when the record has no slug.</returns>
    public static JsonObject? Link(string source, JsonObject? title, IReadOnlyDictionary<int, string> tags, Uri publicBase)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(publicBase);
        var slug = RequestShaper.Str(title?["titleSlug"]);
        if (title is null || string.IsNullOrEmpty(slug))
        {
            return null;
        }

        var labels = (title["tags"] as JsonArray ?? new JsonArray())
            .Select(t => RequestShaper.Int(t))
            .Where(t => t is not null && tags.ContainsKey(t.Value))
            .Select(t => (JsonNode)tags[t!.Value])
            .ToArray();
        return new JsonObject
        {
            ["source"] = source,
            ["name"] = source == "sonarr" ? "Sonarr" : "Radarr",
            ["url"] = new Uri(publicBase, (source == "sonarr" ? "series/" : "movie/") + Uri.EscapeDataString(slug)).AbsoluteUri,
            ["monitored"] = RequestShaper.Bool(title["monitored"]),
            ["tags"] = new JsonArray(labels),
        };
    }

    /// <summary>Reads Sonarr/Radarr's tag list into an id to label map.</summary>
    /// <param name="tags">The tag answer.</param>
    /// <returns>The map.</returns>
    public static IReadOnlyDictionary<int, string> Tags(JsonArray? tags) =>
        (tags ?? new JsonArray()).OfType<JsonObject>()
            .Select(t => (Id: RequestShaper.Int(t["id"]), Label: RequestShaper.Str(t["label"])))
            .Where(t => t.Id is not null && !string.IsNullOrWhiteSpace(t.Label))
            .GroupBy(t => t.Id!.Value)
            .ToDictionary(g => g.Key, g => g.First().Label!);

    /// <summary>A poster URL from an image list, only from known artwork hosts over https.</summary>
    /// <param name="images">Image list.</param>
    /// <returns>The URL or null.</returns>
    public static string? Poster(JsonArray? images)
    {
        var poster = images?.OfType<JsonObject>().FirstOrDefault(i => RequestShaper.Str(i["coverType"]) == "poster");
        var url = RequestShaper.Str(poster?["remoteUrl"]);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !ImageHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        // TMDB serves smaller posters under w342; full size is not needed for cards.
        return uri.Host == "image.tmdb.org" ? uri.AbsoluteUri.Replace("/t/p/original/", "/t/p/w342/", StringComparison.Ordinal) : uri.AbsoluteUri;
    }

    private static string EpisodeLabel(JsonObject episode)
    {
        var season = RequestShaper.Int(episode["seasonNumber"]);
        var number = RequestShaper.Int(episode["episodeNumber"]);
        var name = RequestShaper.Str(episode["title"]);
        var code = season is not null && number is not null ? string.Create(CultureInfo.InvariantCulture, $"S{season}:E{number}") : null;
        return string.Join(" · ", new[] { code, string.Equals(name, "TBA", StringComparison.OrdinalIgnoreCase) ? null : name }.Where(x => !string.IsNullOrEmpty(x)));
    }

    private static double? ExtrasNumber(JsonNode? node) => Extras.ExtrasModels.Number(node);
}
