using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Zorofin.Support;

/// <summary>One playback problem a Zorofin app noticed and sent on its own.</summary>
public sealed class PlaybackReport
{
    /// <summary>Gets or sets what went wrong; one of <see cref="PlaybackReports.Kinds"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin user id of the viewer.</summary>
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the viewer's name at the time.</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets when the server received it.</summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    /// <summary>Gets or sets the Jellyfin item id, or empty.</summary>
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the title that was playing.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the app's platform, such as "Android TV" or "Roku".</summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    /// <summary>Gets or sets the app version.</summary>
    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the device name.</summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    /// <summary>Gets or sets how it was playing: direct, on-device, remux, transcode or live.</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>Gets or sets the app's short reason, such as a player error.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets how long the start took in milliseconds, when known.</summary>
    [JsonPropertyName("startMs")]
    public int? StartMs { get; set; }

    /// <summary>Gets or sets where in the title it happened, in seconds, when known.</summary>
    [JsonPropertyName("positionSeconds")]
    public int? PositionSeconds { get; set; }
}

/// <summary>
/// Playback problems the Zorofin apps report on their own (capability "playbackReports" version 1), so the owner
/// sees what failed without a viewer writing in. Kept on this server in one JSON file; nothing leaves it. The
/// newest <see cref="MaxReports"/> are kept.
/// </summary>
public class PlaybackReports
{
    /// <summary>Reports kept in total; the oldest are dropped.</summary>
    public const int MaxReports = 2000;

    /// <summary>Reports one viewer may send per hour.</summary>
    public const int PerViewerPerHour = 40;

    /// <summary>The problems an app may report.</summary>
    public static readonly IReadOnlyList<string> Kinds = new[]
    {
        "start_failed", "stopped", "fallback", "stalled", "quality_lowered", "recovered", "slow_start", "app_error",
    };

    private readonly object _lock = new();
    private readonly string? _path;
    private List<PlaybackReport> _reports = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackReports"/> class.
    /// </summary>
    /// <param name="path">JSON file to persist to, or null to keep data in memory only.</param>
    public PlaybackReports(string? path)
    {
        _path = path;
        Load();
    }

    /// <summary>Gets or sets the clock, for tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Whether an app may report this kind of problem.</summary>
    /// <param name="kind">Candidate kind.</param>
    /// <returns>True when known.</returns>
    public static bool IsKind(string? kind) => kind is not null && Kinds.Contains(kind, StringComparer.Ordinal);

    /// <summary>Stores a report. The viewer and time come from the server, never from the app.</summary>
    /// <param name="user">Viewer id.</param>
    /// <param name="userName">Viewer name.</param>
    /// <param name="report">What the app sent; its kind must be known.</param>
    /// <returns>False when the viewer sent too many this hour.</returns>
    public bool Add(Guid user, string? userName, PlaybackReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var now = Clock();
        lock (_lock)
        {
            if (_reports.Count(r => r.UserId == user && r.At > now.AddHours(-1)) >= PerViewerPerHour)
            {
                return false;
            }

            _reports.Add(new PlaybackReport
            {
                Kind = report.Kind,
                UserId = user,
                UserName = Trim(userName, 80),
                At = now,
                ItemId = Trim(report.ItemId, 40),
                Title = Trim(report.Title, 200),
                Platform = Trim(report.Platform, 40),
                AppVersion = Trim(report.AppVersion, 40),
                Device = Trim(report.Device, 80),
                Method = Trim(report.Method, 20),
                Reason = Trim(report.Reason, 300),
                StartMs = Clamp(report.StartMs, 600_000),
                PositionSeconds = Clamp(report.PositionSeconds, 864_000),
            });
            if (_reports.Count > MaxReports)
            {
                _reports.RemoveRange(0, _reports.Count - MaxReports);
            }

            Save();
        }

        return true;
    }

    /// <summary>Every report, newest first, for administrators.</summary>
    /// <returns>All reports.</returns>
    public IReadOnlyList<PlaybackReport> All()
    {
        lock (_lock)
        {
            return _reports.AsEnumerable().Reverse().ToArray();
        }
    }

    /// <summary>Deletes every report.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _reports = new();
            Save();
        }
    }

    private static string Trim(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length > max ? text[..max] : text;
    }

    private static int? Clamp(int? value, int max) => value is null ? null : Math.Clamp(value.Value, 0, max);

    // Called with _lock held.
    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(_reports);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            _reports = JsonSerializer.Deserialize<List<PlaybackReport>>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException)
        {
            // A damaged file loses old reports; never fail the server over it.
            _reports = new();
        }
    }
}
