using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Zorofin.Home;

/// <summary>
/// Row rules shared with the apps (apps/apple ClientHomeRows.swift, apps/tv home.dart) and the older
/// support-agent/home-rows.js, so a row means the same thing on every screen.
/// </summary>
public static class HomeRules
{
    /// <summary>Most cards on one row.</summary>
    public const int CardLimit = 20;

    /// <summary>How recent "new" is, in days.</summary>
    public const int RecentDays = 14;

    /// <summary>The short-movie limit in minutes.</summary>
    public const int ShortMovieMinutes = 90;

    /// <summary>How far ahead the sports row looks.</summary>
    public static readonly TimeSpan SportsWindow = TimeSpan.FromHours(12);

    private static readonly Regex NotLive = new(@"\b(highlights?|replay|classic|preview|studio|sportscenter|news|talk)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Separators = new(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Source-format badges from video streams, never from titles or codecs.</summary>
    /// <param name="videoStreams">Video streams of every version: width, VideoRangeType and VideoRange.</param>
    /// <returns>Badges, such as 4K and Dolby Vision.</returns>
    public static IReadOnlyList<string> Formats(IEnumerable<(int? Width, string? RangeType, string? Range)> videoStreams)
    {
        var badges = new List<string>();
        void Add(string badge)
        {
            if (!badges.Contains(badge, StringComparer.Ordinal))
            {
                badges.Add(badge);
            }
        }

        foreach (var (width, rangeType, range) in videoStreams)
        {
            if (width >= 3840)
            {
                Add("4K");
            }

            var type = (rangeType ?? string.Empty).ToUpperInvariant();
            if (type.StartsWith("DOVI", StringComparison.Ordinal))
            {
                Add("Dolby Vision");
            }
            else if (type == "HDR10PLUS")
            {
                Add("HDR10+");
            }
            else if (type == "HDR10")
            {
                Add("HDR10");
            }
            else if (type == "HLG")
            {
                Add("HLG");
            }
            else if (string.Equals(range, "HDR", StringComparison.OrdinalIgnoreCase))
            {
                Add("HDR");
            }
        }

        return badges;
    }

    /// <summary>
    /// Continue Watching and Next Up as one row with one card per show: an unfinished episode wins, then the next
    /// unwatched one, and a finished one last. Movies stay separate. First appearance decides the order.
    /// </summary>
    /// <param name="resumable">Titles with a resume position, most recently played first.</param>
    /// <param name="nextUp">Next Up episodes, in Jellyfin's order.</param>
    /// <returns>Cards.</returns>
    public static IReadOnlyList<HomeCard> ContinueWatching(IEnumerable<HomeEntry> resumable, IEnumerable<HomeEntry> nextUp)
    {
        var winners = new Dictionary<string, HomeEntry>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var item in resumable.Concat(nextUp))
        {
            var key = item.Kind == "Episode" && item.SeriesId is { } series ? "s:" + series.ToString("N") : "i:" + item.Id.ToString("N");
            if (!winners.TryGetValue(key, out var old))
            {
                order.Add(key);
                winners[key] = item;
                continue;
            }

            int rank = ContinuationRank(item), oldRank = ContinuationRank(old);
            if (rank > oldRank || (rank == oldRank && (item.LastPlayed ?? DateTime.MinValue) > (old.LastPlayed ?? DateTime.MinValue)))
            {
                winners[key] = item;
            }
        }

        return order.Select(k => winners[k]).Where(e => e.Playable).Take(CardLimit)
            .Select(e => new HomeCard(e.Id, e, e.PositionTicks > 0 ? "Resume" : e.Kind == "Episode" ? "Next up" : string.Empty, Array.Empty<string>()))
            .ToList();
    }

