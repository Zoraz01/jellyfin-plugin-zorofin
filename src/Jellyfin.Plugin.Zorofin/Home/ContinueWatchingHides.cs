using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Zorofin.Home;

/// <summary>
/// Titles each viewer removed from Continue Watching. Watch progress is kept: a title only stays hidden until the
/// viewer plays it (or, for a show, any of its episodes) again. Shows are keyed by series id, movies by item id.
/// Keeps the 200 most recent per account.
/// </summary>
public class ContinueWatchingHides
{
    private const int PerAccount = 200;
    private readonly object _lock = new();
    private readonly string? _path;
    private Dictionary<Guid, Dictionary<Guid, DateTime>> _users = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ContinueWatchingHides"/> class.
    /// </summary>
    /// <param name="path">JSON file to persist to, or null to keep data in memory only.</param>
    public ContinueWatchingHides(string? path)
    {
        _path = path;
        Load();
    }

    /// <summary>Whether a Continue Watching entry is hidden for this viewer.</summary>
    /// <param name="user">Viewer id.</param>
    /// <param name="entry">The movie or episode.</param>
    /// <returns>True when hidden and not played since.</returns>
    public bool IsHidden(Guid user, HomeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            if (!_users.TryGetValue(user, out var hidden))
            {
                return false;
            }

            var key = entry.Kind == "Episode" && entry.SeriesId is { } series ? series : entry.Id;
            return hidden.TryGetValue(key, out var at) && (entry.LastPlayed is not { } played || played <= at);
        }
    }

    /// <summary>Hides a title from this viewer's Continue Watching.</summary>
    /// <param name="user">Viewer id.</param>
    /// <param name="key">Series id for a show, item id for a movie.</param>
    /// <param name="nowUtc">Now.</param>
    public void Hide(Guid user, Guid key, DateTime nowUtc) => Change(user, hidden =>
    {
        hidden[key] = nowUtc;
        foreach (var old in hidden.OrderByDescending(h => h.Value).Skip(PerAccount).Select(h => h.Key).ToList())
        {
            hidden.Remove(old);
        }
    });

    /// <summary>Shows a hidden title again.</summary>
    /// <param name="user">Viewer id.</param>
    /// <param name="key">Series id for a show, item id for a movie.</param>
    public void Restore(Guid user, Guid key) => Change(user, hidden => hidden.Remove(key));

    private void Change(Guid user, Action<Dictionary<Guid, DateTime>> change)
    {
        string json;
        lock (_lock)
        {
            if (!_users.TryGetValue(user, out var hidden))
            {
                _users[user] = hidden = new Dictionary<Guid, DateTime>();
            }

            change(hidden);
            if (_path is null)
            {
                return;
            }

            json = JsonSerializer.Serialize(_users);
        }

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
            _users = JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<Guid, DateTime>>>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException)
        {
            _users = new();
        }
    }
}
