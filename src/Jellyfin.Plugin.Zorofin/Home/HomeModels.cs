using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Zorofin.Home;

/// <summary>
/// What the row rules need to know about one library item, read with the viewer's own access and user data.
/// Kept free of Jellyfin types so the rules can be tested on plain values.
/// </summary>
public sealed record HomeEntry
{
    /// <summary>Gets the item id.</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the item name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the Jellyfin item kind, such as Movie, Series, Episode or TvChannel.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets the overview, if any.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the genres.</summary>
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();

    /// <summary>Gets the tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Gets the full runtime in ticks.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets when the item was added to the library (UTC).</summary>
    public DateTime? DateCreated { get; init; }

    /// <summary>Gets a value indicating whether the viewer has watched it.</summary>
    public bool Played { get; init; }

    /// <summary>Gets the viewer's resume position in ticks.</summary>
    public long PositionTicks { get; init; }

    /// <summary>Gets when the viewer last played it (UTC).</summary>
    public DateTime? LastPlayed { get; init; }

    /// <summary>Gets a value indicating whether the viewer marked it as a favorite.</summary>
    public bool Favorite { get; init; }

    /// <summary>Gets the series an episode belongs to.</summary>
    public Guid? SeriesId { get; init; }

    /// <summary>Gets the series name of an episode.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets a value indicating whether the item is a real local file that can be played.</summary>
    public bool Playable { get; init; } = true;

    /// <summary>Gets the verified source formats (4K, HDR10, Dolby Vision...), when they were read.</summary>
    public IReadOnlyList<string> Formats { get; init; } = Array.Empty<string>();

    /// <summary>Gets the community rating.</summary>
    public double? CommunityRating { get; init; }
}

/// <summary>One broadcast from the Live TV guide.</summary>
/// <param name="Id">Programme id.</param>
/// <param name="ChannelId">Channel id.</param>
/// <param name="Name">Programme title.</param>
/// <param name="Overview">Programme description.</param>
/// <param name="Start">Start (UTC).</param>
/// <param name="End">End (UTC).</param>
/// <param name="IsSports">Whether the guide marks it as sports.</param>
/// <param name="Genres">Guide genres.</param>
public sealed record GuideProgram(Guid Id, Guid ChannelId, string Name, string? Overview, DateTime Start, DateTime End, bool IsSports, IReadOnlyList<string> Genres);

/// <summary>One of the viewer's own Seerr requests that Seerr marks as available.</summary>
/// <param name="Type">"movie" or "tv".</param>
/// <param name="TmdbId">TMDb id.</param>
/// <param name="Is4K">Whether it was a 4K request.</param>
/// <param name="Seasons">Requested season numbers (shows).</param>
/// <param name="AddedAt">When Seerr saw it arrive, if known.</param>
public sealed record AvailableRequest(string Type, int TmdbId, bool Is4K, IReadOnlyList<int> Seasons, DateTime? AddedAt);

/// <summary>One card on a Home row.</summary>
/// <param name="Id">Card id (the item id; the programme id for sports).</param>
/// <param name="Entry">The item the card opens.</param>
/// <param name="Subtitle">Short context text.</param>
/// <param name="Badges">Badges, such as "New episode" or "4K".</param>
/// <param name="StartsAt">Broadcast start, for sports.</param>
/// <param name="EndsAt">Broadcast end, for sports.</param>
public sealed record HomeCard(Guid Id, HomeEntry Entry, string Subtitle, IReadOnlyList<string> Badges, DateTime? StartsAt = null, DateTime? EndsAt = null);

/// <summary>Where a row sits on Home.</summary>
public enum HomeRowPlacement
{
    /// <summary>Always at the top, in a fixed order: Continue Watching, then My requests.</summary>
    Pinned,

    /// <summary>Picked for this viewer; mixed in among the library rows like the apps do.</summary>
    Personal,

    /// <summary>A plain library shelf, such as Recently added movies.</summary>
    Library,
}

/// <summary>A built row.</summary>
/// <param name="Id">Stable row id.</param>
/// <param name="Title">Row title.</param>
/// <param name="Placement">Where the row sits.</param>
/// <param name="State">ready, empty or unavailable.</param>
/// <param name="Cards">Cards, at most 20.</param>
public sealed record HomeRowResult(string Id, string Title, HomeRowPlacement Placement, string State, IReadOnlyList<HomeCard> Cards);
