using System.Net.Mime;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Zorofin.Requests;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeController"/> class.
    /// </summary>
    /// <param name="service">Home builder.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    public HomeController(HomeRowsService service, IAuthorizationContext authorization)
    {
        _service = service;
        _authorization = authorization;
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
