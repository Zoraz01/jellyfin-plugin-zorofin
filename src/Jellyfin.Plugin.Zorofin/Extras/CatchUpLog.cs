using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Zorofin.Extras;

/// <summary>
/// Administrator-only log of Catch Up choices (who chose what for which title, and when), one JSON object per line.
/// Rotates at about 5 MB, keeping one older file. Only written while the operator has the log switched on.
/// </summary>
public class CatchUpLog
{
    /// <summary>The choices Catch Up records.</summary>
    public static readonly IReadOnlyList<string> Actions = new[] { "watch", "later", "watched", "skip" };

    private const long RotateBytes = 5L * 1024 * 1024;
    private const int MaxRead = 2000;
    private readonly string _path;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CatchUpLog"/> class.
    /// </summary>
    /// <param name="path">Log file path.</param>
    public CatchUpLog(string path)
    {
        _path = path;
    }

    /// <summary>Adds one choice.</summary>
    /// <param name="entry">The choice.</param>
    public void Add(CatchUpEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = new JsonObject
        {
            ["at"] = entry.At.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["userId"] = entry.UserId.ToString("N", System.Globalization.CultureInfo.InvariantCulture),
            ["user"] = entry.UserName,
            ["itemId"] = entry.ItemId.ToString("N", System.Globalization.CultureInfo.InvariantCulture),
            ["title"] = entry.Title,
            ["action"] = entry.Action,
        }.ToJsonString() + "\n";
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && new FileInfo(_path).Length > RotateBytes)
            {
                File.Move(_path, Older, true);
            }

            File.AppendAllText(_path, line, Encoding.UTF8);
        }
    }

    /// <summary>Newest choices first, with a per-viewer summary.</summary>
    /// <param name="take">How many entries to return.</param>
    /// <returns>Entries and summary.</returns>
    public JsonObject Read(int take)
    {
        List<JsonObject> entries;
        lock (_lock)
        {
            entries = Lines(Older).Concat(Lines(_path)).ToList();
        }

        entries.Reverse();
        var summary = entries
            .GroupBy(e => (string?)e["userId"] ?? string.Empty)
            .Select(g => new JsonObject
            {
                ["user"] = (string?)g.First()["user"],
                ["total"] = g.Count(),
                ["watch"] = g.Count(e => (string?)e["action"] == "watch"),
                ["later"] = g.Count(e => (string?)e["action"] == "later"),
                ["watched"] = g.Count(e => (string?)e["action"] == "watched"),
                ["skip"] = g.Count(e => (string?)e["action"] == "skip"),
                ["last"] = (string?)g.First()["at"],
            })
            .OrderByDescending(s => (int)s["total"]!)
            .ToArray<JsonNode?>();
        return new JsonObject
        {
            ["entries"] = new JsonArray(entries.Take(Math.Clamp(take, 1, 500)).Select(e => (JsonNode)e.DeepClone()).ToArray()),
            ["summary"] = new JsonArray(summary),
        };
    }

    private string Older => Path.ChangeExtension(_path, ".1.jsonl");

    private static IEnumerable<JsonObject> Lines(string path)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (var line in File.ReadLines(path).TakeLast(MaxRead))
        {
            JsonObject? entry = null;
            try
            {
                entry = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                // A torn line from a crash; skip it.
            }

            if (entry is not null)
            {
                yield return entry;
            }
        }
    }
}

/// <summary>One Catch Up choice.</summary>
/// <param name="At">When.</param>
/// <param name="UserId">Viewer id.</param>
/// <param name="UserName">Viewer name.</param>
/// <param name="ItemId">Title id.</param>
/// <param name="Title">Title name.</param>
/// <param name="Action">One of <see cref="CatchUpLog.Actions"/>.</param>
public sealed record CatchUpEntry(DateTimeOffset At, Guid UserId, string UserName, Guid ItemId, string Title, string Action);
