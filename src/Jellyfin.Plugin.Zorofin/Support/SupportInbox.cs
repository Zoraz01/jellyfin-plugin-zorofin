using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Zorofin.Support;

/// <summary>One message a viewer sent to the server owner, and the owner's reply.</summary>
public sealed class SupportTicket
{
    /// <summary>Gets or sets the ticket id, such as "Tk3m9x2q4p".</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin user id of the viewer who sent it.</summary>
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the viewer's name when they sent it, for the Dashboard.</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of problem the viewer picked.</summary>
    [JsonPropertyName("issueType")]
    public string IssueType { get; set; } = string.Empty;

    /// <summary>Gets or sets what the viewer wrote.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the title the viewer named, if any.</summary>
    [JsonPropertyName("affectedMedia")]
    public string AffectedMedia { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was sent.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the owner's reply, or empty.</summary>
    [JsonPropertyName("reply")]
    public string Reply { get; set; } = string.Empty;

    /// <summary>Gets or sets when the owner replied.</summary>
    [JsonPropertyName("repliedAt")]
    public DateTimeOffset? RepliedAt { get; set; }

    /// <summary>Gets or sets when the viewer marked the reply read.</summary>
    [JsonPropertyName("readAt")]
    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>Gets or sets a value indicating whether the owner closed it.</summary>
    [JsonPropertyName("closed")]
    public bool Closed { get; set; }
}

/// <summary>
/// The plugin's support inbox: viewers send a short message from an app, the owner replies from the Dashboard,
/// and the reply shows in that viewer's Notifications. Plain messages only; no AI and no outside service.
/// Stored in one JSON file; the newest <see cref="MaxTickets"/> tickets are kept.
/// </summary>
public class SupportInbox
{
    /// <summary>Tickets kept in total; the oldest are dropped.</summary>
    public const int MaxTickets = 500;

    /// <summary>Tickets one viewer may send per hour.</summary>
    public const int PerViewerPerHour = 5;

    /// <summary>Longest reply, matching what the apps show.</summary>
    public const int MaxReply = 1500;

    private const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private readonly object _lock = new();
    private readonly string? _path;
    private List<SupportTicket> _tickets = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SupportInbox"/> class.
    /// </summary>
    /// <param name="path">JSON file to persist to, or null to keep data in memory only.</param>
    public SupportInbox(string? path)
    {
        _path = path;
        Load();
    }

    /// <summary>Gets or sets the clock, for tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Whether a ticket id has the shape the apps accept.</summary>
    /// <param name="id">Candidate id.</param>
    /// <returns>True when valid.</returns>
    public static bool IsTicketId(string? id) =>
        id is { Length: >= 7 and <= 21 } && id[0] == 'T' && id.Skip(1).All(c => IdAlphabet.Contains(c, StringComparison.Ordinal));

    /// <summary>Checks a viewer's report the way the apps do.</summary>
    /// <param name="issueType">Kind of problem.</param>
    /// <param name="description">What happened.</param>
    /// <param name="affectedMedia">Optional title.</param>
    /// <returns>True when it can be stored.</returns>
    public static bool IsValidReport(string? issueType, string? description, string? affectedMedia)
    {
        var issue = (issueType ?? string.Empty).Trim();
        var text = (description ?? string.Empty).Trim();
        var media = (affectedMedia ?? string.Empty).Trim();
        return issue.Length is > 0 and <= 64 && text.Length is > 0 and <= 1500 && media.Length <= 200;
    }

    /// <summary>Stores a new ticket.</summary>
    /// <param name="user">Viewer id.</param>
    /// <param name="userName">Viewer name.</param>
    /// <param name="issueType">Kind of problem.</param>
    /// <param name="description">What happened.</param>
    /// <param name="affectedMedia">Optional title.</param>
    /// <returns>The new ticket id, or null when the viewer sent too many this hour.</returns>
    public string? Add(Guid user, string userName, string issueType, string description, string? affectedMedia)
    {
        var now = Clock();
        SupportTicket ticket;
        lock (_lock)
        {
            if (_tickets.Count(t => t.UserId == user && t.CreatedAt > now.AddHours(-1)) >= PerViewerPerHour)
            {
                return null;
            }

            ticket = new SupportTicket
            {
                Id = NewId(),
                UserId = user,
                UserName = Trim(userName, 80),
                IssueType = issueType.Trim(),
                Description = description.Trim(),
                AffectedMedia = (affectedMedia ?? string.Empty).Trim(),
                CreatedAt = now,
            };
            _tickets.Add(ticket);
            if (_tickets.Count > MaxTickets)
            {
                _tickets.RemoveRange(0, _tickets.Count - MaxTickets);
            }

            Save();
        }

        return ticket.Id;
    }

