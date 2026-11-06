using System;
using System.Linq;
using System.Net.Mime;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Stats;

/// <summary>Popular-this-week for Zorofin apps (capability "popularThisWeek" version 1).</summary>
[ApiController]
[Authorize]
[Route("Zorofin/Stats")]
[Produces(MediaTypeNames.Application.Json)]
public class StatsController : ControllerBase
{
    private readonly PlayStatsStore _store;
    private readonly ILibraryManager _library;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatsController"/> class.
    /// </summary>
    /// <param name="store">Statistics store.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    public StatsController(PlayStatsStore store, ILibraryManager library, IAuthorizationContext authorization)
    {
        _store = store;
        _library = library;
        _authorization = authorization;
    }

    /// <summary>Most-watched movies and shows that the viewer can access. Totals are server-wide and anonymous.</summary>
    /// <param name="days">Window, 1 to 30 days.</param>
    /// <param name="limit">Maximum titles, 1 to 50.</param>
    /// <returns>Ranked title ids.</returns>
    [HttpGet("Popular")]
    public async Task<ActionResult> Popular([FromQuery] int days = 7, [FromQuery] int limit = 20)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (days is < 1 or > 30 || limit is < 1 or > 50)
        {
            return BadRequest();
        }

        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info is not { IsAuthenticated: true, IsApiKey: false, User: { } user })
        {
            return Unauthorized();
        }

        if (Plugin.Instance?.Configuration.PlayStatsEnabled != true)
        {
            return NotFound();
        }

        var items = _store.Top(days, DateTime.UtcNow)
            .Select(t => (Entry: t, Item: _library.GetItemById(t.TitleId)))
            .Where(t => t.Item is not null && t.Item.IsVisibleStandalone(user))
            .Take(limit)
            .Select(t => (JsonNode?)new JsonObject { ["id"] = t.Item!.Id.ToString("N"), ["type"] = t.Item.GetBaseItemKind().ToString(), ["plays"] = t.Entry.Plays })
            .ToArray();
        var body = new JsonObject { ["days"] = days, ["items"] = new JsonArray(items) };
        return Content(body.ToJsonString(), MediaTypeNames.Application.Json);
    }
}