    /// <summary>New, unwatched episodes of the viewer's favorite shows, added in the last 14 days, newest first.</summary>
    /// <param name="episodes">Candidate episodes.</param>
    /// <param name="favoriteSeries">The viewer's favorite shows.</param>
    /// <param name="nowUtc">Now.</param>
    /// <returns>Cards.</returns>
    public static IReadOnlyList<HomeCard> NewEpisodes(IEnumerable<HomeEntry> episodes, IReadOnlySet<Guid> favoriteSeries, DateTime nowUtc)
    {
        var since = nowUtc.AddDays(-RecentDays);
        return episodes
            .Where(e => e.Playable && !e.Played && e.SeriesId is { } s && favoriteSeries.Contains(s) && e.DateCreated is { } c && c >= since && c <= nowUtc)
            .GroupBy(e => e.Id).Select(g => g.First())
            .OrderByDescending(e => e.DateCreated).Take(CardLimit)
            .Select(e => new HomeCard(e.Id, e, "New in your library", new[] { "New episode" })).ToList();
    }

    /// <summary>Unwatched movies of 90 minutes or less, in catalogue order (newest first).</summary>
    /// <param name="movies">Movie catalogue.</param>
    /// <returns>Cards.</returns>
    public static IReadOnlyList<HomeCard> ShortMovies(IEnumerable<HomeEntry> movies)
    {
        const long Limit = ShortMovieMinutes * 600_000_000L;
        return movies.Where(m => m.Playable && !m.Played && m.RunTimeTicks is > 0 and <= Limit).Take(CardLimit)
            .Select(m => new HomeCard(m.Id, m, ((m.RunTimeTicks!.Value + 599_999_999) / 600_000_000).ToString(CultureInfo.InvariantCulture) + " min", Array.Empty<string>()))
            .ToList();
    }

    /// <summary>Movies with a verified 4K or HDR source.</summary>
    /// <param name="movies">Movies with formats read.</param>
    /// <returns>Cards.</returns>
    public static IReadOnlyList<HomeCard> Showcase(IEnumerable<HomeEntry> movies) =>
        movies.Where(m => m.Playable && m.Formats.Count > 0).Take(CardLimit)
            .Select(m => new HomeCard(m.Id, m, "Source format", m.Formats)).ToList();

    /// <summary>Whether a guide text names one of the viewer's teams, as a whole phrase.</summary>
    /// <param name="text">Guide title and description.</param>
    /// <param name="teams">Followed teams.</param>
    /// <returns>True on a match.</returns>
    public static bool TeamMatches(string text, IReadOnlyList<string> teams)
    {
        var normalized = " " + Normalize(text) + " ";
        return teams.Any(team => normalized.Contains(" " + Normalize(team) + " ", StringComparison.Ordinal));
    }

    /// <summary>Sports on now or starting in the next 12 hours, filtered to the viewer's teams when they follow any.</summary>
    /// <param name="programs">Guide programmes.</param>
    /// <param name="channels">Channels the viewer can open, by id.</param>
    /// <param name="teams">Followed teams.</param>
    /// <param name="nowUtc">Now.</param>
    /// <returns>Cards that open the channel.</returns>
    public static IReadOnlyList<HomeCard> Sports(IEnumerable<GuideProgram> programs, IReadOnlyDictionary<Guid, HomeEntry> channels, IReadOnlyList<string> teams, DateTime nowUtc)
    {
        return programs
            .Where(p => (p.IsSports || p.Genres.Any(g => string.Equals(g, "sport", StringComparison.OrdinalIgnoreCase) || string.Equals(g, "sports", StringComparison.OrdinalIgnoreCase)))
                && !NotLive.IsMatch(p.Name) && p.End > nowUtc && p.Start <= nowUtc + SportsWindow && p.Start > nowUtc - SportsWindow
                && channels.ContainsKey(p.ChannelId) && (teams.Count == 0 || TeamMatches(p.Name + " " + p.Overview, teams)))
            .OrderBy(p => p.Start).Take(CardLimit)
            .Select(p => new HomeCard(p.Id, channels[p.ChannelId], p.Name, new[] { p.Start <= nowUtc ? "On now" : "Starting soon" }, p.Start, p.End))
            .ToList();
    }

