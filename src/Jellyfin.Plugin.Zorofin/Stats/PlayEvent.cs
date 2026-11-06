using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Zorofin.Stats;

/// <summary>
/// One finished play in the admin play history. Names are copied at the time of the play so reports still
/// read correctly after a user or title is deleted.
/// </summary>
public sealed record PlayEvent
{
    /// <summary>Gets the event id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets when playback started (UTC).</summary>
    [JsonPropertyName("started")]
    public DateTime StartedUtc { get; init; }

    /// <summary>Gets the time actually spent playing, in seconds. Paused time is not counted.</summary>
    [JsonPropertyName("seconds")]
    public int Seconds { get; init; }

    /// <summary>Gets the Jellyfin user id.</summary>
    [JsonPropertyName("userId")]
    public Guid UserId { get; init; }

    /// <summary>Gets the user's name at the time of the play.</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; init; } = string.Empty;

    /// <summary>Gets the played item id.</summary>
    [JsonPropertyName("itemId")]
    public Guid ItemId { get; init; }

    /// <summary>Gets the item kind, such as Movie, Episode, Audio or TvChannel.</summary>
    [JsonPropertyName("itemType")]
    public string ItemType { get; init; } = string.Empty;

    /// <summary>Gets the item's name at the time of the play.</summary>
    [JsonPropertyName("itemName")]
    public string ItemName { get; init; } = string.Empty;

    /// <summary>Gets the series id for an episode.</summary>
    [JsonPropertyName("seriesId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SeriesId { get; init; }

    /// <summary>Gets the series name for an episode.</summary>
    [JsonPropertyName("seriesName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SeriesName { get; init; }

    /// <summary>Gets the season number for an episode.</summary>
    [JsonPropertyName("season")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Season { get; init; }

    /// <summary>Gets the episode number for an episode.</summary>
    [JsonPropertyName("episode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Episode { get; init; }

    /// <summary>Gets the client app name.</summary>
    [JsonPropertyName("client")]
    public string Client { get; init; } = string.Empty;

    /// <summary>Gets the device name.</summary>
    [JsonPropertyName("device")]
    public string Device { get; init; } = string.Empty;

    /// <summary>Gets how the stream was delivered: DirectPlay, DirectStream or Transcode.</summary>
    [JsonPropertyName("method")]
    public string PlayMethod { get; init; } = string.Empty;

    /// <summary>Gets what was transcoded, such as "video, audio", when the method is Transcode.</summary>
    [JsonPropertyName("transcode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transcode { get; init; }

    /// <summary>Gets a value indicating whether the item was played to the end.</summary>
    [JsonPropertyName("completed")]
    public bool Completed { get; init; }

    /// <summary>Gets where the event came from when it was imported, such as "playbackReporting:123".</summary>
    [JsonPropertyName("importKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImportKey { get; init; }

    /// <summary>Gets the title this play counts toward: the series for an episode, otherwise the item.</summary>
    [JsonIgnore]
    public Guid TitleId => SeriesId is { } series && series != Guid.Empty ? series : ItemId;

    /// <summary>Gets the title's display name: the series for an episode, otherwise the item.</summary>
    [JsonIgnore]
    public string TitleName => string.IsNullOrEmpty(SeriesName) ? ItemName : SeriesName;
}
