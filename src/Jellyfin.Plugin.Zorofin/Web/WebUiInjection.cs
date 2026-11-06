using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Zorofin.Web;

/// <summary>
/// Adds one loader script tag to jellyfin-web's index.html while the web UI is turned on. Nothing else in
/// jellyfin-web is changed, and with the setting off the page is passed through untouched. This replaces the
/// need for a third-party injection plugin.
/// </summary>
public class WebUiStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(WebUiInjection.InvokeAsync);
            next(app);
        };
    }
}

/// <summary>The index.html rewrite used by <see cref="WebUiStartupFilter"/>.</summary>
public static class WebUiInjection
{
    /// <summary>The tag added to index.html. Relative, so a Jellyfin base URL keeps working.</summary>
    public const string LoaderTag = "<script defer src=\"../Zorofin/Web/loader.js\" data-zorofin-loader></script>";

    private const int MaxIndexBytes = 2 * 1024 * 1024;

    /// <summary>Whether a request is for jellyfin-web's index page, with or without a base URL.</summary>
    /// <param name="request">The request.</param>
    /// <returns>True for GET /web/ and GET /web/index.html.</returns>
    public static bool IsIndexRequest(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = (request.PathBase + request.Path).Value ?? string.Empty;
        return path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds the loader tag before the closing head tag, once.</summary>
    /// <param name="html">The page.</param>
    /// <returns>The page with the loader, or the page unchanged when it has one or has no head.</returns>
    public static string Inject(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (html.Contains("data-zorofin-loader", StringComparison.Ordinal))
        {
            return html;
        }

        var at = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html : html.Insert(at, LoaderTag);
    }

    /// <summary>Middleware entry point.</summary>
    /// <param name="context">HTTP context.</param>
    /// <param name="next">Next middleware.</param>
    /// <returns>A task.</returns>
    public static async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (Plugin.Instance?.Configuration.WebUiEnabled != true || !IsIndexRequest(context.Request))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Ask for the plain, full page so it can be read and changed.
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);
        context.Request.Headers.Remove(HeaderNames.IfNoneMatch);
        context.Request.Headers.Remove(HeaderNames.IfModifiedSince);

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        var response = context.Response;
        var isHtml = response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
        var encoded = response.Headers.ContainsKey(HeaderNames.ContentEncoding);
        if (response.StatusCode != StatusCodes.Status200OK || !isHtml || encoded || buffer.Length > MaxIndexBytes)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var page = Inject(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        var bytes = Encoding.UTF8.GetBytes(page);
        response.Headers.Remove(HeaderNames.ETag);
        response.Headers.Remove(HeaderNames.LastModified);
        response.Headers.CacheControl = "no-cache";
        response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}
