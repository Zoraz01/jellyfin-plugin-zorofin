using System;
using System.Globalization;
using System.Net.Mime;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Zorofin.Configuration;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Requests;

/// <summary>
/// Same-server Requests routes for Zorofin apps (capability "requests" version 2).
/// The viewer comes from their Jellyfin session, never from the request body or query.
/// </summary>
[ApiController]
[Authorize]
[Route("Zorofin/Requests")]
[Produces(MediaTypeNames.Application.Json)]
public class RequestsController : ControllerBase
{
    private static readonly string[] DiscoverCategories = { "trending", "movies", "tv", "upcoming" };
    private static readonly string[] DiscoverKinds = { "movies", "tv" };
    private static readonly string[] TitleKinds = { "movie", "tv" };
    private static readonly string[] ActivityFilters = { "all", "pending", "approved", "processing", "available" };
    private readonly RequestsService _service;
    private readonly SeerrUserDirectory _users;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestsController"/> class.
    /// </summary>
    /// <param name="service">Requests service.</param>
    /// <param name="users">Seerr user directory.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    public RequestsController(RequestsService service, SeerrUserDirectory users, IAuthorizationContext authorization)
    {
        _service = service;
        _users = users;
        _authorization = authorization;
    }

    /// <summary>Whether Requests is available to this viewer.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Status.</returns>
    [HttpGet("user-status")]
    public async Task<ActionResult> UserStatus(CancellationToken cancellationToken)
    {
        var viewer = await ViewerAsync().ConfigureAwait(false);
        if (viewer is null)
        {
            return Unauthorized();
        }

        try
        {
            return Json(await _service.UserStatusAsync(Config, viewer.Value, cancellationToken).ConfigureAwait(false));
        }
        catch (UpstreamUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>4K availability.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Configuration.</returns>
    [HttpGet("config")]
    public Task<ActionResult> Configuration(CancellationToken cancellationToken) =>
        AsViewerAsync((seerr, _, ct) => Wrap(_service.ConfigurationAsync(seerr, ct)), cancellationToken);

    /// <summary>Season request settings.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Settings.</returns>
    [HttpGet("settings/partial-requests")]
    public Task<ActionResult> SeasonSettings(CancellationToken cancellationToken) =>
        AsViewerAsync((seerr, _, ct) => Wrap(_service.SeasonSettingsAsync(seerr, ct)), cancellationToken);

    /// <summary>The viewer's request limits.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Quota.</returns>
    [HttpGet("quota")]
    public Task<ActionResult> Quota(CancellationToken cancellationToken) =>
        AsViewerAsync(async (seerr, user, ct) => From(await _service.QuotaAsync(seerr, user, ct).ConfigureAwait(false)), cancellationToken);

    /// <summary>A discover row.</summary>
    /// <param name="category">trending, movies, tv or upcoming.</param>
    /// <param name="page">Page number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Titles.</returns>
    [HttpGet("discover/{category}")]
    public Task<ActionResult> Discover([FromRoute] string category, [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (Array.IndexOf(DiscoverCategories, category) < 0 || page is < 1 or > 500)
        {
            return Task.FromResult<ActionResult>(BadRequest());
        }

        var path = category == "upcoming" ? "movies/upcoming" : category;
        return AsViewerAsync(async (seerr, user, ct) => From(await _service.PageAsync(seerr, user, Invariant($"api/v1/discover/{path}?page={page}"), ct).ConfigureAwait(false)), cancellationToken);
    }

    /// <summary>Upcoming movies or shows.</summary>
    /// <param name="kind">movies or tv.</param>
    /// <param name="page">Page number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Titles.</returns>
    [HttpGet("discover/{kind}/upcoming")]
    public Task<ActionResult> Upcoming([FromRoute] string kind, [FromQuery] int page = 1, CancellationToken cancellationToken = default) =>
        Array.IndexOf(DiscoverKinds, kind) < 0 || page is < 1 or > 500
            ? Task.FromResult<ActionResult>(BadRequest())
            : AsViewerAsync(async (seerr, user, ct) => From(await _service.PageAsync(seerr, user, Invariant($"api/v1/discover/{kind}/upcoming?page={page}"), ct).ConfigureAwait(false)), cancellationToken);

    /// <summary>Popular movies or shows in one genre.</summary>
    /// <param name="kind">movies or tv.</param>
    /// <param name="genre">TMDB genre id.</param>
    /// <param name="page">Page number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Titles.</returns>
    [HttpGet("discover/{kind}/genre/{genre:int}")]
    public Task<ActionResult> Genre([FromRoute] string kind, [FromRoute] int genre, [FromQuery] int page = 1, CancellationToken cancellationToken = default) =>
        Array.IndexOf(DiscoverKinds, kind) < 0 || genre <= 0 || page is < 1 or > 500
            ? Task.FromResult<ActionResult>(BadRequest())
            : AsViewerAsync(async (seerr, user, ct) => From(await _service.PageAsync(seerr, user, Invariant($"api/v1/discover/{kind}/genre/{genre}?page={page}"), ct).ConfigureAwait(false)), cancellationToken);

    /// <summary>Movie or show genres, for browsing.</summary>
    /// <param name="kind">movie or tv.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Genres.</returns>
    [HttpGet("genres/{kind}")]
    public Task<ActionResult> Genres([FromRoute] string kind, CancellationToken cancellationToken) =>
        Array.IndexOf(TitleKinds, kind) < 0
            ? Task.FromResult<ActionResult>(BadRequest())
            : AsViewerAsync(async (seerr, _, ct) => await _service.GenresAsync(seerr, kind, ct).ConfigureAwait(false) is { } genres ? Json(genres) : StatusCode(StatusCodes.Status503ServiceUnavailable), cancellationToken);

    /// <summary>Titles like one movie or show.</summary>
    /// <param name="kind">movie or tv.</param>
    /// <param name="id">TMDB id.</param>
    /// <param name="page">Page number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Titles.</returns>
    [HttpGet("{kind}/{id:int}/recommendations")]
    public Task<ActionResult> Recommendations([FromRoute] string kind, [FromRoute] int id, [FromQuery] int page = 1, CancellationToken cancellationToken = default) =>
        Array.IndexOf(TitleKinds, kind) < 0 || id <= 0 || page is < 1 or > 500
            ? Task.FromResult<ActionResult>(BadRequest())
            : AsViewerAsync(async (seerr, user, ct) => From(await _service.PageAsync(seerr, user, Invariant($"api/v1/{kind}/{id}/recommendations?page={page}"), ct).ConfigureAwait(false)), cancellationToken);

    /// <summary>Search movies and shows.</summary>
    /// <param name="query">Search text.</param>
    /// <param name="page">Page number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Titles.</returns>
    [HttpGet("search")]
    public Task<ActionResult> Search([FromQuery] string? query, [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        var term = query?.Trim() ?? string.Empty;
        if (term.Length is 0 or > 256 || page is < 1 or > 500)
        {
            return Task.FromResult<ActionResult>(BadRequest());
        }

        return AsViewerAsync(async (seerr, user, ct) => From(await _service.PageAsync(seerr, user, Invariant($"api/v1/search?query={Uri.EscapeDataString(term)}&page={page}"), ct).ConfigureAwait(false)), cancellationToken);
    }

    /// <summary>One movie's details.</summary>
    /// <param name="id">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Title.</returns>
    [HttpGet("movie/{id:int}")]
    public Task<ActionResult> Movie([FromRoute] int id, CancellationToken cancellationToken) => Title("movie", id, cancellationToken);

    /// <summary>One show's details.</summary>
    /// <param name="id">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Title.</returns>
    [HttpGet("tv/{id:int}")]
    public Task<ActionResult> Show([FromRoute] int id, CancellationToken cancellationToken) => Title("tv", id, cancellationToken);

    /// <summary>Submit a request.</summary>
    /// <param name="intent">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Receipt.</returns>
    [HttpPost("request")]
    [Consumes(MediaTypeNames.Application.Json)]
    public Task<ActionResult> Submit([FromBody] RequestIntent intent, CancellationToken cancellationToken)
    {
        if (intent is null || !intent.IsValid())
        {
            return Task.FromResult<ActionResult>(BadRequest());
        }

        return AsViewerAsync(async (seerr, user, ct) => From(await _service.SubmitAsync(seerr, user, intent, ct).ConfigureAwait(false)), cancellationToken);
    }

    /// <summary>Request activity, newest first.</summary>
    /// <param name="userOnly">Only the viewer's own requests.</param>
    /// <param name="take">Page size.</param>
    /// <param name="skip">Offset.</param>
    /// <param name="filter">Status filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Activity.</returns>
    [HttpGet("activity")]
    public Task<ActionResult> Activity([FromQuery] bool userOnly = true, [FromQuery] int take = 24, [FromQuery] int skip = 0, [FromQuery] string filter = "all", CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 50 || skip is < 0 or > 10000 || Array.IndexOf(ActivityFilters, filter) < 0)
        {
            return Task.FromResult<ActionResult>(BadRequest());
        }

        return AsViewerAsync(async (seerr, user, ct) => From(await _service.ActivityAsync(seerr, user, userOnly, take, skip, filter, ct).ConfigureAwait(false)), cancellationToken);
    }

    /// <summary>Download progress for titles in requests this viewer can see.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Queue.</returns>
    [HttpGet("queue")]
    public Task<ActionResult> Queue(CancellationToken cancellationToken) =>
        AsViewerAsync((seerr, user, ct) => Wrap(_service.QueueAsync(Config, seerr, user, ct)), cancellationToken);

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static async Task<ActionResult> Wrap(Task<JsonObject> body) => Json(await body.ConfigureAwait(false));

    private static ContentResult Json(JsonNode body) => new() { Content = body.ToJsonString(), ContentType = MediaTypeNames.Application.Json, StatusCode = 200 };

    private static ActionResult From(UpstreamResult result)
    {
        if (result.Ok && result.Body is not null)
        {
            return Json(result.Body);
        }

        // Pass through the rejection kinds the apps understand; anything else is an unavailable upstream.
        var status = result.Status switch
        {
            400 or 403 or 404 or 409 or 422 or 429 => result.Status,
            0 or 401 => StatusCodes.Status503ServiceUnavailable, // unreachable, or our API key was refused (not the viewer)
            _ => StatusCodes.Status502BadGateway,
        };
        return new StatusCodeResult(status);
    }

    private Task<ActionResult> Title(string kind, int id, CancellationToken cancellationToken) =>
        id <= 0
            ? Task.FromResult<ActionResult>(BadRequest())
            : AsViewerAsync(async (seerr, user, ct) => From(await _service.TitleAsync(seerr, user, kind, id, ct).ConfigureAwait(false)), cancellationToken);

    private async Task<Guid?> ViewerAsync()
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return info is { IsAuthenticated: true, IsApiKey: false, User: not null } ? info.UserId : null;
    }

    private async Task<ActionResult> AsViewerAsync(Func<ServiceEndpoint, int, CancellationToken, Task<ActionResult>> action, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var viewer = await ViewerAsync().ConfigureAwait(false);
        if (viewer is null)
        {
            return Unauthorized();
        }

        if (RequestsService.Seerr(Plugin.Instance?.Configuration) is not { } seerr)
        {
            return NotFound();
        }

        try
        {
            var seerrUser = await _users.FindAsync(seerr, viewer.Value, cancellationToken).ConfigureAwait(false);
            return seerrUser is null ? StatusCode(StatusCodes.Status403Forbidden) : await action(seerr, seerrUser.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (UpstreamUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
