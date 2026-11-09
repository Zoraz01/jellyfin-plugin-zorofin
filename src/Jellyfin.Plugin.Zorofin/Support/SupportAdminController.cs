using System.Globalization;
using System.Net.Mime;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Support;

/// <summary>The Support tab in the Dashboard: read viewers' messages and reply. Administrators only.</summary>
[ApiController]
[Authorize]
[Route("Zorofin/Admin/Support")]
[Produces(MediaTypeNames.Application.Json)]
public class SupportAdminController : ControllerBase
{
    private readonly SupportInbox _inbox;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="SupportAdminController"/> class.
    /// </summary>
    /// <param name="inbox">Support inbox.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    public SupportAdminController(SupportInbox inbox, IAuthorizationContext authorization)
    {
        _inbox = inbox;
        _authorization = authorization;
    }

    /// <summary>Every message, newest first.</summary>
    /// <returns>{"tickets":[...]}.</returns>
    [HttpGet("Tickets")]
    public async Task<ActionResult> Tickets()
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var list = new JsonArray();
        foreach (var t in _inbox.All())
        {
            list.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["userId"] = t.UserId.ToString("N", CultureInfo.InvariantCulture),
                ["userName"] = t.UserName,
                ["issueType"] = t.IssueType,
                ["description"] = t.Description,
                ["affectedMedia"] = t.AffectedMedia,
                ["createdAt"] = t.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                ["reply"] = t.Reply,
                ["repliedAt"] = t.RepliedAt?.ToString("o", CultureInfo.InvariantCulture),
                ["read"] = t.ReadAt is not null && t.RepliedAt is not null && t.ReadAt >= t.RepliedAt,
                ["closed"] = t.Closed,
            });
        }

        return new ContentResult { Content = new JsonObject { ["tickets"] = list }.ToJsonString(), ContentType = MediaTypeNames.Application.Json };
    }

    /// <summary>Replies to a message; the viewer sees it in Notifications.</summary>
    /// <param name="id">Ticket id.</param>
    /// <param name="body">The reply.</param>
    /// <returns>204, 400 or 404.</returns>
    [HttpPost("Tickets/{id}/Reply")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> Reply(string id, [FromBody] ReplyBody? body)
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var text = (body?.Message ?? string.Empty).Trim();
        if (text.Length is 0 or > SupportInbox.MaxReply)
        {
            return BadRequest();
        }

        return SupportInbox.IsTicketId(id) && _inbox.SetReply(id, text) is not null ? NoContent() : NotFound();
    }

    /// <summary>Closes or reopens a message.</summary>
    /// <param name="id">Ticket id.</param>
    /// <param name="closed">Whether it is closed.</param>
    /// <returns>204 or 404.</returns>
    [HttpPost("Tickets/{id}/Closed")]
    public async Task<ActionResult> Close(string id, [FromQuery] bool closed = true)
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        return SupportInbox.IsTicketId(id) && _inbox.SetClosed(id, closed) ? NoContent() : NotFound();
    }

    /// <summary>Deletes a message.</summary>
    /// <param name="id">Ticket id.</param>
    /// <returns>204 or 404.</returns>
    [HttpDelete("Tickets/{id}")]
    public async Task<ActionResult> Delete(string id)
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        return SupportInbox.IsTicketId(id) && _inbox.Delete(id) ? NoContent() : NotFound();
    }

    private async Task<ActionResult?> DenyAsync()
    {
        Response.Headers.CacheControl = "private, no-store";
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info is not { IsAuthenticated: true })
        {
            return Unauthorized();
        }

        return !info.IsApiKey && info.User is { } user && user.HasPermission(PermissionKind.IsAdministrator) ? null : Forbid();
    }

    /// <summary>An owner's reply.</summary>
    public sealed class ReplyBody
    {
        /// <summary>Gets or sets the reply text.</summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
