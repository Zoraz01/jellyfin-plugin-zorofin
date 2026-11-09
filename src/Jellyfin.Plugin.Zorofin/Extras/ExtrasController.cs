using System;
using System.Linq;
using System.Net.Mime;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Zorofin.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Extras;

/// <summary>
/// Web extras that replace the Media Bar and Jellyfin Enhanced plugins: which parts are on, banner trailer start
/// points, MDBList ratings, the Catch Up log and the prerequisite plugin check.
/// </summary>
[ApiController]
[Authorize]
[Route("Zorofin/Extras")]
[Produces(MediaTypeNames.Application.Json)]
public class ExtrasController : ControllerBase
{
    private readonly IAuthorizationContext _authorization;
    private readonly ILibraryManager _library;
    private readonly IPluginManager _plugins;
    private readonly ExtrasService _service;
    private readonly CatchUpLog _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExtrasController"/> class.
    /// </summary>
    /// <param name="authorization">Jellyfin authorization context.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="plugins">Plugin manager.</param>
    /// <param name="service">Public service client.</param>
    /// <param name="log">Catch Up log.</param>
    public ExtrasController(IAuthorizationContext authorization, ILibraryManager library, IPluginManager plugins, ExtrasService service, CatchUpLog log)
    {
        _authorization = authorization;
        _library = library;
        _plugins = plugins;
        _service = service;
        _log = log;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Which extras are on, for the web pack.</summary>
    /// <returns>Manifest.</returns>
    [HttpGet]
    public async Task<ActionResult> Manifest()
    {
        var user = await ViewerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        Response.Headers.CacheControl = "private, max-age=60";
        return Json(ExtrasModels.Manifest(Config, user.HasPermission(PermissionKind.IsAdministrator)));
    }

    /// <summary>Where a banner trailer should start, skipping its intro.</summary>
    /// <param name="videoId">YouTube video id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Start in seconds.</returns>
    [HttpGet("Trailer/{videoId}")]
    public async Task<ActionResult> Trailer([FromRoute] string videoId, CancellationToken cancellationToken)
    {
        if (await ViewerAsync().ConfigureAwait(false) is null)
        {
            return Unauthorized();
        }

        var config = Config;
        if (!config.HomeBannerTrailers || !ExtrasModels.YouTubeId(videoId))
        {
            return NotFound();
        }

        var start = config.HomeBannerSkipIntros ? await _service.TrailerStartAsync(videoId, cancellationToken).ConfigureAwait(false) : 0;
        Response.Headers.CacheControl = "private, max-age=86400";
        return Json(new JsonObject { ["start"] = start });
    }

    /// <summary>MDBList ratings for a movie or show the viewer can see.</summary>
    /// <param name="itemId">Jellyfin item id (an episode or season uses its show).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ratings.</returns>
    [HttpGet("Ratings/{itemId:guid}")]
    public async Task<ActionResult> Ratings([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var user = await ViewerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var config = Config;
        if (!config.RatingsEnabled || string.IsNullOrWhiteSpace(config.MdbListApiKey))
        {
            return NotFound();
        }

        var item = _library.GetItemById(itemId);
        if (item is null || !item.IsVisibleStandalone(user))
        {
            return NotFound();
        }

        item = item switch
        {
            Episode e => (BaseItem?)e.Series ?? item,
            Season s => (BaseItem?)s.Series ?? item,
            _ => item,
        };
        var kind = item switch { Movie => "movie", Series => "show", _ => null };
        if (kind is null)
        {
            return NotFound();
        }

        item.ProviderIds.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.Tmdb.ToString(), out var tmdb);
        item.ProviderIds.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.Imdb.ToString(), out var imdb);
        var ratings = await _service.RatingsAsync(config.MdbListApiKey, kind, tmdb, imdb, cancellationToken).ConfigureAwait(false);
        if (ratings is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=3600";
        return Json(ratings);
    }

    /// <summary>Records one Catch Up choice, when the operator keeps the log.</summary>
    /// <param name="body">{ "itemId": "...", "action": "watch|later|watched|skip" }.</param>
    /// <returns>No content.</returns>
    [HttpPost("CatchUp/Log")]
    public async Task<ActionResult> LogChoice([FromBody] JsonObject body)
    {
        var user = await ViewerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var config = Config;
        if (!config.CatchUpEnabled)
        {
            return NotFound();
        }

        var action = ExtrasModels.Text(body?["action"]);
        if (!Guid.TryParse(ExtrasModels.Text(body?["itemId"]), out var itemId) || action is null || !CatchUpLog.Actions.Contains(action))
        {
            return BadRequest();
        }

        var item = _library.GetItemById(itemId);
        if (item is null || !item.IsVisibleStandalone(user))
        {
            return NotFound();
        }

        if (config.CatchUpLogEnabled)
        {
            _log.Add(new CatchUpEntry(DateTimeOffset.UtcNow, user.Id, user.Username, item.Id, item.Name ?? string.Empty, action));
        }

        return NoContent();
    }

    /// <summary>The Catch Up log, for administrators.</summary>
    /// <param name="take">How many entries.</param>
    /// <returns>Entries and a per-viewer summary.</returns>
    [HttpGet("CatchUp/Log")]
    public async Task<ActionResult> ReadLog([FromQuery] int take = 200)
    {
        var user = await ViewerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        if (!user.HasPermission(PermissionKind.IsAdministrator))
        {
            return Forbid();
        }

        Response.Headers.CacheControl = "private, no-store";
        return Json(_log.Read(take));
    }

    /// <summary>The plugins Zorofin expects alongside it, and whether each is installed and running.</summary>
    /// <returns>List.</returns>
    [HttpGet("Prerequisites")]
    public async Task<ActionResult> Prerequisites()
    {
        var user = await ViewerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        if (!user.HasPermission(PermissionKind.IsAdministrator))
        {
            return Forbid();
        }

        static string Key(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var installed = _plugins.Plugins.ToList();
        var list = ExtrasModels.Prerequisites.Select(p =>
        {
            var hit = installed.FirstOrDefault(i => Key(i.Name) == Key(p.Name));
            var state = hit is null ? "missing" : hit.Manifest.Status == PluginStatus.Active ? "ok" : "inactive";
            return (JsonNode)new JsonObject
            {
                ["name"] = p.Name,
                ["why"] = p.Why,
                ["state"] = state,
                ["version"] = hit?.Version?.ToString(),
            };
        }).ToArray();
        Response.Headers.CacheControl = "private, no-store";
        return Json(new JsonObject { ["plugins"] = new JsonArray(list) });
    }

    private static ContentResult Json(JsonNode body) => new() { Content = body.ToJsonString(), ContentType = MediaTypeNames.Application.Json, StatusCode = StatusCodes.Status200OK };

    private async Task<User?> ViewerAsync()
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return info is { IsAuthenticated: true, IsApiKey: false, User: { } user } ? user : null;
    }
}
