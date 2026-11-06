using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Zorofin.Requests;

/// <summary>A viewer's request, in the same shape the apps already send.</summary>
public sealed class RequestIntent
{
    /// <summary>Gets or sets "movie" or "tv".</summary>
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("mediaId")]
    public int MediaId { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a 4K request.</summary>
    [JsonPropertyName("is4k")]
    public bool Is4K { get; set; }

    /// <summary>Gets or sets the selected seasons for a show.</summary>
    [JsonPropertyName("seasons")]
    public IReadOnlyList<int>? Seasons { get; set; }

    /// <summary>Gets or sets the TVDB id for a show.</summary>
    [JsonPropertyName("tvdbId")]
    public int? TvdbId { get; set; }

    /// <summary>Checks the intent before anything is sent upstream.</summary>
    /// <returns>Whether it is well formed.</returns>
    public bool IsValid()
    {
        if (MediaId <= 0 || TvdbId is <= 0)
        {
            return false;
        }

        return MediaType switch
        {
            "movie" => Seasons is null || Seasons.Count == 0,
            "tv" => Seasons is { Count: > 0 and <= 100 } && Seasons.All(s => s is >= 0 and <= 500) && Seasons.Distinct().Count() == Seasons.Count,
            _ => false,
        };
    }

    /// <summary>Builds Seerr's request body.</summary>
    /// <returns>The body.</returns>
    public JsonObject ToSeerrBody()
    {
        var body = new JsonObject { ["mediaType"] = MediaType, ["mediaId"] = MediaId, ["is4k"] = Is4K };
        if (MediaType == "tv")
        {
            body["seasons"] = new JsonArray(Seasons!.Select(s => (JsonNode?)s).ToArray());
            if (TvdbId is { } tvdb)
            {
                body["tvdbId"] = tvdb;
            }
        }

        return body;
    }
}
