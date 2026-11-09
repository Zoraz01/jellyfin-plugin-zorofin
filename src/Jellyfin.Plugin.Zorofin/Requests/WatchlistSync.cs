using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Zorofin.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Zorofin.Requests;

/// <summary>
/// Keeps each viewer's Seerr watchlist and their Jellyfin watchlist (titles they "like", the same flag Jellyfin
/// Enhanced used) in step, every 30 minutes. Each direction has its own switch. With both on, removing a title on one
/// side removes it on the other; with one on, titles are only added.
/// </summary>
public sealed class WatchlistSync : IHostedService, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly UpstreamClient _upstream;
    private readonly SeerrUserDirectory _seerrUsers;
    private readonly string _statePath;
    private readonly ILogger<WatchlistSync> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private Timer? _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistSync"/> class.
    /// </summary>
    /// <param name="users">User manager.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="userData">User data manager.</param>
    /// <param name="upstream">Upstream client.</param>
    /// <param name="seerrUsers">Jellyfin to Seerr user map.</param>
    /// <param name="statePath">File remembering what was in step last time.</param>
    /// <param name="logger">Logger.</param>
    public WatchlistSync(IUserManager users, ILibraryManager library, IUserDataManager userData, UpstreamClient upstream, SeerrUserDirectory seerrUsers, string statePath, ILogger<WatchlistSync> logger)
    {
        _users = users;
        _library = library;
        _userData = userData;
        _upstream = upstream;
        _seerrUsers = seerrUsers;
        _statePath = statePath;
        _logger = logger;
    }

    /// <summary>
    /// Works out the changes for one viewer. Keys look like "movie:603" or "tv:1399".
    /// </summary>
    /// <param name="seerr">On the Seerr watchlist now.</param>
    /// <param name="jellyfin">Liked in Jellyfin now (only titles in the library).</param>
    /// <param name="inLibrary">Keys of Seerr titles this server has.</param>
    /// <param name="previous">In step on both sides after the last run.</param>
    /// <param name="fromSeerr">Copy Seerr to Jellyfin.</param>
    /// <param name="toSeerr">Copy Jellyfin to Seerr.</param>
    /// <returns>What to like, unlike, add and remove, and the new in-step set.</returns>
    public static WatchlistPlan Plan(IReadOnlySet<string> seerr, IReadOnlySet<string> jellyfin, IReadOnlySet<string> inLibrary, IReadOnlySet<string> previous, bool fromSeerr, bool toSeerr)
    {
        ArgumentNullException.ThrowIfNull(seerr);
        ArgumentNullException.ThrowIfNull(jellyfin);
        ArgumentNullException.ThrowIfNull(inLibrary);
        ArgumentNullException.ThrowIfNull(previous);
        var both = fromSeerr && toSeerr;
        // A title in step last time and now gone from one side was removed there on purpose.
        var unlike = both ? previous.Where(k => jellyfin.Contains(k) && !seerr.Contains(k)).ToHashSet() : new HashSet<string>();
        // Only for titles still on the server: a title deleted from the library stays on the Seerr watchlist.
        var remove = both ? previous.Where(k => seerr.Contains(k) && !jellyfin.Contains(k) && inLibrary.Contains(k)).ToHashSet() : new HashSet<string>();
        var like = fromSeerr ? seerr.Where(k => !jellyfin.Contains(k) && inLibrary.Contains(k) && !remove.Contains(k)).ToHashSet() : new HashSet<string>();
        var add = toSeerr ? jellyfin.Where(k => !seerr.Contains(k) && !unlike.Contains(k)).ToHashSet() : new HashSet<string>();
        var seerrAfter = seerr.Except(remove).Concat(add).ToHashSet();
        var jellyfinAfter = jellyfin.Except(unlike).Concat(like).ToHashSet();
        var inStep = seerrAfter.Where(jellyfinAfter.Contains).ToHashSet();
        return new WatchlistPlan(like, unlike, add, remove, inStep);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => _ = RunAsync(CancellationToken.None), null, TimeSpan.FromMinutes(2), Interval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _running.Dispose();
    }

    /// <summary>Runs one sync for every viewer linked to Seerr.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !(config.WatchlistFromSeerr || config.WatchlistToSeerr) || SeerrAutomations.Seerr(config) is not { } seerr)
        {
            return;
        }

        if (!await _running.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var state = Load();
            foreach (var user in _users.GetUsers().ToList())
            {
                if (user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled))
                {
                    continue;
                }

                try
                {
                    var key = user.Id.ToString("N", CultureInfo.InvariantCulture);
                    var previous = state.TryGetValue(key, out var p) ? p : new HashSet<string>();
                    if (await SyncUserAsync(config, seerr, user, previous, cancellationToken).ConfigureAwait(false) is { } inStep)
                    {
                        state[key] = inStep;
                    }
                }
                catch (UpstreamUnavailableException)
                {
                    _logger.LogInformation("Watchlist sync: Seerr is not reachable; trying again later");
                    break;
                }
            }

            Save(state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Watchlist sync failed");
        }
        finally
        {
            _running.Release();
        }
    }

    private static string Key(string mediaType, string tmdbId) => (mediaType == "tv" ? "tv:" : "movie:") + tmdbId;

    private async Task<HashSet<string>?> SyncUserAsync(PluginConfiguration config, ServiceEndpoint seerr, User user, HashSet<string> previous, CancellationToken cancellationToken)
    {
        if (await _seerrUsers.FindAsync(seerr, user.Id, cancellationToken).ConfigureAwait(false) is not { } seerrUser)
        {
            return null;
        }

        // Seerr side.
        var seerrList = new Dictionary<string, string>(StringComparer.Ordinal); // key -> title
        for (var page = 1; page <= 10; page++)
        {
            var result = await _upstream.SendAsync(seerr, HttpMethod.Get, "api/v1/user/" + seerrUser.ToString(CultureInfo.InvariantCulture) + "/watchlist?page=" + page.ToString(CultureInfo.InvariantCulture), null, seerrUser, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return null;
            }

            foreach (var entry in result.Body?["results"] as JsonArray ?? new JsonArray())
            {
                var tmdb = RequestShaper.Int(entry?["tmdbId"]);
                var type = RequestShaper.Str(entry?["mediaType"]);
                if (tmdb is not null && type is "movie" or "tv")
                {
                    seerrList[Key(type, tmdb.Value.ToString(CultureInfo.InvariantCulture))] = RequestShaper.Str(entry?["title"]) ?? string.Empty;
                }
            }

            if (page >= (RequestShaper.Int(result.Body?["totalPages"]) ?? 1))
            {
                break;
            }
        }

        // Jellyfin side: movies and shows this viewer can see, keyed by TMDB id.
        var titles = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
        foreach (var item in _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false) { EnableImages = false },
        }))
        {
            if (item.ProviderIds is not null && item.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdb) && !string.IsNullOrEmpty(tmdb))
            {
                titles.TryAdd(Key(item.GetBaseItemKind() == BaseItemKind.Series ? "tv" : "movie", tmdb), item);
            }
        }

        var liked = titles.Where(t => _userData.GetUserData(user, t.Value)?.Likes == true).Select(t => t.Key).ToHashSet();
        var plan = Plan(seerrList.Keys.ToHashSet(), liked, titles.Keys.Where(seerrList.ContainsKey).ToHashSet(), previous, config.WatchlistFromSeerr, config.WatchlistToSeerr);

        foreach (var key in plan.Like.Concat(plan.Unlike))
        {
            var item = titles[key];
            var data = _userData.GetUserData(user, item);
            if (data is null)
            {
                continue;
            }

            data.Likes = plan.Like.Contains(key) ? true : null;
            _userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, cancellationToken);
        }

        foreach (var key in plan.Add)
        {
            var item = titles[key];
            var parts = key.Split(':');
            var body = new JsonObject { ["tmdbId"] = int.Parse(parts[1], CultureInfo.InvariantCulture), ["mediaType"] = parts[0], ["title"] = item.Name };
            await _upstream.SendAsync(seerr, HttpMethod.Post, "api/v1/watchlist", body, seerrUser, cancellationToken).ConfigureAwait(false);
        }

        foreach (var key in plan.Remove)
        {
            await _upstream.SendAsync(seerr, HttpMethod.Delete, "api/v1/watchlist/" + key.Split(':')[1], null, seerrUser, cancellationToken).ConfigureAwait(false);
        }

        if (plan.Like.Count + plan.Unlike.Count + plan.Add.Count + plan.Remove.Count > 0)
        {
            _logger.LogInformation(
                "Watchlist sync for {User}: {Like} liked, {Unlike} unliked, {Add} added to Seerr, {Remove} removed from Seerr",
                user.Username,
                plan.Like.Count,
                plan.Unlike.Count,
                plan.Add.Count,
                plan.Remove.Count);
        }

        return plan.InStep;
    }

    private Dictionary<string, HashSet<string>> Load()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(File.ReadAllText(_statePath)) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Watchlist sync state unreadable; starting fresh");
        }

        return new();
    }

    private void Save(Dictionary<string, HashSet<string>> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temp = _statePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, _statePath, true);
    }
}

/// <summary>Changes for one viewer's watchlists.</summary>
/// <param name="Like">Mark liked in Jellyfin.</param>
/// <param name="Unlike">Clear liked in Jellyfin.</param>
/// <param name="Add">Add to the Seerr watchlist.</param>
/// <param name="Remove">Remove from the Seerr watchlist.</param>
/// <param name="InStep">On both sides afterwards.</param>
public sealed record WatchlistPlan(HashSet<string> Like, HashSet<string> Unlike, HashSet<string> Add, HashSet<string> Remove, HashSet<string> InStep);
