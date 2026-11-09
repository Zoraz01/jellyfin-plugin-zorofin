using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Zorofin.Support;

/// <summary>
/// Translates between the alert settings screen in the Zorofin apps (the older /support/request-alerts contract)
/// and the companion's notifications module (/v1/modules/notifications/preferences).
/// </summary>
public static class RequestAlertsShape
{
    /// <summary>What each app action becomes: the companion route, its body, and the message the apps show.</summary>
    /// <param name="Path">Route under the notifications module.</param>
    /// <param name="Body">JSON body, or null.</param>
    /// <param name="Message">Message for the viewer.</param>
    /// <param name="ReturnsCredentials">Whether a push password may come back.</param>
    public sealed record Step(string Path, Func<string, JsonObject?> Body, string Message, bool ReturnsCredentials = false);

    /// <summary>Gets the actions the apps send, keyed by their path after /support/request-alerts/.</summary>
    public static IReadOnlyDictionary<string, Step> Actions { get; } = new Dictionary<string, Step>(StringComparer.Ordinal)
    {
        ["email/save"] = new("preferences/email", email => new JsonObject { ["address"] = email },
            "We sent a 6-digit code to that address. Open the link in that email to turn on email alerts."),
        ["email/test"] = new("preferences/test", _ => new JsonObject { ["channel"] = "email" },
            "Test email sent. Check your inbox, Spam, or Promotions."),
        ["email/remove"] = new("preferences/email/remove", _ => null, "Email alerts turned off."),
        ["push/create"] = new("preferences", _ => new JsonObject { ["ntfyEnabled"] = true },
            "Push alerts are on.", ReturnsCredentials: true),
        ["push/test"] = new("preferences/test", _ => new JsonObject { ["channel"] = "ntfy" }, "Test push sent."),
        ["push/reset"] = new("preferences/push/reset-password", _ => null,
            "A new password was created. Update ntfy now; the previous password no longer works.", ReturnsCredentials: true),
        ["push/remove"] = new("preferences", _ => new JsonObject { ["ntfyEnabled"] = false }, "Push alerts turned off."),
    };

    /// <summary>Messages for the companion's error codes.</summary>
    public static IReadOnlyDictionary<string, string> Errors { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["invalid_email"] = "Enter a valid email address.",
        ["too_many_codes"] = "Too many codes were sent. Wait an hour and try again.",
        ["channel_not_enabled"] = "Turn that alert on first.",
        ["too_many_tests"] = "Too many test alerts. Wait an hour and try again.",
        ["no_push_login"] = "Private push alerts are not enabled.",
        ["email_unavailable"] = "Email alerts are not available on this server.",
    };

    /// <summary>Masks an address the way the apps expect, such as "ab*****@example.com".</summary>
    /// <param name="value">Email address.</param>
    /// <returns>The masked address, or empty.</returns>
    public static string Mask(string? value)
    {
        var email = (value ?? string.Empty).Trim().ToLowerInvariant();
        var at = email.LastIndexOf('@');
        if (at < 1)
        {
            return string.Empty;
        }

        var local = email[..at];
        var visible = local.Length <= 2 ? local[..1] : local[..2];
        return visible + new string('*', Math.Max(3, Math.Min(8, local.Length - visible.Length))) + email[at..];
    }

    /// <summary>Builds the settings screen's answer from the companion's preferences view.</summary>
    /// <param name="view">The companion's view.</param>
    /// <param name="userId">Viewer id, in Jellyfin's "N" format.</param>
    /// <param name="userName">Viewer name.</param>
    /// <returns>The app-facing settings.</returns>
    public static JsonObject Settings(JsonNode? view, string userId, string userName)
    {
        var channels = view?["channels"] as JsonObject;
        var email = view?["email"] as JsonObject;
        var ntfy = view?["ntfy"] as JsonObject;
        var emailOn = email is not null && Bool(email["enabled"]);
        var pushOn = ntfy is not null && Bool(ntfy["enabled"]);
        var name = userName ?? string.Empty;
        return new JsonObject
        {
            ["account"] = new JsonObject { ["id"] = userId, ["name"] = name.Length > 80 ? name[..80] : name },
            ["email"] = new JsonObject
            {
                ["available"] = channels is not null && Bool(channels["email"]),
                ["enabled"] = emailOn,
                ["maskedAddress"] = emailOn ? Mask(Text(email!["address"])) : string.Empty,
                ["pendingAddress"] = Mask(Text(view?["pendingEmail"])),
            },
            ["push"] = new JsonObject
            {
                ["available"] = channels is not null && Bool(channels["ntfy"]),
                ["enabled"] = pushOn,
                ["server"] = Text(ntfy?["server"]),
                ["topic"] = pushOn ? Text(ntfy!["topic"]) : string.Empty,
                ["username"] = pushOn ? Text(ntfy!["username"]) : string.Empty,
            },
        };
    }

    /// <summary>The push login to show once, when the companion returned a new password.</summary>
    /// <param name="view">The companion's view, with ntfyPassword.</param>
    /// <returns>The credentials, or null.</returns>
    public static JsonObject? Credentials(JsonNode? view)
    {
        var password = Text(view?["ntfyPassword"]);
        if (password.Length == 0)
        {
            return null;
        }

        var ntfy = view?["ntfy"] as JsonObject;
        return new JsonObject
        {
            ["server"] = Text(ntfy?["server"]),
            ["topic"] = Text(ntfy?["topic"]),
            ["username"] = Text(ntfy?["username"]),
            ["password"] = password,
        };
    }

    private static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var b) && b;

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : string.Empty;
}
