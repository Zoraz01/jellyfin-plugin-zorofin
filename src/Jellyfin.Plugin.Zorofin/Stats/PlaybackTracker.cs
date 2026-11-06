using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Zorofin.Stats;

/// <summary>
/// Measures how long each playback actually played, from Jellyfin's start, progress and stop events.
/// Paused time is left out, and a long silence between reports (a client that crashed or lost its
/// connection) counts as at most <see cref="MaxGap"/>.
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
    public void Start(string key, DateTime nowUtc, bool paused = false, string? playMethod = null, string? transcode = null)
    {
        lock (_lock)
        {
            if (_tracks.Count >= MaxTracked)
            {
                _tracks.Clear();
            }

            _tracks[key] = new Track { StartedUtc = nowUtc, LastUtc = nowUtc, Paused = paused, PlayMethod = playMethod, Transcode = transcode };
        }
    }

    /// <summary>Records a progress report. Unknown keys start tracking (the plugin may have loaded mid-play).</summary>
    /// <param name="key">Playback key.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="paused">Whether playback is paused now.</param>
    /// <param name="playMethod">How the stream is delivered, if known.</param>
    /// <param name="transcode">What is transcoded, if anything.</param>
    public void Progress(string key, DateTime nowUtc, bool paused, string? playMethod = null, string? transcode = null)
    {
        lock (_lock)
        {
            if (!_tracks.TryGetValue(key, out var track))
            {
                Start(key, nowUtc, paused, playMethod, transcode);
                return;
            }

            Advance(track, nowUtc);
            track.Paused = paused;
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
    /// <returns>Start time, seconds played and the last known delivery, or null for an unknown key.</returns>
    public (DateTime StartedUtc, int Seconds, string? PlayMethod, string? Transcode)? Stop(string key, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_tracks.Remove(key, out var track))
            {
                return null;
            }

            Advance(track, nowUtc);
            return (track.StartedUtc, (int)Math.Round(track.Active.TotalSeconds), track.PlayMethod, track.Transcode);
        }
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
    }
}