    /// <summary>Replies this viewer hasn't marked read, newest first.</summary>
    /// <param name="user">Viewer id.</param>
    /// <returns>Unread replies.</returns>
    public IReadOnlyList<SupportTicket> UnreadReplies(Guid user)
    {
        lock (_lock)
        {
            return _tickets
                .Where(t => t.UserId == user && t.RepliedAt is not null && (t.ReadAt is null || t.ReadAt < t.RepliedAt))
                .OrderByDescending(t => t.RepliedAt)
                .Take(50)
                .Select(Copy)
                .ToArray();
        }
    }

    /// <summary>Marks a reply read for the viewer who owns the ticket.</summary>
    /// <param name="user">Viewer id.</param>
    /// <param name="id">Ticket id.</param>
    /// <returns>False when the viewer has no such ticket.</returns>
    public bool MarkRead(Guid user, string id)
    {
        lock (_lock)
        {
            var ticket = _tickets.FirstOrDefault(t => t.UserId == user && string.Equals(t.Id, id, StringComparison.Ordinal));
            if (ticket is null)
            {
                return false;
            }

            ticket.ReadAt = Clock();
            Save();
            return true;
        }
    }

    /// <summary>Every ticket, newest first, for administrators.</summary>
    /// <returns>All tickets.</returns>
    public IReadOnlyList<SupportTicket> All()
    {
        lock (_lock)
        {
            return _tickets.OrderByDescending(t => t.CreatedAt).Select(Copy).ToArray();
        }
    }

    /// <summary>Sets or replaces the owner's reply; the viewer sees it as a new unread message.</summary>
    /// <param name="id">Ticket id.</param>
    /// <param name="reply">Reply text.</param>
    /// <returns>The updated ticket, or null when not found.</returns>
    public SupportTicket? SetReply(string id, string reply)
    {
        lock (_lock)
        {
            var ticket = _tickets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
            if (ticket is null)
            {
                return null;
            }

            ticket.Reply = reply.Trim();
            ticket.RepliedAt = Clock();
            ticket.ReadAt = null;
            Save();
            return Copy(ticket);
        }
    }

    /// <summary>Opens or closes a ticket.</summary>
    /// <param name="id">Ticket id.</param>
    /// <param name="closed">Whether it is closed.</param>
    /// <returns>False when not found.</returns>
    public bool SetClosed(string id, bool closed)
    {
        lock (_lock)
        {
            var ticket = _tickets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
            if (ticket is null)
            {
                return false;
            }

            ticket.Closed = closed;
            Save();
            return true;
        }
    }

    /// <summary>Deletes a ticket.</summary>
    /// <param name="id">Ticket id.</param>
    /// <returns>False when not found.</returns>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _tickets.RemoveAll(t => string.Equals(t.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                Save();
            }

            return removed;
        }
    }

    private static string Trim(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length > max ? text[..max] : text;
    }

    private static SupportTicket Copy(SupportTicket t) => new()
    {
        Id = t.Id,
        UserId = t.UserId,
        UserName = t.UserName,
        IssueType = t.IssueType,
        Description = t.Description,
        AffectedMedia = t.AffectedMedia,
        CreatedAt = t.CreatedAt,
        Reply = t.Reply,
        RepliedAt = t.RepliedAt,
        ReadAt = t.ReadAt,
        Closed = t.Closed,
    };

    private string NewId()
    {
        while (true)
        {
            var id = "T" + RandomNumberGenerator.GetString(IdAlphabet, 10);
            if (!_tickets.Any(t => string.Equals(t.Id, id, StringComparison.Ordinal)))
            {
                return id;
            }
        }
    }

    // Called with _lock held.
    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(_tickets);
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
            _tickets = JsonSerializer.Deserialize<List<SupportTicket>>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException)
        {
            // A damaged file loses old messages; never fail the server over it.
            _tickets = new();
        }
    }
}
