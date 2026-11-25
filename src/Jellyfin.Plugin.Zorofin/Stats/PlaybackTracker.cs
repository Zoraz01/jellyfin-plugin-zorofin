using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Zorofin.Stats;

/// <summary>
/// Measures how long each playback actually played, from Jellyfin's start, progress and stop events.
/// Paused time is left out, and a long silence between reports (a client that crashed or lost its
/// connection) counts as at most <see cref="MaxGap"/>. Each playback can carry fallback keys (session and
/// item, device and item) so a stop report with a different or missing play session id still finds it, and an opaque context so plays
/// that never get a stop report can still be written down by <see cref="Expire"/>.
/// </summary>
public sealed class PlaybackTracker
{
    /// <summary>Longest stretch between two reports that counts as playing.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(5);

    private const int MaxTracked = 10000;
    private readonly object _lock = new();
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);

    /// <summary>Gets the number of playbacks being tracked.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _tracks.Count;
            }
        }
    }

    /// <summary>Starts tracking a playback.</summary>
    /// <param name="key">Playback key.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="paused">Whether it starts paused.</param>
    /// <param name="playMethod">How the stream is delivered, if known.</param>
    /// <param name="transcode">What is transcoded, if anything.</param>
    /// <param name="fallbackKeys">Other keys a stop report may match (session and item, device and item), if any.</param>
    /// <param name="context">Caller data returned with the play when it expires, if any.</param>
    public void Start(string key, DateTime nowUtc, bool paused = false, string? playMethod = null, string? transcode = null, IReadOnlyList<string>? fallbackKeys = null, object? context = null)
    {
        lock (_lock)
        {
            if (_tracks.Count >= MaxTracked)
            {
                _tracks.Clear();
            }

            _tracks[key] = new Track { StartedUtc = nowUtc, LastUtc = nowUtc, Paused = paused, PlayMethod = playMethod, Transcode = transcode, FallbackKeys = fallbackKeys, Context = context };
        }
    }

    /// <summary>Records a progress report. Unknown keys start tracking (the plugin may have loaded mid-play).</summary>
    /// <param name="key">Playback key.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="paused">Whether playback is paused now.</param>
    /// <param name="playMethod">How the stream is delivered, if known.</param>
    /// <param name="transcode">What is transcoded, if anything.</param>
    /// <param name="fallbackKeys">Other keys a stop report may match (session and item, device and item), if any.</param>
    /// <param name="context">Caller data returned with the play when it expires, if any.</param>
    public void Progress(string key, DateTime nowUtc, bool paused, string? playMethod = null, string? transcode = null, IReadOnlyList<string>? fallbackKeys = null, object? context = null)
    {
        lock (_lock)
        {
            if (!_tracks.TryGetValue(key, out var track))
            {
                Start(key, nowUtc, paused, playMethod, transcode, fallbackKeys, context);
                return;
            }

            Advance(track, nowUtc);
            track.Paused = paused;
            track.FallbackKeys = fallbackKeys ?? track.FallbackKeys;
            track.Context = context ?? track.Context;
            if (!string.IsNullOrEmpty(playMethod))
            {
                track.PlayMethod = playMethod;
                track.Transcode = transcode;
            }
        }
    }

    /// <summary>Stops tracking and returns what was measured.</summary>
    /// <param name="key">Playback key.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="fallbackKeys">Keys to try, in order, when no playback has <paramref name="key"/>.</param>
    /// <returns>Start time, seconds played and the last known delivery, or null for an unknown key.</returns>
    public (DateTime StartedUtc, int Seconds, string? PlayMethod, string? Transcode)? Stop(string key, DateTime nowUtc, IReadOnlyList<string>? fallbackKeys = null) =>
        Stop(key, nowUtc, fallbackKeys, out _);

    /// <summary>Stops tracking and returns what was measured, plus the context the playback was tracked with.</summary>
    /// <param name="key">Playback key.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="fallbackKeys">Keys to try, in order, when no playback has <paramref name="key"/>.</param>
    /// <param name="context">The playback's last context, or null.</param>
    /// <returns>Start time, seconds played and the last known delivery, or null for an unknown key.</returns>
    public (DateTime StartedUtc, int Seconds, string? PlayMethod, string? Transcode)? Stop(string key, DateTime nowUtc, IReadOnlyList<string>? fallbackKeys, out object? context)
    {
        context = null;
        lock (_lock)
        {
            if (!_tracks.Remove(key, out var track))
            {
                if (FindByFallback(fallbackKeys) is not { } other || !_tracks.Remove(other, out track))
                {
                    return null;
                }
            }

            Advance(track, nowUtc);
            context = track.Context;
            return Measure(track);
        }
    }

    /// <summary>
    /// Ends playbacks that have sent nothing for <paramref name="idle"/> (the app closed, the TV slept or the
    /// connection dropped, so no stop report will come) and returns what they measured, up to their last report.
    /// </summary>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="idle">Silence after which a playback counts as over.</param>
    /// <returns>Each expired playback with its context.</returns>
    public IReadOnlyList<((DateTime StartedUtc, int Seconds, string? PlayMethod, string? Transcode) Measured, object? Context)> Expire(DateTime nowUtc, TimeSpan idle)
    {
        var expired = new List<((DateTime, int, string?, string?), object?)>();
        lock (_lock)
        {
            foreach (var (key, track) in new List<KeyValuePair<string, Track>>(_tracks))
            {
                if (nowUtc - track.LastUtc >= idle)
                {
                    _tracks.Remove(key);
                    expired.Add((Measure(track), track.Context));
                }
            }
        }

        return expired;
    }

    private static (DateTime StartedUtc, int Seconds, string? PlayMethod, string? Transcode) Measure(Track track) =>
        (track.StartedUtc, (int)Math.Round(track.Active.TotalSeconds), track.PlayMethod, track.Transcode);

    private string? FindByFallback(IReadOnlyList<string>? fallbackKeys)
    {
        if (fallbackKeys is null)
        {
            return null;
        }

        foreach (var wanted in fallbackKeys)
        {
            string? found = null;
            var latest = DateTime.MinValue;
            foreach (var (key, track) in _tracks)
            {
                if (track.FallbackKeys is { } keys && keys.Contains(wanted, StringComparer.Ordinal) && track.LastUtc >= latest)
                {
                    found = key;
                    latest = track.LastUtc;
                }
            }

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static void Advance(Track track, DateTime nowUtc)
    {
        if (!track.Paused && nowUtc > track.LastUtc)
        {
            var gap = nowUtc - track.LastUtc;
            track.Active += gap > MaxGap ? MaxGap : gap;
        }

        track.LastUtc = nowUtc > track.LastUtc ? nowUtc : track.LastUtc;
    }

    private sealed class Track
    {
        public DateTime StartedUtc { get; init; }

        public DateTime LastUtc { get; set; }

        public TimeSpan Active { get; set; }

        public bool Paused { get; set; }

        public string? PlayMethod { get; set; }

        public string? Transcode { get; set; }

        public IReadOnlyList<string>? FallbackKeys { get; set; }

        public object? Context { get; set; }
    }
}
