using System;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.Zorofin;

/// <summary>An operator-configured upstream service: an absolute http(s) base URL plus its API key.</summary>
public sealed class ServiceEndpoint
{
    private ServiceEndpoint(Uri baseUri, string apiKey)
    {
        BaseUri = baseUri;
        ApiKey = apiKey;
    }

    /// <summary>Gets the base URI, always ending with a slash so relative paths append.</summary>
    public Uri BaseUri { get; }

    /// <summary>Gets the API key.</summary>
    public string ApiKey { get; }

    /// <summary>Validates operator input. Rejects relative URLs, other schemes, embedded credentials and blank keys.</summary>
    /// <param name="url">Configured URL.</param>
    /// <param name="apiKey">Configured key.</param>
    /// <param name="endpoint">The endpoint when valid.</param>
    /// <returns>Whether the input is usable.</returns>
    public static bool TryCreate(string? url, string? apiKey, [NotNullWhen(true)] out ServiceEndpoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var text = uri.GetLeftPart(UriPartial.Path);
        endpoint = new ServiceEndpoint(new Uri(text.EndsWith('/') ? text : text + "/"), apiKey.Trim());
        return true;
    }

    /// <summary>Resolves a relative API path against the base URL.</summary>
    /// <param name="relative">Path and query without a leading slash.</param>
    /// <returns>The absolute URI.</returns>
    public Uri Resolve(string relative) => new(BaseUri, relative);
}
