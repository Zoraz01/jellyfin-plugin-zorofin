using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Zorofin.Companion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Support;

/// <summary>
/// The link in the companion's "confirm your email" message. Today's apps have no box to type the code into,
/// so the email carries a link here: one button confirms the address. No sign-in is needed, because the code
/// itself is the proof; the companion locks the code after a few wrong tries and it expires in 30 minutes.
/// Opening the page changes nothing, so mail scanners that follow links don't confirm by accident.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Zorofin/Alerts")]
public class AlertsConfirmController : ControllerBase
{
    private readonly CompanionClient _companion;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlertsConfirmController"/> class.
    /// </summary>
    /// <param name="companion">Companion client.</param>
    public AlertsConfirmController(CompanionClient companion)
    {
        _companion = companion;
    }

    /// <summary>Shows the confirm button.</summary>
    /// <param name="u">Viewer id.</param>
    /// <param name="c">The emailed code.</param>
    /// <returns>An HTML page.</returns>
    [HttpGet("ConfirmEmail")]
    public ActionResult Page([FromQuery] string? u, [FromQuery] string? c)
    {
        if (!Valid(u, c, out _))
        {
            return Html(HttpStatusCode.BadRequest, "This link isn't complete", "Open the newest email from your server and use its button, or ask for a new code in Zorofin.", null);
        }

        var form = "<form method=\"post\"><input type=\"hidden\" name=\"u\" value=\"" + WebUtility.HtmlEncode(u) + "\">"
            + "<input type=\"hidden\" name=\"c\" value=\"" + WebUtility.HtmlEncode(c) + "\">"
            + "<button type=\"submit\">Turn on email alerts</button></form>";
        return Html(HttpStatusCode.OK, "Confirm email alerts", "You'll get an email when a title you requested is ready to watch.", form);
    }

    /// <summary>Confirms the address with the companion.</summary>
    /// <param name="u">Viewer id.</param>
    /// <param name="c">The emailed code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An HTML page saying what happened.</returns>
    [HttpPost("ConfirmEmail")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<ActionResult> Confirm([FromForm] string? u, [FromForm] string? c, CancellationToken cancellationToken)
    {
        if (!Valid(u, c, out var viewer))
        {
            return Html(HttpStatusCode.BadRequest, "This link isn't complete", "Open the newest email from your server and use its button.", null);
        }

        var config = Plugin.Instance?.Configuration;
        var features = await _companion.GetFeaturesAsync(config, cancellationToken).ConfigureAwait(false);
        if (CompanionClient.Endpoint(config) is not { } endpoint || !features.TryGetValue("notifications", out var feature) || !feature.Enabled)
        {
            return Html(HttpStatusCode.NotFound, "Email alerts are off", "This server doesn't send request alerts right now.", null);
        }

        var body = Encoding.UTF8.GetBytes(new JsonObject { ["code"] = c }.ToJsonString());
        var response = await _companion.SendAsync(endpoint, HttpMethod.Post, CompanionController.BuildPath("notifications", "preferences/email/confirm")!, body, viewer, false, cancellationToken).ConfigureAwait(false);
        if (response.Status == 200)
        {
            return Html(HttpStatusCode.OK, "Email alerts are on", "You can close this page. You'll get an email when a title you requested is ready.", null);
        }

        var error = response.Body?["error"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
        var (title, detail) = error switch
        {
            "code_expired" => ("This link expired", "Ask for a new code in Zorofin's alert settings."),
            "code_locked" => ("Too many tries", "Ask for a new code in Zorofin's alert settings."),
            "wrong_code" or "code_missing" => ("This link was replaced", "Use the newest email from your server, or ask for a new code."),
            _ => ("Couldn't confirm right now", "Try the button again in a minute."),
        };
        return Html(HttpStatusCode.BadRequest, title, detail, null);
    }

    private static bool Valid(string? u, string? c, out Guid viewer)
    {
        viewer = Guid.Empty;
        return Guid.TryParse(u, out viewer) && c is { Length: >= 4 and <= 8 } && c.All(char.IsAsciiDigit);
    }

    private ContentResult Html(HttpStatusCode status, string title, string detail, string? extra)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex";
        var page = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<title>" + WebUtility.HtmlEncode(title) + "</title><style>"
            + "body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0d0e12;color:#f3f6f4;font:17px/1.5 system-ui,-apple-system,Segoe UI,sans-serif}"
            + "main{max-width:420px;margin:24px;padding:32px;border:1px solid #303a33;border-top:4px solid #42d779;border-radius:16px;background:#171d19}"
            + "h1{margin:0 0 12px;font-size:26px}p{margin:0 0 20px;color:#c9ccd6}"
            + "button{font:inherit;font-weight:700;padding:14px 22px;border:0;border-radius:10px;background:#42d779;color:#041208;cursor:pointer}"
            + "</style></head><body><main><h1>" + WebUtility.HtmlEncode(title) + "</h1><p>" + WebUtility.HtmlEncode(detail) + "</p>"
            + (extra ?? string.Empty) + "</main></body></html>";
        return new ContentResult { Content = page, ContentType = "text/html; charset=utf-8", StatusCode = (int)status };
    }
}
