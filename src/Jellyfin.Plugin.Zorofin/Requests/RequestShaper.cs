using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Zorofin.Requests;

/// <summary>
/// Reduces Seerr and Arr responses to the fields Zorofin apps read. Anything not listed here
/// (emails, other users' accounts, service URLs, file paths) never leaves the server.
/// </summary>
public static class RequestShaper
{
    private static readonly string[] TitleFields =
    {
        "id", "mediaType", "title", "name", "overview", "posterPath", "backdropPath",
        "releaseDate", "firstAirDate", "voteAverage", "runtime"
    };

    private static readonly string[] SeasonFields = { "seasonNumber", "name", "episodeCount", "status", "status4k" };

    /// <summary>Shapes one movie or show from discover, search or details.</summary>
    /// <param name="source">Seerr title JSON.</param>
    /// <returns>The reduced title, or null for people and malformed entries.</returns>
    public static JsonObject? ShapeTitle(JsonNode? source)
    {
        if (source is not JsonObject input || Int(input["id"]) is not > 0)
        {
            return null;
        }

        var mediaType = Str(input["mediaType"]);
        if (mediaType is not null && mediaType != "movie" && mediaType != "tv")
        {
            return null;
        }

        var output = Copy(input, TitleFields);
        if (input["seasons"] is JsonArray seasons)
        {
            output["seasons"] = ShapeSeasons(seasons);
        }

        if (input["genres"] is JsonArray genres)
        {
            output["genres"] = ShapeGenres(genres);
        }

        if (input["episodeRunTime"] is JsonArray runTimes)
        {
            output["episodeRunTime"] = new JsonArray(runTimes.Select(Int).Where(n => n is > 0).Select(n => (JsonNode?)n).ToArray());
        }

        if (Int(input["externalIds"]?["tvdbId"]) is { } tvdbId)
        {
            output["externalIds"] = new JsonObject { ["tvdbId"] = tvdbId };
        }

        if (input["mediaInfo"] is JsonObject mediaInfo)
        {
            var info = Copy(mediaInfo, new[] { "status", "status4k", "jellyfinMediaId", "jellyfinMediaId4k" });
            if (mediaInfo["seasons"] is JsonArray infoSeasons)
            {
                info["seasons"] = ShapeSeasons(infoSeasons);
            }

            if (mediaInfo["requests"] is JsonArray receipts)
            {
                info["requests"] = new JsonArray(receipts.OfType<JsonObject>().Select(ShapeReceipt).ToArray<JsonNode?>());
            }

            output["mediaInfo"] = info;
        }

        return output;
    }

    /// <summary>Shapes a genre list, keeping each genre's id and name.</summary>
    /// <param name="source">Seerr genre JSON array.</param>
    /// <returns>The genres.</returns>
    public static JsonArray ShapeGenres(JsonNode? source) =>
        new((source as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(g => Int(g["id"]) is > 0 && !string.IsNullOrEmpty(Str(g["name"])))
            .Select(g => (JsonNode?)new JsonObject { ["id"] = Int(g["id"]), ["name"] = Str(g["name"]) })
            .ToArray());

    /// <summary>Shapes a discover or search page.</summary>
    /// <param name="source">Seerr page JSON.</param>
    /// <returns>The reduced page.</returns>
    public static JsonObject ShapePage(JsonNode? source)
    {
        var results = (source?["results"] as JsonArray)?.Select(ShapeTitle).Where(t => t is not null).ToArray<JsonNode?>() ?? Array.Empty<JsonNode?>();
        var page = new JsonObject { ["results"] = new JsonArray(results) };
        if (Int(source?["totalPages"]) is { } totalPages)
        {
            page["totalPages"] = totalPages;
        }

        return page;
    }

    /// <summary>Shapes a request receipt, keeping only state the apps display.</summary>
    /// <param name="source">Seerr request JSON.</param>
    /// <returns>The reduced receipt.</returns>
    public static JsonObject ShapeReceipt(JsonObject source)
    {
        var receipt = Copy(source, new[] { "id", "status", "is4k" });
        if (source["seasons"] is JsonArray seasons)
        {
            receipt["seasons"] = new JsonArray(seasons
                .Select(s => Int(s?["seasonNumber"]))
                .Where(n => n is not null)
                .Select(n => (JsonNode?)new JsonObject { ["seasonNumber"] = n })
                .ToArray());
        }

        return receipt;
    }

    /// <summary>Shapes a viewer's quota.</summary>
    /// <param name="source">Seerr quota JSON.</param>
    /// <returns>The reduced quota.</returns>
    public static JsonObject ShapeQuota(JsonNode? source)
    {
        var quota = new JsonObject();
        foreach (var kind in new[] { "movie", "tv" })
        {
            if (source?[kind] is JsonObject limit)
            {
                quota[kind] = Copy(limit, new[] { "limit", "used", "days", "restricted" });
            }
        }

        return quota;
    }

    /// <summary>Maps a Seerr media status number to the label the apps expect.</summary>
    /// <param name="status">Seerr media status.</param>
    /// <returns>The label.</returns>
    public static string MediaStatusLabel(int? status) => status switch
    {
        2 => "Pending",
        3 => "Processing",
        4 => "Partially Available",
        5 => "Available",
        6 => "Blacklisted",
        7 => "Deleted",
        _ => "Unknown",
    };

    /// <summary>Builds one Requests activity row from a Seerr request and its title details.</summary>
    /// <param name="request">Seerr request JSON.</param>
    /// <param name="details">Seerr title details, if they could be read.</param>
    /// <param name="today">Today's date, used for "waiting for release".</param>
    /// <param name="includeRequester">Whether the requester's display name may be shown.</param>
    /// <returns>The activity row, or null when the request is malformed.</returns>
    public static JsonObject? ShapeActivityItem(JsonNode? request, JsonNode? details, DateOnly today, bool includeRequester)
    {
        if (request is not JsonObject input || Int(input["id"]) is not { } id)
        {
            return null;
        }

        var type = Str(input["type"]);
        var is4k = Bool(input["is4k"]) ?? false;
        var media = input["media"];
        var date = Str(details?["releaseDate"]) ?? Str(details?["firstAirDate"]);
        var parsed = DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var release);
        var item = new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["tmdbId"] = Int(media?["tmdbId"]),
            ["title"] = Str(details?["title"]) ?? Str(details?["name"]),
            ["posterUrl"] = PosterUrl(Str(details?["posterPath"])),
            ["year"] = parsed ? release.Year : null,
            ["mediaStatus"] = MediaStatusLabel(Int(is4k ? media?["status4k"] : media?["status"])),
            ["requestStatus"] = Int(input["status"]),
            ["is4k"] = is4k,
            ["isComingSoon"] = parsed && release > today,
            ["isOrphaned"] = false,
        };
        if (includeRequester)
        {
            item["requestedBy"] = Str(input["requestedBy"]?["displayName"]);
        }

