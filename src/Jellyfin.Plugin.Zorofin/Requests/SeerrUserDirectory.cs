using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Zorofin.Requests;

/// <summary>
/// Finds the Seerr account linked to a Jellyfin user. Seerr imports Jellyfin users with their Jellyfin id,
/// so the link is the operator's existing Seerr setup; nothing here creates accounts.
/// </summary>
public class SeerrUserDirectory
{
    private const int PageSize = 100;
    private const int MaxPages = 50;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly UpstreamClient _upstream;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Dictionary<Guid, int> _map = new();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private string _loadedFor = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrUserDirectory"/> class.
    /// </summary>
    /// <param name="upstream">Upstream client.</param>
    public SeerrUserDirectory(UpstreamClient upstream)
    {
        _upstream = upstream;
    }

    /// <summary>Looks up the Seerr user id for a Jellyfin user.</summary>
    /// <param name="seerr">Seerr endpoint.</param>
    /// <param name="jellyfinUserId">Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Seerr id, null if not linked, or throws <see cref="UpstreamUnavailableException"/>.</returns>
    public async Task<int?> FindAsync(ServiceEndpoint seerr, Guid jellyfinUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seerr);
        var key = seerr.BaseUri.AbsoluteUri;
        if (_loadedFor != key || DateTimeOffset.UtcNow - _loadedAt > Lifetime || !_map.ContainsKey(jellyfinUserId))
        {
            await RefreshAsync(seerr, key, force: _map.Count > 0 && !_map.ContainsKey(jellyfinUserId), cancellationToken).ConfigureAwait(false);
        }

        return _map.TryGetValue(jellyfinUserId, out var id) ? id : null;
    }

    /// <summary>Parses one page of Seerr's user list into the map.</summary>
    /// <param name="page">Seerr user list JSON.</param>
    /// <param name="map">Map to fill.</param>
    /// <returns>The number of pages Seerr reports.</returns>
    internal static int ReadPage(JsonNode? page, Dictionary<Guid, int> map)
    {
        foreach (var user in page?["results"] as JsonArray ?? new JsonArray())
        {
            if (RequestShaper.Int(user?["id"]) is { } id
                && Guid.TryParse(RequestShaper.Str(user?["jellyfinUserId"]), out var jellyfinId)
                && jellyfinId != Guid.Empty)
            {
                map[jellyfinId] = id;
            }
        }

        return RequestShaper.Int(page?["pageInfo"]?["pages"]) ?? 1;
    }

    private async Task RefreshAsync(ServiceEndpoint seerr, string key, bool force, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A just-linked account may be missing from a fresh map; allow one reload a minute for misses.
            var age = DateTimeOffset.UtcNow - _loadedAt;
            if (_loadedFor == key && (force ? age < TimeSpan.FromMinutes(1) : age <= Lifetime))
            {
                return;
            }

            var map = new Dictionary<Guid, int>();
            var pages = 1;
            for (var page = 0; page < pages && page < MaxPages; page++)
            {
                var path = string.Create(CultureInfo.InvariantCulture, $"api/v1/user?take={PageSize}&skip={page * PageSize}");
                var result = await _upstream.SendAsync(seerr, HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false);
                if (!result.Ok)
                {
                    throw new UpstreamUnavailableException(result.Status);
                }

                pages = ReadPage(result.Body, map);
            }

            _map = map;
            _loadedAt = DateTimeOffset.UtcNow;
            _loadedFor = key;
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
