using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Zorofin.Configuration;
using Jellyfin.Plugin.Zorofin.Requests;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Arr;

/// <summary>
/// Downloads, Calendar and admin Sonarr/Radarr links for the web client, in place of Jellyfin Enhanced's.
/// </summary>
[ApiController]
[Authorize]
[Route("Zorofin/Arr")]
[Produces(MediaTypeNames.Application.Json)]
public class ArrController : ControllerBase
{
    private const int MaxCalendarDays = 62;
    private readonly IAuthorizationContext _authorization;
    private readonly ILibraryManager _library;
    private readonly ArrService _service;
    private readonly UpstreamClient _upstream;
    private readonly SeerrUserDirectory _seerrUsers;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrController"/> class.
    /// </summary>
    /// <param name="authorization">Jellyfin authorization context.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="service">Sonarr and Radarr reader.</param>
    /// <param name="upstream">Upstream client, for Seerr.</param>
    /// <param name="seerrUsers">Jellyfin to Seerr user map.</param>
    public ArrController(IAuthorizationContext authorization, ILibraryManager library, ArrService service, UpstreamClient upstream, SeerrUserDirectory seerrUsers)
    {
        _authorization = authorization;
        _library = library;
        _service = service;
        _upstream = upstream;
        _seerrUsers = seerrUsers;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// What Sonarr and Radarr are downloading. Administrators (or everyone, when the operator allows it) see all of it;
    /// other viewers see the titles from Seerr requests they can see.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Downloads.</returns>
    [HttpGet("Downloads")]
    public async Task<ActionResult> Downloads(CancellationToken cancellationToken)
    {
        var user = await ViewerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var config = Config;
        if (!config.DownloadsEnabled || (ArrService.Sonarr(config) is null && ArrService.Radarr(config) is null))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, no-store";
        HashSet<int>? visible = null;
        if (!config.DownloadsShowAll && !user.HasPermission(PermissionKind.IsAdministrator))
        {
            visible = await RequestedAsync(config, user.Id, cancellationToken).ConfigureAwait(false);
            if (visible.Count == 0)
            {
                return Json(new JsonObject { ["items"] = new JsonArray(), ["partial"] = false });
            }
        }

        return Json(await _service.DownloadsAsync(config, visible, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Upcoming episodes and movie releases.</summary>
    /// <param name="start">First day (yyyy-MM-dd), default today.</param>
    /// <param name="days">How many days, up to 62.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Events, each with the library item when this server has the title.</returns>
    [HttpGet("Calendar")]
    public async Task<ActionResult> Calendar([FromQuery] string? start, [FromQuery] int days = 14, CancellationToken cancellationToken = default)
    {
        var user = await ViewerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var config = Config;
        if (!config.CalendarEnabled || (ArrService.Sonarr(config) is null && ArrService.Radarr(config) is null))
        {
            return NotFound();
        }

        if (days is < 1 or > MaxCalendarDays)
        {
            return BadRequest();
        }

        DateTimeOffset from;
        if (string.IsNullOrEmpty(start))
        {
            from = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        }
        else if (!DateTime.TryParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return BadRequest();
        }
        else
        {
            from = new DateTimeOffset(parsed, TimeSpan.Zero);
        }

        // A day either side, so evening shows in time zones behind or ahead of UTC still land on the right local day.
        var events = await _service.CalendarAsync(config, from.AddDays(-1), from.AddDays(days + 1), cancellationToken).ConfigureAwait(false);
        Attach(user, events);
        Response.Headers.CacheControl = "private, max-age=300";
        return Json(new JsonObject
        {
            ["start"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["days"] = days,
            ["items"] = new JsonArray(events.Select(e => (JsonNode)e).ToArray()),
        });
    }

    /// <summary>Sonarr or Radarr link and tags for a title, for administrators.</summary>
    /// <param name="itemId">Jellyfin item id (an episode or season uses its show).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Links.</returns>
    [HttpGet("Item/{itemId:guid}")]
    public async Task<ActionResult> Item([FromRoute] Guid itemId, CancellationToken cancellationToken)
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

        var config = Config;
        if (!config.ArrLinksEnabled)
        {
            return NotFound();
        }

        var item = _library.GetItemById(itemId);
        item = item switch
        {
            Episode e => (BaseItem?)e.Series ?? item,
            Season s => (BaseItem?)s.Series ?? item,
            _ => item,
        };
        if (item is not (Series or Movie))
        {
            return NotFound();
        }

        item.ProviderIds.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.Tvdb.ToString(), out var tvdb);
        item.ProviderIds.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.Tmdb.ToString(), out var tmdb);
        var links = await _service.LinksAsync(config, item is Series, tvdb, tmdb, cancellationToken).ConfigureAwait(false);
        Response.Headers.CacheControl = "private, no-store";
        return Json(new JsonObject { ["links"] = links });
    }

    private static ContentResult Json(JsonNode body) => new() { Content = body.ToJsonString(), ContentType = MediaTypeNames.Application.Json, StatusCode = StatusCodes.Status200OK };

    // Adds "itemId" to each event whose show or movie is in the viewer's libraries.
    private void Attach(User user, List<JsonObject> events)
    {
        var shows = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var movies = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var e in events.Take(400))
        {
            var isMovie = (string?)e["kind"] == "movie";
            var key = isMovie ? RequestShaper.Int(e["tmdbId"])?.ToString(CultureInfo.InvariantCulture) : RequestShaper.Int(e["tvdbId"])?.ToString(CultureInfo.InvariantCulture);
            if (key is null)
            {
                continue;
            }

            var map = isMovie ? movies : shows;
            if (!map.TryGetValue(key, out var id))
            {
                id = Find(user, isMovie ? BaseItemKind.Movie : BaseItemKind.Series, isMovie ? MediaBrowser.Model.Entities.MetadataProvider.Tmdb : MediaBrowser.Model.Entities.MetadataProvider.Tvdb, key);
                map[key] = id;
            }

            if (id is { } found)
            {
                e["itemId"] = found.ToString("N", CultureInfo.InvariantCulture);
            }
        }
    }

    private Guid? Find(User user, BaseItemKind kind, MediaBrowser.Model.Entities.MetadataProvider provider, string id)
    {
        var name = provider.ToString();
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            IsVirtualItem = false,
            Limit = 5,
            HasAnyProviderId = new Dictionary<string, string> { [name] = id },
            DtoOptions = new DtoOptions(false) { EnableImages = false },
        };
        return _library.GetItemList(query)
            .FirstOrDefault(i => i.ProviderIds is not null && i.ProviderIds.TryGetValue(name, out var v) && v == id)?.Id;
    }