    /// <summary>Reads the followed teams the apps and web save in Jellyfin's display preferences.</summary>
    /// <param name="json">The zorofinTeams custom preference (a JSON array of names).</param>
    /// <returns>Up to 12 clean team names.</returns>
    public static IReadOnlyList<string> ParseTeams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096)
        {
            return Array.Empty<string>();
        }

        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            return list.Select(t => (t ?? string.Empty).Trim())
                .Where(t => t.Length is >= 3 and <= 64 && !t.Any(char.IsControl))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// My requests: the viewer's own requests that are now on the server. Unwatched titles first (badged Ready),
    /// then watched ones, newest arrival first. This joins the apps' "requests are ready" row with Home Screen
    /// Sections' My Requests row, which also kept watched titles.
    /// </summary>
    /// <param name="matches">Available requested titles with their arrival time.</param>
    /// <returns>Cards.</returns>
    public static IReadOnlyList<HomeCard> MyRequests(IEnumerable<(HomeEntry Entry, DateTime? Arrived)> matches) =>
        matches.Where(m => m.Entry.Playable)
            .GroupBy(m => m.Entry.Id).Select(g => g.First())
            .OrderBy(m => m.Entry.Played ? 1 : 0).ThenByDescending(m => m.Arrived ?? m.Entry.DateCreated ?? DateTime.MinValue)
            .Take(CardLimit)
            .Select(m => new HomeCard(m.Entry.Id, m.Entry, m.Entry.Played ? "Requested" : "Ready to watch", m.Entry.Played ? Array.Empty<string>() : new[] { "Ready" }))
            .ToList();

    /// <summary>Plain library shelf: playable items in the given order.</summary>
    /// <param name="items">Items.</param>
    /// <param name="subtitle">Card subtitle.</param>
    /// <returns>Cards.</returns>
    public static IReadOnlyList<HomeCard> Shelf(IEnumerable<HomeEntry> items, string subtitle = "") =>
        items.Where(i => i.Playable).GroupBy(i => i.Id).Select(g => g.First()).Take(CardLimit)
            .Select(i => new HomeCard(i.Id, i, subtitle, Array.Empty<string>())).ToList();

    /// <summary>
    /// Because you watched: titles that share the most genres with the seed, unwatched first, then by rating.
    /// </summary>
    /// <param name="seed">A movie or show the viewer finished.</param>
    /// <param name="candidates">Movies or shows sharing a genre.</param>
    /// <returns>Cards.</returns>
    public static IReadOnlyList<HomeCard> Similar(HomeEntry seed, IEnumerable<HomeEntry> candidates)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var genres = new HashSet<string>(seed.Genres, StringComparer.OrdinalIgnoreCase);
        return candidates.Where(c => c.Playable && c.Id != seed.Id)
            .Select(c => (Item: c, Shared: c.Genres.Count(genres.Contains)))
            .Where(c => c.Shared > 0)
            .OrderBy(c => c.Item.Played ? 1 : 0).ThenByDescending(c => c.Shared).ThenByDescending(c => c.Item.CommunityRating ?? 0)
            .GroupBy(c => c.Item.Id).Select(g => g.First())
            .Take(CardLimit).Select(c => new HomeCard(c.Item.Id, c.Item, string.Empty, Array.Empty<string>())).ToList();
    }

    private static int ContinuationRank(HomeEntry item)
    {
        if (item.RunTimeTicks is > 0 && item.PositionTicks >= item.RunTimeTicks)
        {
            return 0;
        }

        if (item.PositionTicks > 0)
        {
            return 2;
        }

        return item.Played ? 0 : 1;
    }

    private static string Normalize(string text) =>
        Separators.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), " ").Trim();
}
