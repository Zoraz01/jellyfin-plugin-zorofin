using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Zorofin.Configuration;

namespace Jellyfin.Plugin.Zorofin.Announcements;

/// <summary>
/// The server's one announcement, validated the same way as the older announcement receiver so web and app
/// clients can share one renderer. Invalid settings fail closed: no banner instead of a broken one.
/// </summary>
public static class AnnouncementFeed
{
    /// <summary>Announcement kinds clients style.</summary>
    public static readonly string[] Types = { "info", "success", "warning", "maintenance" };

    /// <summary>Longest title.</summary>
    public const int TitleLength = 80;

    /// <summary>Longest message.</summary>
    public const int MessageLength = 320;

    /// <summary>Longest button label.</summary>
    public const int LabelLength = 40;

    /// <summary>
    /// The current announcement for clients, or null when there is none to show right now.
    /// Its id changes whenever its content (or revision) changes, so an edited announcement shows again.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The announcement JSON, without the viewer's dismissal state.</returns>
    public static JsonObject? Current(PluginConfiguration? config, DateTimeOffset now)
    {
        if (config is not { AnnouncementEnabled: true })
        {
            return null;
        }

        var title = Clean(config.AnnouncementTitle);
        var message = Clean(config.AnnouncementMessage);
        if (title.Length is 0 or > TitleLength || message.Length is 0 or > MessageLength)
        {
            return null;
        }

        var type = Types.Contains(config.AnnouncementType, StringComparer.Ordinal) ? config.AnnouncementType : "info";
        if (!TryTime(config.AnnouncementStartAt, out var start) || !TryTime(config.AnnouncementEndAt, out var end)
            || (start is not null && end is not null && end <= start)
            || (start is not null && now < start) || (end is not null && now >= end))
        {
            return null;
        }

        var label = Clean(config.AnnouncementActionLabel);
        var href = SafeHref(config.AnnouncementActionHref);
        var hasAction = label.Length is > 0 and <= LabelLength && href is not null;
        var id = IdFor(type, title, message, hasAction ? label : string.Empty, hasAction ? href! : string.Empty, config.AnnouncementRevision);
        return new JsonObject
        {
            ["enabled"] = true,
            ["id"] = id,
            ["type"] = type,
            ["title"] = title,
            ["message"] = message,
            ["dismissible"] = config.AnnouncementDismissible,
            ["startAt"] = start?.ToString("o", CultureInfo.InvariantCulture),
            ["endAt"] = end?.ToString("o", CultureInfo.InvariantCulture),
            ["action"] = hasAction ? new JsonObject { ["label"] = label, ["href"] = href } : null,
        };
    }

    /// <summary>A link clients may open: a path on this server or an https URL. Anything else is dropped.</summary>
    /// <param name="value">Configured link.</param>
    /// <returns>The link, or null.</returns>
    public static string? SafeHref(string? value)
    {
        var href = (value ?? string.Empty).Trim();
        if (href.Length is 0 or > 500 || href.Any(char.IsControl))
        {
            return null;
        }

        if (href[0] == '/' && !href.StartsWith("//", StringComparison.Ordinal) && !href.Contains('\\', StringComparison.Ordinal))
        {
            return href;
        }

        return Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;
    }

    private static string Clean(string? value) => (value ?? string.Empty).Trim();

    private static bool TryTime(string? value, out DateTimeOffset? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            time = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static string IdFor(string type, string title, string message, string label, string href, int revision)
    {
        var text = string.Join('\n', type, title, message, label, href, revision.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "a-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
