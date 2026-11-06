using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Zorofin.Home;

/// <summary>
/// The seasonal / curated calendar. A port of support-agent/curated-seasons.js, which the iOS, Apple TV and
/// TV apps also follow, so every client picks the same theme on the same day.
/// </summary>
public static class CuratedSeasons
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Gets the themes by key. Collection names and tags are editorial evidence; genres only describe moods.</summary>
    public static IReadOnlyDictionary<string, Theme> Themes { get; } = new Dictionary<string, Theme>(StringComparer.Ordinal)
    {
        ["new-year"] = new("New year, fresh starts", "new.year|fresh.starts|new.beginnings", "new.year|fresh.start|new.beginning"),
        ["valentines"] = new("Valentine’s picks", "valentine|date.night|romantic.comed", null, "Romance"),
        ["black-history"] = new("Black History Month", "black.history|black.cinema|black.filmmakers"),
        ["spring-break"] = new("Spring break", "spring.break", "spring.break|road.trip|vacation", "Comedy", "Adventure", "Family"),
        ["st-patricks"] = new("St. Patrick’s Day", @"st\.?\s*patrick|irish.cinema|irish.stories"),
        ["earth-day"] = new("Earth Day: wild worlds", "earth.day|planet.earth|wildlife|nature.documentar", "wildlife|natural.world|rainforest|ocean|conservation", "Documentary"),
        ["mothers-day"] = new("Mother’s Day", "mother[’']?s.day|mothers.day"),
        ["memorial-day"] = new("Memorial Day weekend", "memorial.day"),
        ["graduation"] = new("Graduation & new beginnings", "graduation|coming.of.age|new.beginnings", "graduat(?:e|es|ion|ing)|coming.of.age", "Comedy", "Drama", "Family"),
        ["pride"] = new("Pride stories", @"\bpride\b|lgbtq?\+?.cinema|queer.cinema"),
        ["juneteenth"] = new("Juneteenth", "juneteenth"),
        ["fathers-day"] = new("Father’s Day", "father[’']?s.day|fathers.day"),
        ["fourth-july"] = new("Fourth of July", @"(?:fourth|4th).of.july|july.(?:fourth|4th|4)\b|independence.day|stars.and.stripes", @"(?:fourth|4th).of.july|july.(?:fourth|4th|4)\b|independence.day"),
        ["back-to-school"] = new("Back to school", "back.to.school|school.days|campus.classics", "high.school|boarding.school|college|campus|classroom|school.year", "Comedy", "Drama", "Family", "Animation"),
        ["labor-day"] = new("Long-weekend movies", "labor.day|long.weekend", null, "Adventure", "Comedy"),
        ["halloween"] = new("Spooky season", "halloween|horror|spooky", null, "Horror"),
        ["thanksgiving"] = new("Thanksgiving weekend", "thanksgiving|friendsgiving", "thanksgiving|friendsgiving"),
        ["christmas"] = new("Christmas favorites", @"christmas|\bxmas\b", @"christmas|\bxmas\b"),
        ["winter"] = new("Winter nights", "winter|snow.day|snowy", "winter|snowbound|snowstorm|snowy|ski.resort"),
        ["spring"] = new("Springtime stories", "springtime|spring.favorites|spring.picks", "springtime|spring.festival|spring.garden"),
        ["summer"] = new("Summer movie nights", "summer|beach|surfing|vacation", "summer|beach|surfing|vacation|road.trip"),
        ["autumn"] = new("Autumn evenings", "autumn|fall.favorites|fall.picks|cozy.season", "autumn|fall.foliage|harvest.festival"),
        ["comfort"] = new("Comfort watches", "comfort|ghibli|feel.good", null, "Family", "Comedy"),
        ["movie-night"] = new("Movie night", "movie.night|heist|mind.bend|sci.fi|essentials", null, "Adventure", "Mystery", "Science Fiction"),
        ["rainy-day"] = new("Rainy-day mysteries", "rainy.day|whodunit|mystery.night", null, "Mystery"),
        ["weekend-adventures"] = new("Weekend adventures", "weekend.adventure|adventure.favorites", null, "Adventure"),
        // Variable-date holidays need an explicit dated calendar entry. Never guess observances.
        ["ramadan"] = new("Ramadan evenings", @"\bramadan\b"),
        ["eid"] = new("Eid picks", @"\beid\b"),
        ["diwali"] = new("Diwali picks", @"\bdiwali\b|\bdeepavali\b"),
        ["hanukkah"] = new("Hanukkah picks", @"\bhanukkah\b|\bchanukah\b"),
        ["lunar-new-year"] = new("Lunar New Year", @"lunar.new.year|chinese.new.year|\btet\b"),
        ["easter"] = new("Easter weekend", @"\beaster\b"),
        ["game-day"] = new("Game-day movies", "game.day|sports.movies|sports.films"),
        ["awards-night"] = new("Awards-night picks", "award.winn|oscar|academy.award"),
    };

    /// <summary>The viewer's local calendar day, as a UTC midnight.</summary>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="zone">Calendar time zone.</param>
    /// <returns>The day.</returns>
    public static DateTime CalendarDay(DateTime nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        return new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>Reads an operator's dated calendar ({"schemaVersion":1,"events":[{theme,start,end}]}). Invalid input gives no events.</summary>
    /// <param name="json">Calendar JSON, or empty.</param>
    /// <returns>Valid events.</returns>
    public static IReadOnlyList<CalendarEvent> ParseEvents(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32768)
        {
            return Array.Empty<CalendarEvent>();
        }

        try
        {
            var root = JsonNode.Parse(json);
            if (root?["schemaVersion"]?.GetValue<int>() != 1 || root["events"] is not JsonArray list || list.Count > 64)
            {
                return Array.Empty<CalendarEvent>();
            }

            var events = new List<CalendarEvent>();
            foreach (var node in list)
            {
                var theme = node?["theme"]?.GetValue<string>();
                if (theme is null || !Themes.ContainsKey(theme)
                    || !TryDate(node?["start"]?.GetValue<string>(), out var start) || !TryDate(node?["end"]?.GetValue<string>(), out var end)
                    || end < start || (end - start).TotalDays > 45)
                {
                    return Array.Empty<CalendarEvent>();
                }

                events.Add(new CalendarEvent(theme, start, end));
            }

            return events;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return Array.Empty<CalendarEvent>();
        }
    }

    /// <summary>Themes to try today, best first: dated events, holidays near today, the season, then rotating moods.</summary>
    /// <param name="today">The calendar day from <see cref="CalendarDay"/>.</param>
    /// <param name="events">Operator's dated events.</param>
    /// <returns>Theme keys.</returns>
    public static IReadOnlyList<string> ThemePlan(DateTime today, IReadOnlyList<CalendarEvent>? events = null)
    {
        int year = today.Year, month = today.Month;
        var candidates = new List<(string Key, double Priority)>();
        void Window(string key, int startMonth, int startDay, int endMonth, int endDay, double priority = 30)
        {
            var start = new DateTime(year, startMonth, startDay, 0, 0, 0, DateTimeKind.Utc);
            var end = new DateTime(year, endMonth, endDay, 0, 0, 0, DateTimeKind.Utc);
            if (today >= start && today <= end)
            {
                candidates.Add((key, priority));
            }
        }

        void Holiday(string key, DateTime day, int before = 7, int after = 1)
        {
            var distance = (day - today).TotalDays;
            if (distance <= before && distance >= -after)
            {
                candidates.Add((key, 100 - Math.Abs(distance) + (distance >= 0 ? 0.5 : 0)));
            }
        }

        DateTime Day(int m, int d) => new(year, m, d, 0, 0, 0, DateTimeKind.Utc);

        // Actual holiday dates, not shifted observances. Seasonal windows are editorial US/northern-hemisphere defaults.
        Window("new-year", 1, 1, 1, 7, 80);
        Window("new-year", 12, 27, 12, 31, 80);
        Holiday("valentines", Day(2, 14));
        Window("black-history", 2, 1, 2, DateTime.DaysInMonth(year, 2));
        Window("spring-break", 3, 8, 4, 12);
        Holiday("st-patricks", Day(3, 17));
        Holiday("earth-day", Day(4, 22));
        Holiday("mothers-day", NthWeekday(year, 5, DayOfWeek.Sunday, 2));
        var lastMay = Day(5, 31);
        Holiday("memorial-day", Day(5, 31 - (((int)lastMay.DayOfWeek + 6) % 7)), 3, 1);
        Window("graduation", 5, 15, 6, 20, 35);
        Window("pride", 6, 1, 6, 30);
        Holiday("juneteenth", Day(6, 19), 4, 2);
        Holiday("fathers-day", NthWeekday(year, 6, DayOfWeek.Sunday, 3));
        Holiday("fourth-july", Day(7, 4), 8, 3);
        Window("back-to-school", 8, 10, 9, 15, 40);
        Holiday("labor-day", NthWeekday(year, 9, DayOfWeek.Monday, 1), 3, 1);
        Window("halloween", 10, 1, 11, 1, 60);
        Holiday("thanksgiving", NthWeekday(year, 11, DayOfWeek.Thursday, 4), 10, 3);
        Window("christmas", 12, 1, 12, 26, 60);

        var scheduled = (events ?? Array.Empty<CalendarEvent>()).Where(e => today >= e.Start && today <= e.End).Select(e => e.Theme);
        var season = month <= 2 || month == 12 ? "winter" : month <= 5 ? "spring" : month <= 8 ? "summer" : "autumn";
        string[] moods = { "comfort", "movie-night", "rainy-day", "weekend-adventures" };
        var offset = (int)(Week(today) % moods.Length);
        var plan = new List<string>();
        // OrderByDescending is stable, like Array.prototype.sort.
        foreach (var key in scheduled.Concat(candidates.OrderByDescending(c => c.Priority).Select(c => c.Key)).Append(season).Concat(moods.Skip(offset)).Concat(moods.Take(offset)))
        {
            if (!plan.Contains(key, StringComparer.Ordinal))
            {
                plan.Add(key);
            }
        }

        return plan;
    }

    /// <summary>Whether an item's tags, or its text and genres, fit a theme.</summary>
    /// <param name="item">Item.</param>
    /// <param name="theme">Theme key.</param>
    /// <returns>True when it fits.</returns>
    public static bool MetadataMatches(HomeEntry item, string theme)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Themes.TryGetValue(theme, out var rule))
        {
            return false;
        }

        if (item.Tags.Any(rule.Collections.IsMatch))
        {
            return true;
        }

        if (rule.Match is null && rule.Genres.Count == 0)
        {
            return false;
        }

        var text = string.Join(' ', new[] { item.Name, item.Overview }.Concat(item.Tags).Where(s => !string.IsNullOrEmpty(s)));
        return (rule.Match is null || rule.Match.IsMatch(text)) && (rule.Genres.Count == 0 || item.Genres.Any(rule.Genres.Contains));
    }

    /// <summary>Unwatched first, then a stable weekly shuffle per viewer, at most 20.</summary>
    /// <param name="items">Candidates.</param>
    /// <param name="theme">Theme key.</param>
    /// <param name="viewer">Viewer id.</param>
    /// <param name="today">Calendar day.</param>
    /// <returns>Picks.</returns>
    public static IReadOnlyList<HomeEntry> RankPicks(IEnumerable<HomeEntry> items, string theme, Guid viewer, DateTime today)
    {
        var week = Week(today).ToString(CultureInfo.InvariantCulture);
        string Rank(HomeEntry item) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(theme + "|" + viewer.ToString("N") + "|" + week + "|" + item.Id.ToString("N"))));
        return items.GroupBy(i => i.Id).Select(g => g.First())
            .Select(item => (Item: item, Rank: Rank(item)))
            .OrderBy(x => x.Item.Played ? 1 : 0).ThenBy(x => x.Rank, StringComparer.Ordinal)
            .Take(HomeRules.CardLimit).Select(x => x.Item).ToList();
    }

    private static long Week(DateTime day) => (long)Math.Floor((day - DateTime.UnixEpoch).TotalDays / 7);

    private static DateTime NthWeekday(int year, int month, DayOfWeek weekday, int occurrence)
    {
        var first = (int)new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).DayOfWeek;
        return new DateTime(year, month, 1 + (((int)weekday - first + 7) % 7) + ((occurrence - 1) * 7), 0, 0, 0, DateTimeKind.Utc);
    }

    private static bool TryDate(string? text, out DateTime date) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date);

    /// <summary>A theme: its title, the collection/tag pattern, an optional text pattern and optional mood genres.</summary>
    public sealed class Theme
    {
        internal Theme(string title, string collections, string? match = null, params string[] genres)
        {
            Title = title;
            Collections = new Regex(collections, Options, MatchTimeout);
            Match = match is null ? null : new Regex(match, Options, MatchTimeout);
            Genres = genres;
        }

        /// <summary>Gets the row title.</summary>
        public string Title { get; }

        /// <summary>Gets the collection-name and tag pattern.</summary>
        public Regex Collections { get; }

        /// <summary>Gets the title/overview pattern, if any.</summary>
        public Regex? Match { get; }

        /// <summary>Gets the mood genres, if any.</summary>
        public IReadOnlyList<string> Genres { get; }
    }

    /// <summary>An operator's dated theme.</summary>
    /// <param name="Theme">Theme key.</param>
    /// <param name="Start">First day.</param>
    /// <param name="End">Last day.</param>
    public sealed record CalendarEvent(string Theme, DateTime Start, DateTime End);
}
