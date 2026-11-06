using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Zorofin.Requests;

/// <summary>Result of one upstream call. A null body with status 0 means the service could not be reached.</summary>
/// <param name="Status">HTTP status, or 0 when unreachable.</param>
/// <param name="Body">Parsed JSON body, if any.</param>
public sealed record UpstreamResult(int Status, JsonNode? Body)
{
    /// <summary>Gets a value indicating whether the call succeeded.</summary>
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>Calls an operator-configured Seerr, Radarr or Sonarr with its API key. Never follows redirects to other hosts.</summary>
public class UpstreamClient
{
    /// <summary>Name of the HttpClient registered for upstream calls.</summary>
    public const string HttpClientName = "Zorofin.Upstream";

    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpstreamClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public UpstreamClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Sends a request.</summary>
    /// <param name="endpoint">The service.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="relativePath">Path and query relative to the base URL.</param>
    /// <param name="body">Optional JSON body.</param>
    /// <param name="actAsSeerrUser">Seerr user id to act as, when calling Seerr for a viewer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result.</returns>
    public virtual async Task<UpstreamResult> SendAsync(ServiceEndpoint endpoint, HttpMethod method, string relativePath, JsonNode? body, int? actAsSeerrUser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var request = new HttpRequestMessage(method, endpoint.Resolve(relativePath));
        request.Headers.Add("X-Api-Key", endpoint.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (actAsSeerrUser is { } user)
        {
            request.Headers.Add("X-API-User", user.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxBodyBytes)
            {
                return new UpstreamResult((int)HttpStatusCode.BadGateway, null);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? parsed = null;
            if (bytes.Length is > 0 and <= MaxBodyBytes)
            {
                try
                {
                    parsed = JsonNode.Parse(bytes);
                }
                catch (JsonException)
                {
                    parsed = null;
                }
            }

            return new UpstreamResult((int)response.StatusCode, parsed);
        }
        catch (HttpRequestException)
        {
            return new UpstreamResult(0, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpstreamResult(0, null);
        }
    }
}
