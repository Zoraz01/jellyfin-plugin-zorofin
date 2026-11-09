using System.Net.Mime;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Zorofin.Requests;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Home;

/// <summary>Zorofin Home rows for the apps and the web client (capabilities "homeRows" v2 and "homeSections" v1).</summary>
[ApiController]
[Authorize]
[Route("Zorofin/Home")]
[Produces(MediaTypeNames.Application.Json)]
public class HomeController : ControllerBase
{
    private readonly HomeRowsService _service;
    private readonly IAuthorizationContext _authorization;
    private readonly ILibraryManager _library;
    private readonly ContinueWatchingHides _hides;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeController"/> class.
    /// </summary>
    /// <param name="service">Home builder.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="hides">Titles viewers removed from Continue Watching.</param>
    public HomeController(HomeRowsService service, IAuthorizationContext authorization, ILibraryManager library, ContinueWatchingHides hides)
    {
        _service = service;
        _authorization = authorization;
        _library = library;
        _hides = hides;
    }

    /// <summary>
    /// Removes a movie or show from the viewer's Continue Watching. Progress is kept; it comes back when they play it
    /// again. An episode removes its whole show.
    /// </summary>
    /// <param name="itemId">Movie or episode id.</param>
    /// <returns>No content.</returns>
    [HttpPost("ContinueWatching/{itemId:guid}/Hide")]
    public Task<ActionResult> Hide([FromRoute] System.Guid itemId) => ChangeHidden(itemId, true);

    /// <summary>Undoes <see cref="Hide"/>.</summary>
    /// <param name="itemId">Movie or episode id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("ContinueWatching/{itemId:guid}/Hide")]
    public Task<ActionResult> Unhide([FromRoute] System.Guid itemId) => ChangeHidden(itemId, false);

    private async Task<ActionResult> ChangeHidden(System.Guid itemId, bool hide)
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info is not { IsAuthenticated: true, IsApiKey: false, User: { } user })
        {
            return Unauthorized();
        }

        if (Plugin.Instance?.Configuration?.HomeRowsEnabled != true)
        {
            return NotFound();
        }

        var item = _library.GetItemById(itemId);
        if (item is null || !item.IsVisibleStandalone(user))
        {
            return NotFound();
        }

        var key = item is MediaBrowser.Controller.Entities.TV.Episode episode && episode.SeriesId != System.Guid.Empty ? episode.SeriesId : item.Id;
        if (hide)
        {
            _hides.Hide(user.Id, key, System.DateTime.UtcNow);
        }
        else
        {
            _hides.Restore(user.Id, key);
        }

        _service.Forget(user.Id);
        return NoContent();
    }

    /// <summary>The seven app rows, in the same shape as the older /support/home/rows feed.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Feed.</returns>
    [HttpGet("Rows")]
    public Task<ActionResult> Rows(CancellationToken cancellationToken) => Serve(HomeFeeds.AppRows, cancellationToken, false);

    /// <summary>Every Home row for the web client, replacing Home Screen Sections.</summary>
    /// <param name="fresh">Rebuild now, after the viewer changed their teams.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sections.</returns>
    [HttpGet("Sections")]
    public Task<ActionResult> Sections([FromQuery] bool fresh, CancellationToken cancellationToken) => Serve(HomeFeeds.Sections, cancellationToken, fresh);

    private async Task<ActionResult> Serve(System.Func<HomeSnapshot, JsonObject> shape, CancellationToken cancellationToken, bool fresh)
    {
        Response.Headers.CacheControl = "private, no-store";
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info is not { IsAuthenticated: true, IsApiKey: false, User: { } user })
        {
            return Unauthorized();
        }

        var config = Plugin.Instance?.Configuration;
        if (config?.HomeRowsEnabled != true)
        {
            return NotFound();
        }

        try
        {
            var snapshot = await _service.GetAsync(user, config, fresh).WaitAsync(cancellationToken).ConfigureAwait(false);
            return Content(shape(snapshot).ToJsonString(), MediaTypeNames.Application.Json);
        }
        catch (HomeBusyException)
        {
            Response.Headers.RetryAfter = "5";
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (UpstreamUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
