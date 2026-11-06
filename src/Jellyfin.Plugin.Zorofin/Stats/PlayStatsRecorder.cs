using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Zorofin.Stats;

/// <summary>
/// Listens to playback start, progress and stop. Feeds the anonymous Popular totals when "Popular this week"
/// is on, and the admin play history when "Play history" is on. Paused time counts toward neither.
/// </summary>
public sealed class PlayStatsRecorder : IHostedService, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);
    private readonly ISessionManager _sessions;
    private readonly PlayStatsStore _store;
    private readonly PlayHistoryStore _history;
    private readonly PlaybackTracker _tracker = new();
    private Timer? _timer;
    private DateTime _lastPrune = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayStatsRecorder"/> class.
    /// </summary>
    /// <param name="sessions">Jellyfin session manager.</param>
    /// <param name="store">Anonymous statistics store.</param>
    /// <param name="history">Admin play history store.</param>
    public PlayStatsRecorder(ISessionManager sessions, PlayStatsStore store, PlayHistoryStore history)
    {
        _sessions = sessions;
        _store = store;
        _history = history;
    }

    /// <summary>Resolves what a play counts toward: the movie itself, or an episode's series.</summary>
    /// <param name="item">Played item.</param>
    /// <returns>Title id, or empty for anything else (music, trailers, Live TV).</returns>
    public static Guid TitleFor(object? item) => item switch
    {
        Episode episode => episode.SeriesId,
        Movie movie => movie.Id,
        _ => Guid.Empty,
    };

    /// <summary>Builds the history entry for a finished play.</summary>
    /// <param name="item">Played item.</param>
    /// <param name="userId">Viewer id.</param>
    /// <param name="userName">Viewer name.</param>
    /// <param name="client">Client app.</param>
    /// <param name="device">Device name.</param>
    /// <param name="measured">What the tracker measured.</param>
    /// <param name="completed">Whether it was played to the end.</param>
    /// <returns>The entry.</returns>
    public static PlayEvent Describe(BaseItem item, Guid userId, string? userName, string? client, string? device, (DateTime StartedUtc, int Seconds, string? PlayMethod, string? Transcode) measured, bool completed)
    {
        ArgumentNullException.ThrowIfNull(item);
        var episode = item as Episode;
        return new PlayEvent
        {
            StartedUtc = measured.StartedUtc,
            Seconds = measured.Seconds,
            UserId = userId,
            UserName = userName ?? string.Empty,
            ItemId = item.Id,
            ItemType = item.GetBaseItemKind().ToString(),
            ItemName = item.Name ?? string.Empty,
            SeriesId = episode?.SeriesId,
            SeriesName = episode?.SeriesName,
            Season = episode?.ParentIndexNumber,
            Episode = episode?.IndexNumber,
            Client = client ?? string.Empty,
            Device = device ?? string.Empty,
            PlayMethod = measured.PlayMethod ?? string.Empty,
            Transcode = measured.Transcode,
            Completed = completed,
        };
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnStart;
        _sessions.PlaybackProgress += OnProgress;
        _sessions.PlaybackStopped += OnStop;
        _timer = new Timer(_ => Tick(), null, FlushInterval, FlushInterval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnStart;
        _sessions.PlaybackProgress -= OnProgress;
        _sessions.PlaybackStopped -= OnStop;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _store.Flush(DateTime.UtcNow);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();

    private static bool StatsOn => Plugin.Instance?.Configuration.PlayStatsEnabled == true;

    private static bool HistoryOn => Plugin.Instance?.Configuration.PlayHistoryEnabled == true;

    private static string? Key(PlaybackProgressEventArgs e) =>
        e.Item is null ? null : (e.PlaySessionId ?? e.Session?.Id ?? e.DeviceId) + "|" + e.Item.Id.ToString("N");

    private static (string? Method, string? Transcode) Delivery(PlaybackProgressEventArgs e)
    {
        var method = e.Session?.PlayState?.PlayMethod?.ToString();
        if (method != "Transcode" || e.Session?.TranscodingInfo is not { } info)
        {
            return (method, null);
        }

        var parts = new List<string>(2);
        if (!info.IsVideoDirect)
        {
            parts.Add("video");
        }

        if (!info.IsAudioDirect)
        {
            parts.Add("audio");
        }

        return (method, parts.Count == 0 ? "container" : string.Join(", ", parts));
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        _store.Flush(now);
        if (now - _lastPrune > TimeSpan.FromHours(6))
        {
            _lastPrune = now;
            _history.Prune(Plugin.Instance?.Configuration.PlayHistoryRetentionDays ?? 0, now);
        }
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs e)
    {
        if ((StatsOn || HistoryOn) && e.Item is not null && Key(e) is { } key)
        {
            var (method, transcode) = Delivery(e);
            _tracker.Start(key, DateTime.UtcNow, e.IsPaused, method, transcode);
        }
    }

    private void OnProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if ((StatsOn || HistoryOn) && e.Item is not null && Key(e) is { } key)
        {
            var (method, transcode) = Delivery(e);
            _tracker.Progress(key, DateTime.UtcNow, e.IsPaused, method, transcode);
        }
    }

    private void OnStop(object? sender, PlaybackStopEventArgs e)
    {
        if (Key(e) is not { } key || _tracker.Stop(key, DateTime.UtcNow) is not { } measured)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (StatsOn)
        {
            _store.Record(TitleFor(e.Item), TimeSpan.FromSeconds(measured.Seconds), now);
        }

        if (HistoryOn && e.Item is not null && e.Session is { } session)
        {
            _history.Add(Describe(e.Item, session.UserId, session.UserName, e.ClientName ?? session.Client, e.DeviceName ?? session.DeviceName, measured, e.PlayedToCompletion));
        }
    }
}
