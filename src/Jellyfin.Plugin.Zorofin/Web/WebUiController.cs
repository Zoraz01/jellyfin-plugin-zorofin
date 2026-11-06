using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Zorofin.Companion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Web;

/// <summary>
/// Serves the web UI loader and passes the companion's webUi files through to the browser. Both are public
/// because the theme has to load on the sign-in page; the files are the same for every viewer.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Zorofin/Web")]
public partial class WebUiController : ControllerBase
{
    /// <summary>Companion module that serves the web UI.</summary>
    public const string ModuleId = "webUi";

    // Reads the companion's manifest, adds its <html> classes, then its styles and scripts in order. Scripts run in order too.
    private const string Loader = """
        (function () {
          'use strict';
          if (window.zorofinWebUiLoaded) return;
          window.zorofinWebUiLoaded = true;
          var base = new URL('../Zorofin/Web/ui/', document.baseURI).href;
          fetch(base + 'manifest.json', { credentials: 'omit' }).then(function (r) {
            return r.ok ? r.json() : null;
          }).then(function (m) {
            if (!m || m.schemaVersion !== 1) return;
            var root = document.documentElement;
            // A version in the path lets files be cached until the companion's look changes.
            var files = typeof m.version === 'string' && /^[a-z0-9]{1,32}$/.test(m.version) ? base + m.version + '/' : base;
            root.classList.add('zorofin-web');
            // Classes the companion asks for, such as zoro-ui for its theme, go on before the styles load.
            (m.classes || []).forEach(function (name) {
              if (typeof name === 'string' && /^[a-z][a-z0-9-]{0,63}$/.test(name)) root.classList.add(name);
            });
            (m.styles || []).forEach(function (name) {
              var link = document.createElement('link');
              link.rel = 'stylesheet'; link.href = files + encodeURIComponent(name); link.dataset.zorofin = '';
              document.head.appendChild(link);
            });
            (m.scripts || []).forEach(function (name) {
              var script = document.createElement('script');
              script.src = files + encodeURIComponent(name); script.async = false; script.dataset.zorofin = '';
              document.head.appendChild(script);
            });
          }).catch(function () {});
        }());
        """;

