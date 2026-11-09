using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Zorofin.Announcements;
using Jellyfin.Plugin.Zorofin.Companion;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Support;

/// <summary>
/// Answers the /support/* addresses the released Zorofin apps (iOS and Apple TV 1.3, Android 200 and the TV apps)
/// still call for alert settings, Notifications, Support and Recommended to request. Before the plugin these were
/// served by a server's own support service; here they come from the plugin and the companion, so today's store
/// apps get those features on any server. Newer apps move to /Zorofin/* addresses.
/// A server that still runs its own support service in front of Jellyfin keeps serving these paths itself.
/// </summary>
[ApiController]
[Authorize]
[Route("support")]
[Produces(MediaTypeNames.Application.Json)]
public class AppCompatibilityController : ControllerBase
{
    private const int MaxBody = 16 * 1024;
    private readonly SupportInbox _inbox;
    private readonly AnnouncementDismissals _dismissals;
    private readonly CompanionClient _companion;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppCompatibilityController"/> class.
    /// </summary>
    /// <param name="inbox">Support inbox.</param>
    /// <param name="dismissals">Announcement dismissals.</param>
    /// <param name="companion">Companion client.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    public AppCompatibilityController(SupportInbox inbox, AnnouncementDismissals dismissals, CompanionClient companion, IAuthorizationContext authorization)
    {
        _inbox = inbox;
        _dismissals = dismissals;
        _companion = companion;
        _authorization = authorization;
    }

    /// <summary>The server announcement in the older apps' shape.</summary>
    /// <returns>The announcement, or {"enabled":false}.</returns>
    [HttpGet("announcement/me")]
    public async Task<ActionResult> Announcement()
    {
        Response.Headers.CacheControl = "private, no-store";
        if (await ViewerAsync().ConfigureAwait(false) is not { } viewer)
        {
            return Unauthorized();
        }

        var current = AnnouncementFeed.Current(Plugin.Instance?.Configuration, DateTimeOffset.UtcNow);
        if (current is null)
        {
            return Json(new JsonObject { ["enabled"] = false });
        }

        current["dismissed"] = (bool)current["dismissible"]! && _dismissals.IsDismissed(viewer.Id, (string)current["id"]!);
        return Json(current);
    }

    /// <summary>The viewer's unread replies from the server owner.</summary>
    /// <returns>{"notifications":[...]}; empty while the inbox is off.</returns>
    [HttpGet("notifications")]
    public async Task<ActionResult> Notifications()
    {
        Response.Headers.CacheControl = "private, no-store";
        if (await ViewerAsync().ConfigureAwait(false) is not { } viewer)
        {
            return Unauthorized();
        }

        var list = new JsonArray();
        if (Plugin.Instance?.Configuration.SupportInboxEnabled == true)
        {
            foreach (var ticket in _inbox.UnreadReplies(viewer.Id))
            {
                var reply = new JsonObject
                {
                    ["id"] = ticket.Id,
                    ["status"] = ticket.Closed ? "Closed" : "Replied",
                    ["issueType"] = ticket.IssueType,
                    ["message"] = ticket.Reply,
                    ["updatedAt"] = ticket.RepliedAt!.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                };
                if (ticket.AffectedMedia.Length > 0)
                {
                    reply["affectedMedia"] = ticket.AffectedMedia;
                }

                list.Add(reply);
            }
        }

        return Json(new JsonObject { ["notifications"] = list });
    }

    /// <summary>Marks one reply read on all the viewer's devices.</summary>
    /// <param name="id">Ticket id.</param>
    /// <returns>204, or 404 when the viewer has no such ticket.</returns>
    [HttpPost("notifications/{id}/read")]
    public async Task<ActionResult> MarkRead(string id)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (await ViewerAsync().ConfigureAwait(false) is not { } viewer)
        {
            return Unauthorized();
        }

