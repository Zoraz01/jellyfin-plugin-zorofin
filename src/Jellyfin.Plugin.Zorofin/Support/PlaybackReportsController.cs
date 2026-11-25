using System.Globalization;
using System.Net.Mime;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Support;

/// <summary>
/// Playback problem reports (capability "playbackReports" version 1): the apps post what went wrong, and
/// administrators read the list in the Dashboard. Off for everyone when the owner switches collection off.
/// </summary>
[ApiController]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class PlaybackReportsController : ControllerBase
{
    private readonly PlaybackReports _reports;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackReportsController"/> class.
    /// </summary>
    /// <param name="reports">Report store.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    public PlaybackReportsController(PlaybackReports reports, IAuthorizationContext authorization)
    {
        _reports = reports;
        _authorization = authorization;
    }

    /// <summary>Stores one problem from the signed-in viewer's app.</summary>
    /// <param name="body">What went wrong.</param>
    /// <returns>204; 400 for an unknown kind; 404 while collection is off; 429 when the viewer sent too many.</returns>
    [HttpPost("Zorofin/PlaybackReports")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> Add([FromBody] PlaybackReport? body)
    {
        Response.Headers.CacheControl = "private, no-store";
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info is not { IsAuthenticated: true, IsApiKey: false, User: not null })
        {
            return Unauthorized();
        }

        if (Plugin.Instance?.Configuration.PlaybackReportsEnabled != true)
        {
            return NotFound();
        }

        if (body is null || !PlaybackReports.IsKind(body.Kind))
        {
            return BadRequest();
        }

        if (string.IsNullOrWhiteSpace(body.Device))
        {
            body.Device = info.Device ?? string.Empty;
        }

        return _reports.Add(info.UserId, info.User.Username, body) ? NoContent() : StatusCode(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>Every report, newest first. Administrators only.</summary>
    /// <returns>{"reports":[...]}.</returns>
    [HttpGet("Zorofin/Admin/PlaybackReports")]
    public async Task<ActionResult> List()
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var list = new JsonArray();
        foreach (var r in _reports.All())
        {
            list.Add(new JsonObject
            {
                ["kind"] = r.Kind,
                ["userName"] = r.UserName,
                ["at"] = r.At.ToString("o", CultureInfo.InvariantCulture),
                ["itemId"] = r.ItemId,
                ["title"] = r.Title,
                ["platform"] = r.Platform,
                ["appVersion"] = r.AppVersion,
                ["device"] = r.Device,
                ["method"] = r.Method,
                ["reason"] = r.Reason,
                ["startMs"] = r.StartMs,
                ["positionSeconds"] = r.PositionSeconds,
            });
        }

        return new ContentResult { Content = new JsonObject { ["reports"] = list }.ToJsonString(), ContentType = MediaTypeNames.Application.Json };
    }

    /// <summary>Deletes every report. Administrators only.</summary>
    /// <returns>204.</returns>
    [HttpDelete("Zorofin/Admin/PlaybackReports")]
    public async Task<ActionResult> Clear()
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        _reports.Clear();
        return NoContent();
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
}
