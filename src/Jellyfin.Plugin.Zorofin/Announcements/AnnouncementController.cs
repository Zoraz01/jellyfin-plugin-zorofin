using System;
using System.Net.Mime;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Announcements;

/// <summary>
/// The announcement banner (capability "announcements" version 1). Same shape as the older
/// /support/announcement/me feed, so the web banner and the apps read one feed.
/// </summary>
[ApiController]
[Authorize]
[Route("Zorofin/Announcement")]
[Produces(MediaTypeNames.Application.Json)]
public class AnnouncementController : ControllerBase
{
    private readonly AnnouncementDismissals _dismissals;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnnouncementController"/> class.
    /// </summary>
    /// <param name="dismissals">Dismissal store.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    public AnnouncementController(AnnouncementDismissals dismissals, IAuthorizationContext authorization)
    {
        _dismissals = dismissals;
        _authorization = authorization;
    }

    /// <summary>The current announcement and whether this account dismissed it.</summary>
    /// <returns>The announcement, or {"enabled":false}.</returns>
    [HttpGet]
    public async Task<ActionResult> Get()
    {
        Response.Headers.CacheControl = "private, no-store";
        var viewer = await ViewerAsync().ConfigureAwait(false);
        if (viewer is null)
        {
            return Unauthorized();
        }

        var current = AnnouncementFeed.Current(Plugin.Instance?.Configuration, DateTimeOffset.UtcNow);
        if (current is null)
        {
            return Json(new JsonObject { ["enabled"] = false });
        }

        current["dismissed"] = (bool)current["dismissible"]! && _dismissals.IsDismissed(viewer.Value, (string)current["id"]!);
        return Json(current);
    }

    /// <summary>Hides the current announcement for this account on all its devices.</summary>
    /// <param name="body">The announcement id being dismissed.</param>
    /// <returns>The result.</returns>
    [HttpPost("Dismiss")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> Dismiss([FromBody] DismissBody? body)
    {
        Response.Headers.CacheControl = "private, no-store";
        var viewer = await ViewerAsync().ConfigureAwait(false);
        if (viewer is null)
        {
            return Unauthorized();
        }

        var current = AnnouncementFeed.Current(Plugin.Instance?.Configuration, DateTimeOffset.UtcNow);
        // Only the announcement showing now can be dismissed; a stale id is answered but not stored.
        if (current is null || body?.Id is not { } id || !string.Equals(id, (string)current["id"]!, StringComparison.Ordinal))
        {
            return Json(new JsonObject { ["dismissed"] = false, ["current"] = false });
        }

        if (!(bool)current["dismissible"]!)
        {
            return Conflict();
        }

        _dismissals.Dismiss(viewer.Value, id);
        return Json(new JsonObject { ["dismissed"] = true, ["current"] = true });
    }

    private static ContentResult Json(JsonNode body) => new() { Content = body.ToJsonString(), ContentType = MediaTypeNames.Application.Json, StatusCode = 200 };

    private async Task<Guid?> ViewerAsync()
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return info is { IsAuthenticated: true, IsApiKey: false, User: not null } ? info.UserId : null;
    }

    /// <summary>A dismissal request.</summary>
    public sealed class DismissBody
    {
        /// <summary>Gets or sets the announcement id.</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