        return SupportInbox.IsTicketId(id) && _inbox.MarkRead(viewer.Id, id) ? NoContent() : NotFound();
    }

    /// <summary>A viewer's message to the server owner.</summary>
    /// <param name="report">The report.</param>
    /// <returns>202 with the ticket id.</returns>
    [HttpPost("ticket")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> Ticket([FromBody] TicketBody? report)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (await ViewerAsync().ConfigureAwait(false) is not { } viewer)
        {
            return Unauthorized();
        }

        if (Plugin.Instance?.Configuration.SupportInboxEnabled != true)
        {
            return NotFound();
        }

        if (report is null || !SupportInbox.IsValidReport(report.IssueType, report.Description, report.AffectedMedia))
        {
            return BadRequest();
        }

        var id = _inbox.Add(viewer.Id, viewer.Username, report.IssueType!, report.Description!, report.AffectedMedia);
        return id is null ? StatusCode(StatusCodes.Status429TooManyRequests) : StatusCode(StatusCodes.Status202Accepted, new JsonObject { ["ticketId"] = id });
    }

    /// <summary>Recommended to request, from the companion's recommendations module.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The feed, or 404 when the module is off.</returns>
    [HttpGet("recommendations")]
    public async Task<ActionResult> Recommendations(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (await ViewerAsync().ConfigureAwait(false) is not { } viewer)
        {
            return Unauthorized();
        }

        var response = await CallModuleAsync("recommendations", HttpMethod.Get, "feed", null, viewer, cancellationToken).ConfigureAwait(false);
        return response is null ? NotFound() : CompanionController.Map(response);
    }

    /// <summary>The viewer's alert settings, from the companion's notifications module.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The settings.</returns>
    [HttpGet("request-alerts")]
    public async Task<ActionResult> RequestAlerts(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (await ViewerAsync().ConfigureAwait(false) is not { } viewer)
        {
            return Unauthorized();
        }

        var response = await CallModuleAsync("notifications", HttpMethod.Get, "preferences", null, viewer, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return NotFound();
        }

        return response.Status == 200
            ? Json(RequestAlertsShape.Settings(response.Body, UserId(viewer), viewer.Username))
            : CompanionController.Map(response);
    }

    /// <summary>One change to the viewer's alert settings, such as email/save or push/create.</summary>
    /// <param name="channel">email or push.</param>
    /// <param name="change">The action ("action" is reserved in ASP.NET routes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated settings, with a message.</returns>
    [HttpPost("request-alerts/{channel}/{change}")]
    public async Task<ActionResult> RequestAlertsAction(string channel, string change, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (await ViewerAsync().ConfigureAwait(false) is not { } viewer)
        {
            return Unauthorized();
        }

        if (!RequestAlertsShape.Actions.TryGetValue(channel + "/" + change, out var step))
        {
            return NotFound();
        }

        var email = string.Empty;
        if (step.Path == "preferences/email")
        {
            var body = await ReadJsonAsync(cancellationToken).ConfigureAwait(false);
            email = body?["email"] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty;
        }

        var payload = step.Body(email);
        var response = await CallModuleAsync(
            "notifications",
            HttpMethod.Post,
            step.Path,
            payload is null ? null : Encoding.UTF8.GetBytes(payload.ToJsonString()),
            viewer,
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return NotFound();
        }

        if (response.Status is < 200 or >= 300)
        {
            var code = response.Body?["error"] is JsonValue error && error.TryGetValue<string>(out var c) ? c : string.Empty;
            if (response.Status is >= 400 and < 500 && RequestAlertsShape.Errors.TryGetValue(code, out var message))
            {
                return StatusCode(response.Status == 429 ? 429 : 400, new JsonObject { ["error"] = message });
            }

            return CompanionController.Map(response);
        }

        // Tests return no view; read the current one. Other actions return it.
        var view = response.Body?["channels"] is not null ? response.Body
            : (await CallModuleAsync("notifications", HttpMethod.Get, "preferences", null, viewer, cancellationToken).ConfigureAwait(false))?.Body;
        var result = RequestAlertsShape.Settings(view, UserId(viewer), viewer.Username);
        result["message"] = step.Message;
        if (step.ReturnsCredentials && RequestAlertsShape.Credentials(response.Body) is { } credentials)
        {
            result["credentials"] = credentials;
            result["message"] = "Private push is ready. Save the password now; it is shown only once.";
        }

        return Json(result);
    }

    private static string UserId(User viewer) => viewer.Id.ToString("N", CultureInfo.InvariantCulture);

    private static ContentResult Json(JsonNode body) => new() { Content = body.ToJsonString(), ContentType = MediaTypeNames.Application.Json, StatusCode = 200 };

    private async Task<CompanionResponse?> CallModuleAsync(string module, HttpMethod method, string path, byte[]? body, User viewer, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (CompanionClient.Endpoint(config) is not { } endpoint)
        {
            return null;
        }

        var features = await _companion.GetFeaturesAsync(config, cancellationToken).ConfigureAwait(false);
        if (!features.TryGetValue(module, out var feature) || !feature.Enabled)
        {
            return null;
        }

        return await _companion.SendAsync(
            endpoint,
            method,
            CompanionController.BuildPath(module, path)!,
            body,
            viewer.Id,
            viewer.HasPermission(PermissionKind.IsAdministrator),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonNode?> ReadJsonAsync(CancellationToken cancellationToken)
    {
        if (Request.ContentLength > MaxBody)
        {
            return null;
        }

        try
        {
            return await JsonNode.ParseAsync(Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<User?> ViewerAsync()
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return info is { IsAuthenticated: true, IsApiKey: false, User: not null } ? info.User : null;
    }

    /// <summary>A viewer's support report.</summary>
    public sealed class TicketBody
    {
        /// <summary>Gets or sets the kind of problem.</summary>
        [JsonPropertyName("issueType")]
        public string? IssueType { get; set; }

        /// <summary>Gets or sets what happened.</summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>Gets or sets the title, if any.</summary>
        [JsonPropertyName("affectedMedia")]
        public string? AffectedMedia { get; set; }
    }
}
