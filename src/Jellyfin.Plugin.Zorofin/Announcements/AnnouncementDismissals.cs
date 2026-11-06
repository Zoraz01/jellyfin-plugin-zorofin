using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Zorofin.Announcements;

/// <summary>
/// Which announcements each account has dismissed, so dismissing on one device hides it on the others.
/// Stores only Jellyfin user ids and announcement ids, the 50 most recent per account.
/// </summary>
public class AnnouncementDismissals
{
    private const int PerAccount = 50;
    private readonly object _lock = new();
    private readonly string? _path;
    private Dictionary<Guid, List<string>> _users = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AnnouncementDismissals"/> class.
    /// </summary>
    /// <param name="path">JSON file to persist to, or null to keep data in memory only.</param>
    public AnnouncementDismissals(string? path)
    {
        _path = path;
        Load();
    }

    /// <summary>Whether this account dismissed this announcement.</summary>
    /// <param name="user">Jellyfin user id.</param>
    /// <param name="id">Announcement id.</param>
    /// <returns>True when dismissed.</returns>
    public bool IsDismissed(Guid user, string id)
    {
        lock (_lock)
        {
            return _users.TryGetValue(user, out var ids) && ids.Contains(id, StringComparer.Ordinal);
        }
    }

    /// <summary>Records a dismissal.</summary>
    /// <param name="user">Jellyfin user id.</param>
    /// <param name="id">Announcement id.</param>
    public void Dismiss(Guid user, string id)
    {
        string json;
        lock (_lock)
        {
            if (!_users.TryGetValue(user, out var ids))
            {
                _users[user] = ids = new List<string>();
            }

            if (ids.Contains(id, StringComparer.Ordinal))
            {
                return;
            }

            ids.Add(id);
            if (ids.Count > PerAccount)
            {
                ids.RemoveRange(0, ids.Count - PerAccount);
            }

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
            _users = JsonSerializer.Deserialize<Dictionary<Guid, List<string>>>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException)
        {
            // A damaged file only means banners show again; never fail the server over it.
            _users = new();
        }
    }
}