        return item;
    }

    /// <summary>Builds download rows for titles this viewer can see. Everything else in the queue stays private.</summary>
    /// <param name="records">Arr queue records.</param>
    /// <param name="source">"radarr" or "sonarr".</param>
    /// <param name="visibleTmdbIds">TMDB ids covered by requests the viewer can see.</param>
    /// <returns>Download rows.</returns>
    public static IEnumerable<JsonObject> ShapeQueue(JsonArray? records, string source, IReadOnlySet<int> visibleTmdbIds)
    {
        var owner = source == "radarr" ? "movie" : "series";
        foreach (var record in records?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            if (Int(record[owner]?["tmdbId"]) is not { } tmdbId || !visibleTmdbIds.Contains(tmdbId))
            {
                continue;
            }

            var size = Dbl(record["size"]);
            var left = Dbl(record["sizeleft"]);
            double? progress = size is > 0 && left is not null ? Math.Round(Math.Clamp((size.Value - left.Value) / size.Value * 100, 0, 100), 1) : null;
            var state = Str(record["trackedDownloadState"]);
            var status = Str(record["trackedDownloadStatus"]);
            var label = string.Join(' ', new[] { Str(record["status"]), state, status is "warning" or "error" ? status : null }.Where(s => !string.IsNullOrEmpty(s)));
            yield return new JsonObject { ["tmdbId"] = tmdbId, ["source"] = source, ["progress"] = progress, ["status"] = label };
        }
    }

    /// <summary>Builds a TMDB poster URL from a Seerr poster path, accepting only plain file names.</summary>
    /// <param name="posterPath">Poster path such as "/abc.jpg".</param>
    /// <returns>The URL or null.</returns>
    public static string? PosterUrl(string? posterPath)
    {
        if (posterPath is null || posterPath.Length < 6 || posterPath[0] != '/'
            || !posterPath.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            || !(posterPath.EndsWith(".jpg", StringComparison.Ordinal) || posterPath.EndsWith(".png", StringComparison.Ordinal) || posterPath.EndsWith(".webp", StringComparison.Ordinal)))
        {
            return null;
        }

        return "https://image.tmdb.org/t/p/w342" + posterPath;
    }

    internal static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        return value.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;
    }

    internal static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    internal static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : null;

    private static double? Dbl(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    private static JsonArray ShapeSeasons(JsonArray seasons) =>
        new(seasons.OfType<JsonObject>().Where(s => Int(s["seasonNumber"]) is not null).Select(s => (JsonNode?)Copy(s, SeasonFields)).ToArray());

    private static JsonObject Copy(JsonObject source, IEnumerable<string> fields)
    {
        var output = new JsonObject();
        foreach (var field in fields)
        {
            if (source[field] is JsonValue value)
            {
                output[field] = value.DeepClone();
            }
        }

        return output;
    }
}
