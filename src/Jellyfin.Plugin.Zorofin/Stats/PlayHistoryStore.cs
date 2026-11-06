using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Zorofin.Stats;

/// <summary>
/// The admin play history: one line of JSON per finished play, one file per month
/// (<c>&lt;dir&gt;/2026-11.jsonl</c>). Unlike <see cref="PlayStatsStore"/> this records who watched what,
/// so it has its own switch, is readable only by administrators, and is pruned to the retention setting.
/// </summary>
public class PlayHistoryStore
{
    /// <summary>Shortest recorded play. Shorter ones are mis-taps and previews.</summary>
    public static readonly TimeSpan MinimumPlay = TimeSpan.FromSeconds(10);

    /// <summary>Most events one import call may send.</summary>
    public const int MaxImportBatch = 5000;

    private static readonly JsonSerializerOptions LineOptions = new() { WriteIndented = false };
    private readonly object _lock = new();
    private readonly string? _directory;
    private readonly List<PlayEvent> _events = new();
    private readonly HashSet<string> _importKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayHistoryStore"/> class.
    /// </summary>
    /// <param name="directory">Folder for the monthly files, or null to keep data in memory only.</param>
    public PlayHistoryStore(string? directory)
    {
        _directory = directory;
        Load();
    }

    /// <summary>Gets the number of stored events.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>Adds one finished play.</summary>
    /// <param name="play">The play.</param>
    /// <returns>Whether it was long enough to keep.</returns>
    public bool Add(PlayEvent play)
    {
        ArgumentNullException.ThrowIfNull(play);
        if (play.Seconds < MinimumPlay.TotalSeconds || play.UserId == Guid.Empty || play.ItemId == Guid.Empty)
        {
            return false;
        }

        lock (_lock)
        {
            Insert(play);
            if (_directory is not null)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(FileFor(play.StartedUtc), JsonSerializer.Serialize(play, LineOptions) + "\n", Encoding.UTF8);
            }
        }

        return true;
    }

    /// <summary>
    /// Adds imported plays, skipping any whose import key is already stored, and any that started after the
    /// plugin began recording on its own (both would have recorded those, so they would count twice).
    /// </summary>
    /// <param name="plays">Plays with an <see cref="PlayEvent.ImportKey"/>.</param>
    /// <returns>How many were added, skipped as already imported or unusable, and skipped as overlapping.</returns>
    public (int Added, int Skipped, int Overlap) Import(IEnumerable<PlayEvent> plays)
    {
        ArgumentNullException.ThrowIfNull(plays);
        var added = 0;
        var skipped = 0;
        var overlap = 0;
        lock (_lock)
        {
            var firstOwn = _events.FirstOrDefault(e => e.ImportKey is null)?.StartedUtc ?? DateTime.MaxValue;
            var months = new HashSet<string>(StringComparer.Ordinal);
            foreach (var play in plays)
            {
                if (play.ImportKey is not { Length: > 0 } key || play.UserId == Guid.Empty || play.ItemId == Guid.Empty || play.Seconds <= 0 || _importKeys.Contains(key))
                {
                    skipped++;
                    continue;
                }

                if (play.StartedUtc >= firstOwn)
                {
                    overlap++;
                    continue;
                }

                _importKeys.Add(key);

                Insert(play);
                months.Add(Month(play.StartedUtc));
                added++;
            }

            foreach (var month in months)
            {
                WriteMonth(month);
            }
        }

        return (added, skipped, overlap);
    }

    /// <summary>Returns the plays that started in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), oldest first.</summary>
    /// <param name="fromUtc">Start (inclusive).</param>
    /// <param name="toUtc">End (exclusive).</param>
    /// <returns>A copy of the matching plays.</returns>
    public IReadOnlyList<PlayEvent> Between(DateTime fromUtc, DateTime toUtc)
    {
        lock (_lock)
        {
            var start = LowerBound(fromUtc);
            var end = LowerBound(toUtc);
            return end > start ? _events.GetRange(start, end - start) : new List<PlayEvent>();
        }
    }

    /// <summary>Deletes history: all of it, or one user's.</summary>
    /// <param name="userId">Only this user's plays, or null for everyone's.</param>
    /// <returns>How many plays were deleted.</returns>
    public int Delete(Guid? userId)
    {
        lock (_lock)
        {
            var months = _events.Where(e => userId is null || e.UserId == userId).Select(e => Month(e.StartedUtc)).ToHashSet(StringComparer.Ordinal);
            var removed = _events.RemoveAll(e => userId is null || e.UserId == userId);
            RebuildImportKeys();
            foreach (var month in months)
            {
                WriteMonth(month);
            }

            return removed;
        }
    }

    /// <summary>Drops plays older than the retention window.</summary>
    /// <param name="retentionDays">Days to keep; 0 keeps everything.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <returns>How many plays were dropped.</returns>
    public int Prune(int retentionDays, DateTime nowUtc)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        var cutoff = nowUtc.AddDays(-retentionDays);
        lock (_lock)
        {
            var end = LowerBound(cutoff);
            if (end == 0)
            {
                return 0;
            }

            var months = _events.Take(end).Select(e => Month(e.StartedUtc)).ToHashSet(StringComparer.Ordinal);
            _events.RemoveRange(0, end);
            RebuildImportKeys();
            foreach (var month in months)
            {
                WriteMonth(month);
            }

            return end;
        }
    }

    private static string Month(DateTime utc) => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private string FileFor(DateTime utc) => Path.Combine(_directory!, Month(utc) + ".jsonl");

    private void Insert(PlayEvent play)
    {
        // Live plays arrive in order, so this is almost always an append.
        if (_events.Count == 0 || _events[^1].StartedUtc <= play.StartedUtc)
        {
            _events.Add(play);
        }
        else
        {
            _events.Insert(UpperBound(play.StartedUtc), play);
        }

        if (play.ImportKey is { Length: > 0 } key)
        {
            _importKeys.Add(key);
        }
    }

    private int LowerBound(DateTime utc)
    {
        int lo = 0, hi = _events.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_events[mid].StartedUtc < utc)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private int UpperBound(DateTime utc)
    {
        int lo = 0, hi = _events.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_events[mid].StartedUtc <= utc)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private void RebuildImportKeys()
    {
        _importKeys.Clear();
        foreach (var e in _events)
        {
            if (e.ImportKey is { Length: > 0 } key)
            {
                _importKeys.Add(key);
            }
        }
    }

    private void WriteMonth(string month)
    {
        if (_directory is null)
        {
            return;
        }

        var file = Path.Combine(_directory, month + ".jsonl");
        var lines = _events.Where(e => Month(e.StartedUtc) == month).Select(e => JsonSerializer.Serialize(e, LineOptions)).ToList();
        if (lines.Count == 0)
        {
            File.Delete(file);
            return;
        }

        Directory.CreateDirectory(_directory);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, string.Join('\n', lines) + "\n", Encoding.UTF8);
        File.Move(temp, file, overwrite: true);
    }

    private void Load()
    {
        if (_directory is null || !Directory.Exists(_directory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_directory, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<PlayEvent>(line) is { } play)
                    {
                        Insert(play);
                    }
                }
                catch (JsonException)
                {
                    // A damaged line (for example a write cut off by a crash) costs that one play only.
                }
            }
        }
    }
}
