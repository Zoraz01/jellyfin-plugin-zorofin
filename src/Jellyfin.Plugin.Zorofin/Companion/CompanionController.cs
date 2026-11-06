using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Companion;

/// <summary>
/// Forwards a signed-in viewer's calls to an enabled companion module. Only the viewer's id and admin flag
/// reach the companion; their Jellyfin token and any api_key query parameter are not forwarded.
/// </summary>
[ApiController]
[Authorize]
[Route("Zorofin/Companion")]
[Produces(MediaTypeNames.Application.Json)]
public class CompanionController : ControllerBase
{
    private static readonly string[] TokenQueryKeys = { "api_key", "apikey", "token" };
    private readonly CompanionClient _companion;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompanionController"/> class.
    /// </summary>
    /// <param name="companion">Companion client.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    public CompanionController(CompanionClient companion, IAuthorizationContext authorization)
    {
        _companion = companion;
        _authorization = authorization;
    }

    /// <summary>Forwards a module call.</summary>
    /// <param name="module">Companion module id, as listed in the capability manifest.</param>
    /// <param name="path">Module route.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The module's JSON response.</returns>
    [HttpGet("{module}/{**path}")]
    [HttpPost("{module}/{**path}")]
    public async Task<ActionResult> Forward(string module, string? path, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info is not { IsAuthenticated: true, IsApiKey: false, User: not null })
        {
            return Unauthorized();
        }

        var config = Plugin.Instance?.Configuration;
        if (CompanionClient.Endpoint(config) is not { } endpoint || !CompanionClient.IsModuleId(module)
            || BuildPath(module, path) is not { } relative)
        {
            return NotFound();
        }

        var features = await _companion.GetFeaturesAsync(config, cancellationToken).ConfigureAwait(false);
        if (!features.TryGetValue(module, out var feature) || !feature.Enabled)
        {
            return NotFound();
        }

        byte[]? body = null;
        if (HttpMethods.IsPost(Request.Method))
        {
            var read = await ReadBodyAsync(Request, cancellationToken).ConfigureAwait(false);
            if (read.Status != 0)
            {
                return StatusCode(read.Status);
            }

            body = read.Body;
        }

        var response = await _companion.SendAsync(
            endpoint,
            HttpMethods.IsPost(Request.Method) ? HttpMethod.Post : HttpMethod.Get,
            relative + ForwardedQuery(Request.Query),
            body,
            info.UserId,
            info.User.HasPermission(PermissionKind.IsAdministrator),
            cancellationToken).ConfigureAwait(false);
        return Map(response);
    }

    /// <summary>Builds the companion path, escaping each segment and refusing dot segments.</summary>
    /// <param name="module">Module id.</param>
    /// <param name="path">Decoded route path.</param>
    /// <returns>Relative path, or null when unsafe.</returns>
    internal static string? BuildPath(string module, string? path)
    {
        var builder = new StringBuilder("v1/modules/").Append(module);
        foreach (var segment in (path ?? string.Empty).Split('/'))
        {
            if (segment is "." or ".." || segment.Contains('\\', StringComparison.Ordinal))
            {
                return null;
            }

            if (segment.Length > 0)
            {
                builder.Append('/').Append(Uri.EscapeDataString(segment));
            }
        }

        return builder.ToString();
    }

    /// <summary>Copies the query string without Jellyfin credentials.</summary>
    /// <param name="query">Incoming query.</param>
    /// <returns>Query string including the leading "?", or empty.</returns>
    internal static string ForwardedQuery(IQueryCollection query)
    {
        var kept = query
            .Where(pair => !TokenQueryKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value => new System.Collections.Generic.KeyValuePair<string, string?>(pair.Key, value)));
        return QueryString.Create(kept).ToUriComponent();
    }

    /// <summary>Maps a companion response to what apps understand.</summary>
    /// <param name="response">Companion response.</param>
    /// <returns>The action result.</returns>
    internal static ActionResult Map(CompanionResponse response)
    {
        var status = response.Status switch
        {
            >= 200 and < 300 => response.Status,
            400 or 403 or 404 or 409 or 413 or 422 or 429 => response.Status,
            0 or 401 => StatusCodes.Status503ServiceUnavailable, // unreachable, or the bridge secret was refused (not the viewer)
            _ => StatusCodes.Status502BadGateway,
        };
        if (status == StatusCodes.Status204NoContent || response.Body is null || status >= 500)
        {
            return new StatusCodeResult(status);
        }

        return new ContentResult { Content = response.Body.ToJsonString(), ContentType = MediaTypeNames.Application.Json, StatusCode = status };
    }

    /// <summary>Reads a JSON request body up to the forwarding limit.</summary>
    /// <param name="request">Incoming request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="maxBytes">Largest body accepted; the viewer forwarding limit by default.</param>
    /// <returns>Zero and the body, or an error status.</returns>
    internal static async Task<(int Status, byte[]? Body)> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken, int maxBytes = CompanionClient.MaxRequestBytes)
    {
        if (request.ContentLength > maxBytes)
        {
            return (StatusCodes.Status413PayloadTooLarge, null);
        }

        if (request.ContentLength is not 0 && request.ContentType is { } type
            && !type.StartsWith(MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase))
        {
            return (StatusCodes.Status415UnsupportedMediaType, null);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return (StatusCodes.Status413PayloadTooLarge, null);
            }

            buffer.Write(chunk, 0, read);
        }

        return (0, buffer.ToArray());
    }
}
