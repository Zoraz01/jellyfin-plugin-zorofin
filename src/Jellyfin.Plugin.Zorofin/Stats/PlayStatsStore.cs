using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Zorofin.Stats;

/// <summary>
/// Anonymous per-title daily watch totals. No user, device or session identity is stored, so the data
/// can only answer "what is popular on this server", never "who watched what".
/// </summary>
public class PlayStatsStore
{
    /// <summary>Shortest counted play. Shorter sessions are previews and mis-taps.</summary>
    public static readonly TimeSpan MinimumPlay = TimeSpan.FromMinutes(2);

    /// <summary>Longest counted play. Longer ones are abandoned, paused-overnight sessions.</summary>
    public static readonly TimeSpan MaximumPlay = TimeSpan.FromHours(6);

    private const int RetentionDays = 35;
    private readonly object _lock = new();
    private readonly string? _path;
    private Dictionary<Guid, Dictionary<string, Totals>> _days = new();
    private bool _dirty;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayStatsStore"/> class.
    /// </summary>
    /// <param name="path">JSON file to persist to, or null to keep data in memory only.</param>
    public PlayStatsStore(string? path)
    {
        _path = path;
        Load();
    }

    /// <summary>Records one finished play of a movie or series.</summary>
    /// <param name="titleId">Movie id, or the series id for an episode.</param>
    /// <param name="watched">Time spent watching.</param>
    /// <param name="endedUtc">When playback stopped.</param>
    /// <returns>Whether the play counted.</returns>
    public bool Record(Guid titleId, TimeSpan watched, DateTime endedUtc)
    {
        if (titleId == Guid.Empty || watched < MinimumPlay || watched > MaximumPlay)
        {
            return false;
        }

        var day = endedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            if (!_days.TryGetValue(titleId, out var perDay))
            {
                _days[titleId] = perDay = new Dictionary<string, Totals>(StringComparer.Ordinal);
            }

            perDay.TryGetValue(day, out var totals);
            perDay[day] = new Totals { Seconds = totals.Seconds + (long)watched.TotalSeconds, Plays = totals.Plays + 1 };
            _dirty = true;
        }

        return true;
    }

    /// <summary>Ranks titles by watch time over the last <paramref name="days"/> days (including today).</summary>
    /// <param name="days">Window in days.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <returns>Title ids with totals, most watched first.</returns>
    public IReadOnlyList<(Guid TitleId, long Seconds, int Plays)> Top(int days, DateTime nowUtc)
    {
        var first = DateOnly.FromDateTime(nowUtc).AddDays(1 - Math.Clamp(days, 1, RetentionDays)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            return _days
                .Select(entry => (TitleId: entry.Key, Window: entry.Value.Where(d => string.CompareOrdinal(d.Key, first) >= 0).Select(d => d.Value).ToList()))
                .Where(t => t.Window.Count > 0)
                .Select(t => (t.TitleId, Seconds: t.Window.Sum(w => w.Seconds), Plays: t.Window.Sum(w => w.Plays)))
                .OrderByDescending(t => t.Seconds)
                .ThenByDescending(t => t.Plays)
                .ThenBy(t => t.TitleId)
                .ToList();
        }
    }

    /// <summary>Drops days outside the retention window and writes the file if anything changed.</summary>
    /// <param name="nowUtc">Current time.</param>
    public void Flush(DateTime nowUtc)
    {
        var cutoff = DateOnly.FromDateTime(nowUtc).AddDays(-RetentionDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string? json = null;
        lock (_lock)
        {
            foreach (var perDay in _days.Values)
            {
                foreach (var old in perDay.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
                {
                    perDay.Remove(old);
                    _dirty = true;
                }
            }

            foreach (var empty in _days.Where(d => d.Value.Count == 0).Select(d => d.Key).ToList())
            {
                _days.Remove(empty);
            }

            if (_dirty && _path is not null)
            {
                json = JsonSerializer.Serialize(_days);
                _dirty = false;
            }
        }

        if (json is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path!)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path!, overwrite: true);
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            _days = JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<string, Totals>>>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException)
        {
            // A damaged file only costs history; start fresh instead of failing the server.
            _days = new();
        }
    }

    /// <summary>Totals for one title on one day.</summary>
    public struct Totals
    {
        /// <summary>Gets or sets watched seconds.</summary>
        public long Seconds { get; set; }

        /// <summary>Gets or sets counted plays.</summary>
        public int Plays { get; set; }
    }
}