    private const string Off = "/* Zorofin web UI is off. */\n";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".woff2"] = "font/woff2",
    };

    private readonly CompanionClient _companion;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebUiController"/> class.
    /// </summary>
    /// <param name="companion">Companion client.</param>
    public WebUiController(CompanionClient companion)
    {
        _companion = companion;
    }

    /// <summary>Gets the loader script that index.html references.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JavaScript.</returns>
    [HttpGet("loader.js")]
    public async Task<ActionResult> GetLoader(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        var on = await IsOnAsync(cancellationToken).ConfigureAwait(false);
        return Content(on ? Loader : Off, "text/javascript; charset=utf-8");
    }

    /// <summary>Gets one web UI file from the companion.</summary>
    /// <param name="name">File name, or manifest.json.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file.</returns>
    [HttpGet("ui/{name}")]
    public Task<ActionResult> GetFile(string name, CancellationToken cancellationToken) => GetFileAsync(name, false, cancellationToken);

    /// <summary>Gets one web UI file under the version the companion's manifest names.</summary>
    /// <param name="version">The manifest's version; only used to tell browsers the file changed.</param>
    /// <param name="name">File name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file.</returns>
    [HttpGet("ui/{version}/{name}")]
    public Task<ActionResult> GetVersionedFile(string version, string name, CancellationToken cancellationToken) =>
        VersionPattern().IsMatch(version ?? string.Empty) && name != "manifest.json"
            ? GetFileAsync(name, true, cancellationToken)
            : Task.FromResult<ActionResult>(NotFound());

    /// <summary>Gets the sports row's artwork catalogue or one of its images, when the companion downloads them.</summary>
    /// <param name="name">events.json or a content-hashed image name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file.</returns>
    [HttpGet("ui/live-tv-logos/art/{name}")]
    public Task<ActionResult> GetSportsArt(string name, CancellationToken cancellationToken) => GetSportsArtAsync(name, cancellationToken);

    /// <summary>Gets sports artwork under the version the companion's manifest names.</summary>
    /// <param name="version">The manifest's version.</param>
    /// <param name="name">events.json or a content-hashed image name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file.</returns>
    [HttpGet("ui/{version}/live-tv-logos/art/{name}")]
    public Task<ActionResult> GetVersionedSportsArt(string version, string name, CancellationToken cancellationToken) =>
        VersionPattern().IsMatch(version ?? string.Empty)
            ? GetSportsArtAsync(name, cancellationToken)
            : Task.FromResult<ActionResult>(NotFound());

    /// <summary>The content type for a sports artwork name, or null when the name is not allowed.</summary>
    /// <param name="name">File name.</param>
    /// <returns>Content type or null.</returns>
    internal static string? SportsArtTypeOf(string? name)
    {
        if (name is null || !SportsArtName().IsMatch(name))
        {
            return null;
        }

        return name.EndsWith(".json", StringComparison.Ordinal) ? "application/json; charset=utf-8"
            : name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg";
    }

    private async Task<ActionResult> GetSportsArtAsync(string name, CancellationToken cancellationToken)
    {
        if (SportsArtTypeOf(name) is not { } type || !await IsOnAsync(cancellationToken).ConfigureAwait(false)
            || CompanionClient.Endpoint(Plugin.Instance?.Configuration) is not { } endpoint)
        {
            return NotFound();
        }

        var (status, body, _) = await _companion.GetPublicAsync(
            endpoint, "v1/public/" + ModuleId + "/live-tv-logos/art/" + Uri.EscapeDataString(name), cancellationToken).ConfigureAwait(false);
        if (status == StatusCodes.Status404NotFound)
        {
            return NotFound();
        }

        if (status != StatusCodes.Status200OK || body is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // The catalogue follows the schedule; image names carry a content hash and never change.
        Response.Headers.CacheControl = name == "events.json" ? "public, max-age=60" : "public, max-age=604800, immutable";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(body, type);
    }

    private async Task<ActionResult> GetFileAsync(string name, bool versioned, CancellationToken cancellationToken)
    {
        if (TypeOf(name) is not { } type || !await IsOnAsync(cancellationToken).ConfigureAwait(false)
            || CompanionClient.Endpoint(Plugin.Instance?.Configuration) is not { } endpoint)
        {
            return NotFound();
        }

        var (status, body, _) = await _companion.GetPublicAsync(
            endpoint, "v1/public/" + ModuleId + "/" + Uri.EscapeDataString(name), cancellationToken).ConfigureAwait(false);
        if (status == StatusCodes.Status404NotFound)
        {
            return NotFound();
        }

        if (status != StatusCodes.Status200OK || body is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // The manifest changes when an admin switches a part on or off, and must show on the next reload.
        Response.Headers.CacheControl = name == "manifest.json" ? "no-cache" : versioned ? "public, max-age=86400" : "public, max-age=300";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(body, type);
    }

    /// <summary>The content type for a web UI file name, or null when the name is not allowed.</summary>
    /// <param name="name">File name.</param>
    /// <returns>Content type or null.</returns>
    internal static string? TypeOf(string? name)
    {
        if (name is null || !FileName().IsMatch(name))
        {
            return null;
        }

        var dot = name.LastIndexOf('.');
        return dot > 0 && Types.TryGetValue(name[dot..], out var type) ? type : null;
    }

    private async Task<bool> IsOnAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.WebUiEnabled != true || CompanionClient.Endpoint(config) is null)
        {
            return false;
        }

        var features = await _companion.GetFeaturesAsync(config, cancellationToken).ConfigureAwait(false);
        return features.TryGetValue(ModuleId, out var feature) && feature.Enabled;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();

    [GeneratedRegex("^[a-z0-9]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^(?:events\\.json|sports-[a-f0-9]{24}-[a-f0-9]{8}\\.(?:png|jpg))$", RegexOptions.CultureInvariant)]
    private static partial Regex SportsArtName();
}
