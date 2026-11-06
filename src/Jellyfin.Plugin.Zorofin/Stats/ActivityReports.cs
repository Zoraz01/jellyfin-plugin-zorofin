using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Zorofin.Stats;

/// <summary>Filters shared by every activity report.</summary>
/// <param name="UserId">Only this user, or null.</param>
/// <param name="ItemType">Only this kind (Movie, Episode, Audio, TvChannel…), or null.</param>
/// <param name="Search">Only plays whose title, user, client or device contains this text, or null.</param>
public sealed record ActivityFilter(Guid? UserId = null, string? ItemType = null, string? Search = null)
{
    /// <summary>Whether a play passes the filter.</summary>
    /// <param name="play">The play.</param>
    /// <returns>True when it matches.</returns>
    public bool Matches(PlayEvent play)
    {
        ArgumentNullException.ThrowIfNull(play);
        if (UserId is { } user && play.UserId != user)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(ItemType) && !string.Equals(play.ItemType, ItemType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        var q = Search.Trim();
        return Contains(play.ItemName, q) || Contains(play.SeriesName, q) || Contains(play.UserName, q) || Contains(play.Client, q) || Contains(play.Device, q);
    }

    private static bool Contains(string? text, string q) => text is not null && text.Contains(q, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Turns play history into the admin charts and reports. Pure functions over a list of plays, so they are
/// tested without Jellyfin. Days, hours and weekdays are in the viewer's time zone, given as an offset.
/// </summary>
public static class ActivityReports
{
    /// <summary>Start of the import key of a play brought in from Playback Reporting.</summary>
    public const string ImportPrefix = "playbackReporting:";

    /// <summary>Most rows in a ranked list.</summary>
    public const int TopLimit = 25;

    /// <summary>Builds the summary behind the Activity page's charts.</summary>
    /// <param name="plays">Plays in the window.</param>
    /// <param name="fromUtc">Window start.</param>
    /// <param name="toUtc">Window end.</param>
    /// <param name="offsetMinutes">Viewer's offset from UTC in minutes (New York in winter is -300).</param>
    /// <param name="filter">Filters.</param>
    /// <returns>The summary.</returns>
    public static JsonObject Summary(IEnumerable<PlayEvent> plays, DateTime fromUtc, DateTime toUtc, int offsetMinutes, ActivityFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        var list = plays.Where(filter.Matches).ToList();

        var firstDay = DateOnly.FromDateTime(fromUtc + offset);
        var lastDay = DateOnly.FromDateTime(toUtc.AddTicks(-1) + offset);
        var perDay = new SortedDictionary<DateOnly, (int Plays, long Seconds)>();
        for (var d = firstDay; d <= lastDay && perDay.Count < 3700; d = d.AddDays(1))
        {
            perDay[d] = (0, 0);
        }

        var hours = new long[24];
        var hourPlays = new int[24];
        var weekdays = new long[7];
        var weekdayPlays = new int[7];
        var heat = new long[7, 24];
        foreach (var play in list)
        {
            var local = play.StartedUtc + offset;
            var day = DateOnly.FromDateTime(local);
            perDay.TryGetValue(day, out var t);
            perDay[day] = (t.Plays + 1, t.Seconds + play.Seconds);
            hours[local.Hour] += play.Seconds;
            hourPlays[local.Hour]++;
            weekdays[(int)local.DayOfWeek] += play.Seconds;
            weekdayPlays[(int)local.DayOfWeek]++;
            heat[(int)local.DayOfWeek, local.Hour] += play.Seconds;
        }

        var days = new JsonArray(perDay.Select(d => (JsonNode)new JsonObject
        {
            ["date"] = d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["plays"] = d.Value.Plays,
            ["seconds"] = d.Value.Seconds,
        }).ToArray());

        var heatmap = new JsonArray();
        for (var w = 0; w < 7; w++)
        {
            heatmap.Add(new JsonArray(Enumerable.Range(0, 24).Select(h => (JsonNode)JsonValue.Create(heat[w, h])).ToArray()));
        }

        var users = list.GroupBy(p => p.UserId).Select(g => new JsonObject
        {
            ["id"] = g.Key.ToString("N"),
            ["name"] = g.OrderByDescending(p => p.StartedUtc).First().UserName,
            ["plays"] = g.Count(),
            ["seconds"] = g.Sum(p => (long)p.Seconds),
            ["lastPlayed"] = g.Max(p => p.StartedUtc).ToString("o", CultureInfo.InvariantCulture),
        }).OrderByDescending(u => (long)u["seconds"]!).ToList();

        return new JsonObject
        {
            ["from"] = fromUtc.ToString("o", CultureInfo.InvariantCulture),
            ["to"] = toUtc.ToString("o", CultureInfo.InvariantCulture),
            ["totals"] = new JsonObject
            {
                ["plays"] = list.Count,
                ["seconds"] = list.Sum(p => (long)p.Seconds),
                ["users"] = users.Count,
                ["titles"] = list.Select(p => p.TitleId).Distinct().Count(),
                ["completed"] = list.Count(p => p.Completed),
                ["transcoded"] = list.Count(p => string.Equals(p.PlayMethod, "Transcode", StringComparison.OrdinalIgnoreCase)),
            },
            ["days"] = days,
            ["hours"] = new JsonArray(Enumerable.Range(0, 24).Select(h => (JsonNode)new JsonObject { ["hour"] = h, ["plays"] = hourPlays[h], ["seconds"] = hours[h] }).ToArray()),
            ["weekdays"] = new JsonArray(Enumerable.Range(0, 7).Select(w => (JsonNode)new JsonObject { ["day"] = w, ["plays"] = weekdayPlays[w], ["seconds"] = weekdays[w] }).ToArray()),
            ["heatmap"] = heatmap,
            ["users"] = new JsonArray(users.Take(100).Select(u => (JsonNode)u).ToArray()),
            ["types"] = Breakdown(list, p => p.ItemType),
            ["clients"] = Breakdown(list, p => p.Client),
            ["devices"] = Breakdown(list, p => p.Device),
            ["methods"] = Breakdown(list, p => p.PlayMethod),
            ["movies"] = TopTitles(list.Where(p => p.ItemType == "Movie")),
            ["shows"] = TopTitles(list.Where(p => p.ItemType == "Episode")),
            ["channels"] = TopTitles(list.Where(p => p.ItemType == "TvChannel")),
        };
    }

    /// <summary>One page of the activity log, newest first.</summary>
    /// <param name="plays">Plays in the window.</param>
    /// <param name="filter">Filters.</param>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Rows to return.</param>
    /// <returns>The total and the page.</returns>
    public static JsonObject Log(IEnumerable<PlayEvent> plays, ActivityFilter filter, int skip, int take)
    {
        var list = plays.Where(filter.Matches).OrderByDescending(p => p.StartedUtc).ToList();
        return new JsonObject
        {
            ["total"] = list.Count,
            ["items"] = new JsonArray(list.Skip(skip).Take(take).Select(p => (JsonNode)Row(p)).ToArray()),
        };
    }

    /// <summary>The activity log as CSV (UTC times), for spreadsheets.</summary>
    /// <param name="plays">Plays in the window.</param>
    /// <param name="filter">Filters.</param>
    /// <returns>CSV text.</returns>
    public static string Csv(IEnumerable<PlayEvent> plays, ActivityFilter filter)
    {
        var csv = new StringBuilder("Started (UTC),User,Type,Title,Item,Season,Episode,Minutes,Client,Device,Method,Transcode,Completed\n");
        foreach (var p in plays.Where(filter.Matches).OrderByDescending(p => p.StartedUtc))
        {
            csv.AppendJoin(
                ',',
                p.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Cell(p.UserName),
                Cell(p.ItemType),
                Cell(p.TitleName),
                Cell(p.ItemName),
                p.Season?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                p.Episode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Math.Round(p.Seconds / 60.0, 1).ToString(CultureInfo.InvariantCulture),
                Cell(p.Client),
                Cell(p.Device),
                Cell(p.PlayMethod),
                Cell(p.Transcode),
                p.Completed ? "yes" : "no").Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>One play as the page and the scripts read it.</summary>
    /// <param name="p">The play.</param>
    /// <returns>JSON row.</returns>
    public static JsonObject Row(PlayEvent p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new JsonObject
        {
            ["id"] = p.Id,
            ["started"] = p.StartedUtc.ToString("o", CultureInfo.InvariantCulture),
            ["seconds"] = p.Seconds,
            ["userId"] = p.UserId.ToString("N"),
            ["userName"] = p.UserName,
            ["itemId"] = p.ItemId.ToString("N"),
            ["itemType"] = p.ItemType,
            ["itemName"] = p.ItemName,
            ["seriesId"] = p.SeriesId?.ToString("N"),
            ["seriesName"] = p.SeriesName,
            ["season"] = p.Season,
            ["episode"] = p.Episode,
            ["client"] = p.Client,
            ["device"] = p.Device,
            ["method"] = p.PlayMethod,
            ["transcode"] = p.Transcode,
            ["completed"] = p.Completed,
        };
    }

    /// <summary>
    /// A play in the column names Playback Reporting used, so Rewind and the Popular script can switch
    /// source without changing their maths.
    /// </summary>
    /// <param name="p">The play.</param>
    /// <returns>JSON row.</returns>
    public static JsonObject LegacyRow(PlayEvent p)
    {
        ArgumentNullException.ThrowIfNull(p);
        // Imported plays give back Playback Reporting's own rowid and time text, so Rewind's saved history
        // (keyed on both) recognises them instead of counting them twice after the switch.
        JsonNode sourceRowId = p.Id;
        var dateCreated = DateTime.SpecifyKind(p.StartedUtc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        if (p.ImportKey is { } key && key.StartsWith(ImportPrefix, StringComparison.Ordinal)
            && key.IndexOf('|', StringComparison.Ordinal) is var bar and > 0
            && long.TryParse(key.AsSpan(ImportPrefix.Length, bar - ImportPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var rowId))
        {
            sourceRowId = rowId;
            dateCreated = key[(bar + 1)..];
        }

        return new JsonObject
        {
            ["SourceRowId"] = sourceRowId,
            ["DateCreated"] = dateCreated,
            ["UserId"] = p.UserId.ToString("N"),
            ["ItemId"] = p.ItemId.ToString("N"),
            ["ItemType"] = p.ItemType,
            ["ItemName"] = p.ItemName,
            ["ClientName"] = p.Client,
            ["DeviceName"] = p.Device,
            ["PlaybackMethod"] = p.PlayMethod,
            ["PlayDuration"] = p.Seconds,
        };
    }

    private static JsonArray Breakdown(IEnumerable<PlayEvent> plays, Func<PlayEvent, string?> key) =>
        new(plays.GroupBy(p => string.IsNullOrWhiteSpace(key(p)) ? "Unknown" : key(p)!, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Plays: g.Count(), Seconds: g.Sum(p => (long)p.Seconds)))
            .OrderByDescending(g => g.Seconds)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .Take(TopLimit)
            .Select(g => (JsonNode)new JsonObject { ["name"] = g.Name, ["plays"] = g.Plays, ["seconds"] = g.Seconds })
            .ToArray());

    private static JsonArray TopTitles(IEnumerable<PlayEvent> plays) =>
        new(plays.GroupBy(p => p.TitleId)
            .Select(g => new
            {
                Id = g.Key,
                Name = g.OrderByDescending(p => p.StartedUtc).First().TitleName,
                Plays = g.Count(),
                Seconds = g.Sum(p => (long)p.Seconds),
                Viewers = g.Select(p => p.UserId).Distinct().Count(),
            })
            .OrderByDescending(t => t.Seconds)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(TopLimit)
            .Select(t => (JsonNode)new JsonObject { ["id"] = t.Id.ToString("N"), ["name"] = t.Name, ["plays"] = t.Plays, ["seconds"] = t.Seconds, ["viewers"] = t.Viewers })
            .ToArray());

    private static string Cell(string? value)
    {
        var text = value ?? string.Empty;
        // A leading =, +, - or @ would run as a formula in a spreadsheet.
        if (text.Length > 0 && "=+-@".Contains(text[0], StringComparison.Ordinal))
        {
            text = "'" + text;
        }

        return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }
}
