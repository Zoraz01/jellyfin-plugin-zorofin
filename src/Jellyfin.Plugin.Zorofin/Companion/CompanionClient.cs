using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Zorofin.Capabilities;
using Jellyfin.Plugin.Zorofin.Configuration;

namespace Jellyfin.Plugin.Zorofin.Companion;

/// <summary>
/// Talks to the optional closed companion over the signed bridge (docs/COMPANION_BRIDGE.md).
/// The viewer's Jellyfin token is never sent; the companion learns only the viewer's id and admin flag.
/// </summary>
public partial class CompanionClient
{
    /// <summary>Name of the HttpClient registered for companion calls.</summary>
    public const string HttpClientName = "Zorofin.Companion";

    /// <summary>Shortest accepted shared secret.</summary>
    public const int MinSecretLength = 32;

    /// <summary>Largest request body forwarded to the companion.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>Largest body an administrator may send to the companion's settings, which can carry logo images.</summary>
    public const int MaxAdminRequestBytes = 2 * 1024 * 1024;

    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxFeatures = 32;
    private static readonly TimeSpan CapabilitiesTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CapabilitiesTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(15);
    private static readonly IReadOnlyDictionary<string, CapabilityFeature> NoFeatures = new Dictionary<string, CapabilityFeature>();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Lock _cacheLock = new();
    private (string Key, DateTimeOffset Expires, IReadOnlyDictionary<string, CapabilityFeature> Features)? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompanionClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public CompanionClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Gets or sets the clock. Tests replace it.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the nonce source. Tests replace it.</summary>
    internal Func<string> NewNonce { get; set; } = () => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Gets the configured companion, if the URL and secret are usable.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The endpoint, or null when the companion is not configured.</returns>
    public static ServiceEndpoint? Endpoint(PluginConfiguration? config)
    {
        if (config is null || (config.CompanionSecret?.Trim().Length ?? 0) < MinSecretLength
            || !ServiceEndpoint.TryCreate(config.CompanionUrl, config.CompanionSecret, out var endpoint))
        {
            return null;
        }

        // The signature covers the path the companion sees, so a path prefix (for example a stripping reverse proxy) is not supported.
        return endpoint.BaseUri.AbsolutePath == "/" ? endpoint : null;
    }

    /// <summary>Drops the cached feature list, so a settings change shows in the manifest at once.</summary>
    public void ForgetFeatures()
    {
        lock (_cacheLock)
        {
            _cache = null;
        }
    }

    /// <summary>Whether a module id is well formed.</summary>
    /// <param name="module">Module id.</param>
    /// <returns>True when valid.</returns>
    public static bool IsModuleId(string? module) => module is not null && ModuleIdPattern().IsMatch(module);

