using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Zorofin.Support;

namespace Jellyfin.Plugin.Zorofin.Notifications;

/// <summary>One account's read notifications, as every client sees them.</summary>
public sealed class NotificationReadState
{
    /// <summary>Gets or sets a value indicating whether a client has already recorded this account's history.</summary>
    /// <remarks>Until then the first client treats every request that is already ready as old news.</remarks>
    [JsonPropertyName("initialized")]
    public bool Initialized { get; set; }

    /// <summary>Gets or sets the read "your request is available" notices, by request id, oldest first.</summary>
    [JsonPropertyName("requests")]
    public List<int> Requests { get; set; } = new();

    /// <summary>Gets or sets the read support replies as "ticketId|updatedAt", oldest first.</summary>
    /// <remarks>A later reply on the same ticket has a new updatedAt, so it is unread again.</remarks>
    [JsonPropertyName("replies")]
    public List<string> Replies { get; set; } = new();
}

/// <summary>
/// Which notifications each account has read, so a notice read on one device is not new on the others.
/// Stores only Jellyfin user ids, request ids and ticket ids with their reply time.
/// </summary>
public class NotificationReads
{
    /// <summary>Most request ids kept per account.</summary>
    public const int RequestsPerAccount = 2000;

    /// <summary>Most replies kept per account.</summary>
    public const int RepliesPerAccount = 500;

    /// <summary>Most items one call may add.</summary>
    public const int MaxPerCall = 500;

    private readonly object _lock = new();
    private readonly string? _path;
    private Dictionary<Guid, NotificationReadState> _users = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationReads"/> class.
    /// </summary>
    /// <param name="path">JSON file to persist to, or null to keep data in memory only.</param>
    public NotificationReads(string? path)
    {
        _path = path;
        Load();
    }

    /// <summary>Builds the stored key of one support reply.</summary>
    /// <param name="ticketId">Ticket id.</param>
    /// <param name="updatedAt">The reply's time exactly as the inbox sent it.</param>
    /// <returns>The key, or null when either part is not valid.</returns>
    public static string? ReplyKey(string? ticketId, string? updatedAt)
    {
        if (!SupportInbox.IsTicketId(ticketId) || updatedAt is not { Length: > 0 and <= 40 }
            || !updatedAt.All(c => char.IsAsciiDigit(c) || "TZ:.+-".Contains(c, StringComparison.Ordinal)))
        {
            return null;
        }

        return ticketId + "|" + updatedAt;
    }

    /// <summary>Checks a stored reply key.</summary>
    /// <param name="key">"ticketId|updatedAt".</param>
    /// <returns>True when valid.</returns>
    public static bool IsReplyKey(string? key)
    {
        var bar = key?.IndexOf('|', StringComparison.Ordinal) ?? -1;
        return bar > 0 && ReplyKey(key![..bar], key[(bar + 1)..]) == key;
    }

    /// <summary>This account's read state.</summary>
    /// <param name="user">Jellyfin user id.</param>
    /// <returns>A copy of the state.</returns>
    public NotificationReadState Get(Guid user)
    {
        lock (_lock)
        {
            return Copy(_users.TryGetValue(user, out var state) ? state : new NotificationReadState());
        }
    }

    /// <summary>Adds read notifications. Reading is one-way: nothing here makes a notice unread.</summary>
    /// <param name="user">Jellyfin user id.</param>
    /// <param name="requests">Request ids read.</param>
    /// <param name="replies">Reply keys read (see <see cref="ReplyKey"/>).</param>
    /// <param name="initialize">Marks the account's history as recorded.</param>
    /// <returns>The state after the change.</returns>
    public NotificationReadState Add(Guid user, IEnumerable<int> requests, IEnumerable<string> replies, bool initialize)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(replies);
        var newRequests = requests.Where(id => id > 0).Distinct().ToList();
        var newReplies = replies.Where(IsReplyKey).Distinct(StringComparer.Ordinal).ToList();
        string json;
        NotificationReadState result;
        lock (_lock)
        {
            if (!_users.TryGetValue(user, out var state))
            {
                state = new NotificationReadState();
            }

            var changed = initialize && !state.Initialized;
            state.Initialized |= initialize;
            foreach (var id in newRequests.Where(id => !state.Requests.Contains(id)))
            {
                state.Requests.Add(id);
                changed = true;
            }

            foreach (var key in newReplies.Where(key => !state.Replies.Contains(key, StringComparer.Ordinal)))
            {
                state.Replies.Add(key);
                changed = true;
            }

            Trim(state.Requests, RequestsPerAccount);
            Trim(state.Replies, RepliesPerAccount);
            result = Copy(state);
            if (!changed)
            {
                return result;
            }

            _users[user] = state;
            if (_path is null)
            {
                return result;
            }

            json = JsonSerializer.Serialize(_users);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
        return result;
    }

    private static void Trim<T>(List<T> list, int max)
    {
        if (list.Count > max)
        {
            list.RemoveRange(0, list.Count - max);
        }
    }

    private static NotificationReadState Copy(NotificationReadState state) => new()
    {
        Initialized = state.Initialized,
        Requests = new List<int>(state.Requests),
        Replies = new List<string>(state.Replies),
    };

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            _users = JsonSerializer.Deserialize<Dictionary<Guid, NotificationReadState>>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException)
        {
            // A damaged file only means old notices show as new once; never fail the server over it.
            _users = new();
        }
    }
}
