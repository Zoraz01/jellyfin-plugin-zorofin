using System;
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
/// Lets a Jellyfin administrator read and change the companion's settings from the plugin's Dashboard page.
/// Calls go over the signed bridge with the administrator's id and admin flag; the companion refuses anyone else.
/// Secrets are write-only on the companion side and are never returned.
/// </summary>
[ApiController]
[Authorize]
[Route("Zorofin/Admin/Companion")]
[Produces(MediaTypeNames.Application.Json)]
public class CompanionSettingsController : ControllerBase
{
    /// <summary>The section that holds the companion's shared connections.</summary>
    public const string ConnectionsSection = "connections";

    private readonly CompanionClient _companion;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompanionSettingsController"/> class.
    /// </summary>
    /// <param name="companion">Companion client.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    public CompanionSettingsController(CompanionClient companion, IAuthorizationContext authorization)
    {
        _companion = companion;
        _authorization = authorization;
    }

    /// <summary>Gets every section of the companion's settings.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sections, with secrets shown only as set or not set.</returns>
    [HttpGet("Settings")]
    public Task<ActionResult> GetSettings(CancellationToken cancellationToken) =>
        ForwardAsync(HttpMethod.Get, "v1/admin/settings", false, cancellationToken);

    /// <summary>Saves one section. The companion applies it at once, or refuses it with the reason.</summary>
    /// <param name="section">Section id: "connections" or a module id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The saved section.</returns>
    [HttpPost("Settings/{section}")]
    public Task<ActionResult> SaveSection(string section, CancellationToken cancellationToken) =>
        SectionPath(section, false) is { } path ? ForwardAsync(HttpMethod.Post, path, true, cancellationToken) : Task.FromResult<ActionResult>(NotFound());

    /// <summary>Checks one section's settings and addresses without saving them.</summary>
    /// <param name="section">Section id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The checks and whether they passed.</returns>
    [HttpPost("Settings/{section}/Test")]
    public Task<ActionResult> TestSection(string section, CancellationToken cancellationToken) =>
        SectionPath(section, true) is { } path ? ForwardAsync(HttpMethod.Post, path, true, cancellationToken) : Task.FromResult<ActionResult>(NotFound());

    /// <summary>Builds the companion path for a section, refusing anything that is not a plain id.</summary>
    /// <param name="section">Section id.</param>
    /// <param name="test">Whether this is a test call.</param>
    /// <returns>The relative path, or null.</returns>
    internal static string? SectionPath(string? section, bool test)
    {
        if (section != ConnectionsSection && !CompanionClient.IsModuleId(section))
        {
            return null;
        }

        var path = new StringBuilder("v1/admin/settings/").Append(section);
        return (test ? path.Append("/test") : path).ToString();
    }

    private async Task<ActionResult> ForwardAsync(HttpMethod method, string relative, bool withBody, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info is not { IsAuthenticated: true, IsApiKey: false, User: not null })
        {
            return Unauthorized();
        }

        if (!info.User.HasPermission(PermissionKind.IsAdministrator))
        {
            return Forbid();
        }

        if (CompanionClient.Endpoint(Plugin.Instance?.Configuration) is not { } endpoint)
        {
            return NotFound();
        }

        byte[]? body = null;
        if (withBody)
        {
            var read = await CompanionController.ReadBodyAsync(Request, cancellationToken, CompanionClient.MaxAdminRequestBytes).ConfigureAwait(false);
            if (read.Status != 0)
            {
                return StatusCode(read.Status);
            }

            body = read.Body;
        }

        var response = await _companion.SendAsync(endpoint, method, relative, body, info.UserId, true, cancellationToken).ConfigureAwait(false);
        if (method == HttpMethod.Post && response.Status is >= 200 and < 300 && !relative.EndsWith("/test", StringComparison.Ordinal))
        {
            // A module may have been turned on or off; apps should see it on their next manifest request.
            _companion.ForgetFeatures();
        }

        return CompanionController.Map(response);
    }
}
