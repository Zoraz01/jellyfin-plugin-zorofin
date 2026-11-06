using Jellyfin.Plugin.Zorofin.Web;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Zorofin.Tests;

public class WebUiTests
{
    [Theory]
    [InlineData("GET", "", "/web/", true)]
    [InlineData("GET", "", "/web/index.html", true)]
    [InlineData("GET", "/jellyfin", "/web/index.html", true)]
    [InlineData("GET", "", "/WEB/Index.HTML", true)]
    [InlineData("HEAD", "", "/web/index.html", false)]
    [InlineData("GET", "", "/web/main.bundle.js", false)]
    [InlineData("GET", "", "/web/index.html.map", false)]
    [InlineData("GET", "", "/Items", false)]
    public void RecognisesTheIndexPage(string method, string pathBase, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.PathBase = pathBase;
        context.Request.Path = path;
        Assert.Equal(expected, WebUiInjection.IsIndexRequest(context.Request));
    }

    [Fact]
    public void AddsTheLoaderOnceBeforeTheHeadCloses()
    {
        var page = "<html><head><title>Jellyfin</title></HEAD><body></body></html>";
        var once = WebUiInjection.Inject(page);
        Assert.Equal("<html><head><title>Jellyfin</title>" + WebUiInjection.LoaderTag + "</HEAD><body></body></html>", once);
        Assert.Equal(once, WebUiInjection.Inject(once));
        Assert.Equal("<p>no head</p>", WebUiInjection.Inject("<p>no head</p>"));
    }

    [Theory]
    [InlineData("10-theme.css", "text/css; charset=utf-8")]
    [InlineData("20-app.js", "text/javascript; charset=utf-8")]
    [InlineData("manifest.json", "application/json; charset=utf-8")]
    [InlineData("logo.SVG", "image/svg+xml")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("notes.txt", null)]
    [InlineData(".hidden.js", null)]
    [InlineData("../app.js", null)]
    [InlineData("a/b.js", null)]
    [InlineData("noextension", null)]
    [InlineData(null, null)]
    public void AllowsOnlyWebFiles(string? name, string? expected)
    {
        Assert.Equal(expected, WebUiController.TypeOf(name));
    }

    [Theory]
    [InlineData("events.json", "application/json; charset=utf-8")]
    [InlineData("sports-0123456789abcdef01234567-89abcdef.png", "image/png")]
    [InlineData("sports-0123456789abcdef01234567-89abcdef.jpg", "image/jpeg")]
    [InlineData("sports-0123456789abcdef01234567-89abcdef.svg", null)]
    [InlineData("sports-xyz.png", null)]
    [InlineData("state.json", null)]
    [InlineData("../events.json", null)]
    [InlineData(null, null)]
    public void AllowsOnlySportsArtFiles(string? name, string? expected)
    {
        Assert.Equal(expected, WebUiController.SportsArtTypeOf(name));
    }
}
