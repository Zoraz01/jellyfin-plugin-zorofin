using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Zorofin.Home;

/// <summary>Turns a built Home into the two wire formats.</summary>
public static class HomeFeeds
{
    /// <summary>
    /// The apps' homeRows feed: the same shape as the older /support/home/rows feed (schema version 1), with the
    /// seven app rows, empty and unavailable ones included so apps can tell them apart.
    /// </summary>
    /// <param name="snapshot">Built Home.</param>
    /// <returns>Feed JSON.</returns>
    public static JsonObject AppRows(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rows = new JsonArray();
        foreach (var id in HomeBuilder.AppRowIds)
        {
            if (snapshot.Rows.FirstOrDefault(r => r.Id == id) is not { } row)
            {
                continue;
            }

            rows.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["title"] = row.Title,
                ["description"] = string.Empty,
                ["state"] = row.State,
                ["message"] = null,
                ["cards"] = Cards(row, snapshot.Items),
            });
        }

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["generatedAt"] = Stamp(snapshot.GeneratedAt),
            ["preferences"] = new JsonObject
            {
                ["hiddenRows"] = new JsonArray(),
                ["teams"] = new JsonArray(snapshot.Teams.Select(t => (JsonNode?)t).ToArray()),
                ["minutes"] = HomeRules.ShortMovieMinutes,
                ["theme"] = "automatic",
            },
            ["rows"] = rows,
        };
    }

    /// <summary>
    /// Every Home row for the web client, in place of Home Screen Sections: only rows with cards, in a stable
    /// order, each saying where it sits (pinned, personal or library).
    /// </summary>
    /// <param name="snapshot">Built Home.</param>
    /// <returns>Sections JSON.</returns>
    public static JsonObject Sections(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sections = new JsonArray();
        foreach (var row in snapshot.Rows.Where(r => r.Cards.Count > 0))
        {
            var cards = Cards(row, snapshot.Items);
            if (cards.Count == 0)
            {
                continue;
            }

            sections.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["title"] = row.Title,
                ["placement"] = row.Placement.ToString().ToLowerInvariant(),
                ["cards"] = cards,
            });
        }

        return new JsonObject { ["schemaVersion"] = 1, ["generatedAt"] = Stamp(snapshot.GeneratedAt), ["sections"] = sections };
    }

    private static JsonArray Cards(HomeRowResult row, IReadOnlyDictionary<Guid, JsonObject> items)
    {
        var cards = new JsonArray();
        var seen = new HashSet<Guid>();
        foreach (var card in row.Cards)
        {
            // A card whose item couldn't be described (deleted a moment ago, or no longer visible) is dropped.
            if (!items.TryGetValue(card.Entry.Id, out var item) || !seen.Add(card.Id))
            {
                continue;
            }

            var json = new JsonObject
            {
                ["id"] = card.Id.ToString("N"),
                ["item"] = item.DeepClone(),
                ["subtitle"] = card.Subtitle,
                ["badges"] = new JsonArray(card.Badges.Select(b => (JsonNode?)b).ToArray()),
            };
            if (card.StartsAt is { } start)
            {
                json["startsAt"] = Stamp(start);
            }

            if (card.EndsAt is { } end)
            {
                json["endsAt"] = Stamp(end);
            }

            cards.Add(json);
        }

        return cards;
    }

    private static string Stamp(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