    /// <summary>Gets the companion's advertised features. Returns none when it is not configured or not reachable.</summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Features keyed by module id.</returns>
    public virtual async Task<IReadOnlyDictionary<string, CapabilityFeature>> GetFeaturesAsync(PluginConfiguration? config, CancellationToken cancellationToken)
    {
        if (Endpoint(config) is not { } endpoint)
        {
            return NoFeatures;
        }

        var key = endpoint.BaseUri + "\n" + endpoint.ApiKey;
        lock (_cacheLock)
        {
            if (_cache is { } cached && cached.Key == key && cached.Expires > Clock())
            {
                return cached.Features;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CapabilitiesTimeout);
        CompanionResponse response;
        try
        {
            response = await SendAsync(endpoint, HttpMethod.Get, "v1/capabilities", null, null, false, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response = new CompanionResponse(0, null);
        }

        var ok = response.Status is >= 200 and < 300;
        var features = ok ? ParseFeatures(response.Body) : NoFeatures;
        lock (_cacheLock)
        {
            _cache = (key, Clock() + (ok ? CapabilitiesTtl : FailureTtl), features);
        }

        return features;
    }

    /// <summary>Sends a signed request to the companion.</summary>
    /// <param name="endpoint">The companion.</param>
    /// <param name="method">GET or POST.</param>
    /// <param name="relativePathAndQuery">Path and query without a leading slash, already escaped.</param>
    /// <param name="body">Raw JSON body, if any.</param>
    /// <param name="viewer">The viewer, or null for plugin-level calls.</param>
    /// <param name="admin">Whether the viewer is an administrator.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    public virtual async Task<CompanionResponse> SendAsync(ServiceEndpoint endpoint, HttpMethod method, string relativePathAndQuery, byte[]? body, Guid? viewer, bool admin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(method);
        var uri = endpoint.Resolve(relativePathAndQuery);
        var timestamp = Clock().ToUnixTimeSeconds();
        var nonce = NewNonce();
        var userId = viewer?.ToString("N", CultureInfo.InvariantCulture) ?? string.Empty;
        var isAdmin = viewer is not null && admin;
        var payload = body ?? Array.Empty<byte>();

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Zorofin-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Zorofin-Nonce", nonce);
        request.Headers.Add("X-Zorofin-User", userId);
        request.Headers.Add("X-Zorofin-User-Admin", isAdmin ? "1" : "0");
        request.Headers.Add("X-Zorofin-Signature", CompanionSigner.Sign(endpoint.ApiKey, method.Method, uri.PathAndQuery, timestamp, nonce, userId, isAdmin, payload));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            using var response = await TransmitAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                return new CompanionResponse(502, null);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? parsed = null;
            if (bytes.Length is > 0 and <= MaxResponseBytes)
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

            return new CompanionResponse((int)response.StatusCode, parsed);
        }
        catch (HttpRequestException)
        {
            return new CompanionResponse(0, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CompanionResponse(0, null);
        }
    }

    /// <summary>
    /// Fetches one file from a module's public route (GET /v1/public/{module}/...). These routes are unsigned and
    /// carry no viewer, so they are used only for files that are the same for everyone, such as web UI assets.
    /// </summary>
    /// <param name="endpoint">The companion.</param>
    /// <param name="relativePath">Path without a leading slash, already escaped.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Status (0 when unreachable), body and content type.</returns>
    public virtual async Task<(int Status, byte[]? Body, string? ContentType)> GetPublicAsync(ServiceEndpoint endpoint, string relativePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Resolve(relativePath));
        try
        {
            using var response = await TransmitAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                return (502, null, null);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length > MaxResponseBytes
                ? (502, null, null)
                : ((int)response.StatusCode, bytes, response.Content.Headers.ContentType?.MediaType);
        }
        catch (HttpRequestException)
        {
            return (0, null, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (0, null, null);
        }
    }

    /// <summary>Reads the features object of a capabilities response, ignoring anything malformed.</summary>
    /// <param name="body">Response body.</param>
    /// <returns>Features keyed by module id.</returns>
    internal static IReadOnlyDictionary<string, CapabilityFeature> ParseFeatures(JsonNode? body)
    {
        var features = new Dictionary<string, CapabilityFeature>(StringComparer.Ordinal);
        if (body is not JsonObject root || root["schemaVersion"] is not JsonValue schema
            || !schema.TryGetValue<int>(out var schemaVersion) || schemaVersion != 1
            || root["features"] is not JsonObject list)
        {
            return features;
        }

        foreach (var (name, value) in list)
        {
            if (features.Count >= MaxFeatures)
            {
                break;
            }

            if (!IsModuleId(name) || value is not JsonObject feature
                || feature["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number <= 0)
            {
                continue;
            }

            var enabled = feature["enabled"] is not JsonValue flag || !flag.TryGetValue<bool>(out var on) || on;
            features[name] = new CapabilityFeature(number, enabled);
        }

        return features;
    }

    /// <summary>Puts the request on the wire. Tests override it.</summary>
    /// <param name="request">Signed request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    protected virtual Task<HttpResponseMessage> TransmitAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleIdPattern();
}
