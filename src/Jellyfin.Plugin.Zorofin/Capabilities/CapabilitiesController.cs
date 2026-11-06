using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Zorofin.Companion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Capabilities;

/// <summary>Tells signed-in Zorofin apps which optional modules this server offers.</summary>
[ApiController]
[Authorize]
[Route("Zorofin")]
[Produces(MediaTypeNames.Application.Json)]
public class CapabilitiesController : ControllerBase
{
    private readonly CompanionClient _companion;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapabilitiesController"/> class.
    /// </summary>
    /// <param name="companion">Companion client.</param>
    public CapabilitiesController(CompanionClient companion)
    {
        _companion = companion;
    }

    /// <summary>Gets the capability manifest, including companion modules when a companion is configured and reachable.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The manifest.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<CapabilityManifest>> GetCapabilities(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return new CapabilityManifest(1, new Dictionary<string, CapabilityFeature>());
        }

        var companion = await _companion.GetFeaturesAsync(config, cancellationToken).ConfigureAwait(false);
        return CapabilityManifest.Build(config, companion);
    }
}