    // TMDB ids of the Seerr requests this viewer can see (their own, or all with Seerr's view-requests permission).
    private async Task<HashSet<int>> RequestedAsync(PluginConfiguration config, Guid viewer, CancellationToken cancellationToken)
    {
        var ids = new HashSet<int>();
        if (SeerrAutomations.Seerr(config) is not { } seerr)
        {
            return ids;
        }

        try
        {
            if (await _seerrUsers.FindAsync(seerr, viewer, cancellationToken).ConfigureAwait(false) is not { } seerrUser)
            {
                return ids;
            }

            var result = await _upstream.SendAsync(seerr, HttpMethod.Get, "api/v1/request?take=100&skip=0&filter=all&sort=added", null, seerrUser, cancellationToken).ConfigureAwait(false);
            foreach (var request in result.Ok ? result.Body?["results"] as JsonArray ?? new JsonArray() : new JsonArray())
            {
                if (RequestShaper.Int(request?["media"]?["tmdbId"]) is { } id)
                {
                    ids.Add(id);
                }
            }
        }
        catch (UpstreamUnavailableException)
        {
            // Seerr down: show nothing rather than everything.
        }

        return ids;
    }

    private async Task<User?> ViewerAsync()
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return info is { IsAuthenticated: true, IsApiKey: false, User: { } user } ? user : null;
    }
}
