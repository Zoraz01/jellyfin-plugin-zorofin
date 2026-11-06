using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Stats;

/// <summary>
/// The admin play history: charts, the activity log, CSV export, a feed for Rewind and the Popular row,
/// and an import of Playback Reporting's history. Administrators and API keys only.
/// </summary>
[ApiController]
[Authorize]
[Route("Zorofin/Admin/Activity")]
[Produces(MediaTypeNames.Application.Json)]
public class ActivityController : ControllerBase
{
    /// <summary>Longest window a report may cover.</summary>
    public const int MaxDays = 3660;

    private readonly PlayHistoryStore _history;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivityController"/> class.
    /// </summary>
    /// <param name="history">Play history store.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="users">User manager.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    public ActivityController(PlayHistoryStore history, ILibraryManager library, IUserManager users, IAuthorizationContext authorization)
    {
        _history = history;
        _library = library;
        _users = users;
        _authorization = authorization;
    }

    /// <summary>Whether history is on, how much is stored, and the retention setting.</summary>
    /// <returns>Status.</returns>
    [HttpGet("Status")]
    public async Task<ActionResult> Status()
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var config = Plugin.Instance?.Configuration;
        var all = _history.Between(DateTime.MinValue, DateTime.MaxValue);
        return Json(new JsonObject
        {
            ["enabled"] = config?.PlayHistoryEnabled == true,
            ["retentionDays"] = config?.PlayHistoryRetentionDays ?? 0,
            ["plays"] = all.Count,
            ["first"] = all.Count > 0 ? all[0].StartedUtc.ToString("o", CultureInfo.InvariantCulture) : null,
            ["imported"] = all.Count(p => p.ImportKey is not null),
        });
    }

    /// <summary>Totals and breakdowns for the charts.</summary>
    /// <param name="days">Window ending now, used when <paramref name="from"/> is not given.</param>
    /// <param name="from">Window start (ISO 8601).</param>
    /// <param name="to">Window end (ISO 8601).</param>
    /// <param name="offset">Viewer's offset from UTC in minutes, for days and hours.</param>
    /// <param name="userId">Only this user.</param>
    /// <param name="type">Only this item kind.</param>
    /// <returns>The summary.</returns>
    [HttpGet("Summary")]
    public async Task<ActionResult> Summary([FromQuery] int days = 30, [FromQuery] string? from = null, [FromQuery] string? to = null, [FromQuery] int offset = 0, [FromQuery] string? userId = null, [FromQuery] string? type = null)
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (Window(days, from, to) is not { } window || offset is < -900 or > 900 || Filter(userId, type, null) is not { } filter)
        {
            return BadRequest();
        }

        return Json(ActivityReports.Summary(_history.Between(window.From, window.To), window.From, window.To, offset, filter));
    }

    /// <summary>The activity log, newest first.</summary>
    /// <param name="days">Window ending now, used when <paramref name="from"/> is not given.</param>
    /// <param name="from">Window start.</param>
    /// <param name="to">Window end.</param>
    /// <param name="userId">Only this user.</param>
    /// <param name="type">Only this item kind.</param>
    /// <param name="q">Text search over title, user, client and device.</param>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Rows to return, up to 500.</param>
    /// <returns>A page of plays.</returns>
    [HttpGet("Log")]
    public async Task<ActionResult> Log([FromQuery] int days = 30, [FromQuery] string? from = null, [FromQuery] string? to = null, [FromQuery] string? userId = null, [FromQuery] string? type = null, [FromQuery] string? q = null, [FromQuery] int skip = 0, [FromQuery] int take = 100)
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (Window(days, from, to) is not { } window || skip < 0 || take is < 1 or > 500 || Filter(userId, type, q) is not { } filter)
        {
            return BadRequest();
        }

        return Json(ActivityReports.Log(_history.Between(window.From, window.To), filter, skip, take));
    }

    /// <summary>The activity log as a CSV download.</summary>
    /// <param name="days">Window ending now.</param>
    /// <param name="from">Window start.</param>
    /// <param name="to">Window end.</param>
    /// <param name="userId">Only this user.</param>
    /// <param name="type">Only this item kind.</param>
    /// <param name="q">Text search.</param>
    /// <returns>CSV file.</returns>
    [HttpGet("Export")]
    [Produces("text/csv")]
    public async Task<ActionResult> Export([FromQuery] int days = 30, [FromQuery] string? from = null, [FromQuery] string? to = null, [FromQuery] string? userId = null, [FromQuery] string? type = null, [FromQuery] string? q = null)
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (Window(days, from, to) is not { } window || Filter(userId, type, q) is not { } filter)
        {
            return BadRequest();
        }

        var csv = ActivityReports.Csv(_history.Between(window.From, window.To), filter);
        var name = "zorofin-activity-" + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", name);
    }

    /// <summary>
    /// Plays since a time, in Playback Reporting's column names. This is the data source for Rewind
    /// (support-agent/recap-build.py) once Playback Reporting is removed.
    /// </summary>
    /// <param name="since">Only plays that started at or after this time (ISO 8601).</param>
    /// <param name="until">Only plays that started before this time.</param>
    /// <returns>Rows, oldest first.</returns>
    [HttpGet("Rows")]
    public async Task<ActionResult> Rows([FromQuery] string? since = null, [FromQuery] string? until = null)
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var start = DateTime.MinValue;
        var end = DateTime.MaxValue;
        if ((since is not null && !TryUtc(since, out start)) || (until is not null && !TryUtc(until, out end)))
        {
            return BadRequest();
        }

        return Json(new JsonArray(_history.Between(start, end).Select(p => (JsonNode)ActivityReports.LegacyRow(p)).ToArray()));
    }

    /// <summary>
    /// Watch time per played item (episodes separately) over the last days, as the Home rows script reads it:
    /// <c>[{"id":"…","seconds":5400}]</c>. Plays shorter than 2 minutes or longer than 6 hours are left out.
    /// </summary>
    /// <param name="days">Window, 1 to 35 days.</param>
    /// <returns>Items, most watched first, up to 1000.</returns>
    [HttpGet("Popularity")]
    public async Task<ActionResult> Popularity([FromQuery] int days = 7)
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (days is < 1 or > 35)
        {
            return BadRequest();
        }

        var now = DateTime.UtcNow;
        var rows = _history.Between(now.AddDays(-days), now)
            .Where(p => p.ItemType is "Movie" or "Episode" && p.Seconds >= PlayStatsStore.MinimumPlay.TotalSeconds && p.Seconds <= PlayStatsStore.MaximumPlay.TotalSeconds)
            .GroupBy(p => p.ItemId)
            .Select(g => (Id: g.Key, Seconds: g.Sum(p => (long)p.Seconds)))
            .OrderByDescending(g => g.Seconds)
            .Take(1000)
            .Select(g => (JsonNode)new JsonObject { ["id"] = g.Id.ToString("N"), ["seconds"] = g.Seconds });
        return Json(new JsonArray(rows.ToArray()));
    }

    /// <summary>
    /// Adds history exported from Playback Reporting (tools/import-playback-reporting.py sends it in batches).
    /// Rows already imported are skipped, so running the import again is safe.
    /// </summary>
    /// <param name="body">Rows and the time zone their times are in.</param>
    /// <returns>How many were added and skipped.</returns>
    [HttpPost("Import")]
    [Consumes(MediaTypeNames.Application.Json)]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<ActionResult> Import([FromBody] ImportBody? body)
    {
        if (await DenyAsync().ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (body?.Rows is not { } rows || rows.Count > PlayHistoryStore.MaxImportBatch)
        {
            return BadRequest();
        }

        var zone = TimeZoneInfo.Local;
        if (!string.IsNullOrEmpty(body.TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(body.TimeZone, out zone))
        {
            return BadRequest(new JsonObject { ["error"] = "Unknown time zone " + body.TimeZone });
        }

        var names = new Dictionary<Guid, string>();
        var plays = new List<PlayEvent>(rows.Count);
        var invalid = 0;
        foreach (var row in rows)
        {
            if (ToPlay(row, zone!, names) is { } play)
            {
                plays.Add(play);
            }
            else
            {
                invalid++;
            }
        }

        var (added, skipped, overlap) = _history.Import(plays);
        return Json(new JsonObject { ["added"] = added, ["skipped"] = skipped, ["overlap"] = overlap, ["invalid"] = invalid });
    }

    /// <summary>Deletes play history: everyone's, or one user's.</summary>
    /// <param name="userId">Only this user's plays.</param>
    /// <returns>How many plays were deleted.</returns>
    [HttpDelete]
    public async Task<ActionResult> Delete([FromQuery] string? userId = null)
    {
        if (await DenyAsync(allowApiKey: false).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        Guid? user = null;
        if (userId is not null)
        {
            if (!Guid.TryParse(userId, out var parsed))
            {
                return BadRequest();
            }

            user = parsed;
        }

        return Json(new JsonObject { ["deleted"] = _history.Delete(user) });
    }

    /// <summary>Turns one Playback Reporting row into a play, or null when it is unusable.</summary>
    /// <param name="row">The row.</param>
    /// <param name="zone">Time zone of DateCreated when it carries no offset.</param>
    /// <param name="names">User name cache.</param>
    /// <returns>The play.</returns>
    internal PlayEvent? ToPlay(ImportRow row, TimeZoneInfo zone, Dictionary<Guid, string> names)
    {
        if (!Guid.TryParse(row.UserId, out var userId) || !Guid.TryParse(row.ItemId, out var itemId) || row.PlayDuration <= 0
            || string.IsNullOrEmpty(row.DateCreated) || !TryLocal(row.DateCreated, zone, out var started))
        {
            return null;
        }

        if (!names.TryGetValue(userId, out var userName))
        {
            names[userId] = userName = _users.GetUserById(userId)?.Username ?? string.Empty;
        }

        var item = _library.GetItemById(itemId);
        var episode = item as Episode;
        var (method, transcode) = Method(row.PlaybackMethod);
        return new PlayEvent
        {
            StartedUtc = started,
            Seconds = (int)Math.Min(row.PlayDuration, int.MaxValue),
            UserId = userId,
            UserName = userName,
            ItemId = itemId,
            ItemType = item?.GetBaseItemKind().ToString() ?? row.ItemType ?? string.Empty,
            ItemName = item?.Name ?? row.ItemName ?? string.Empty,
            SeriesId = episode?.SeriesId,
            SeriesName = episode?.SeriesName,
            Season = episode?.ParentIndexNumber,
            Episode = episode?.IndexNumber,
            Client = row.ClientName ?? string.Empty,
            Device = row.DeviceName ?? string.Empty,
            PlayMethod = method,
            Transcode = transcode,
            ImportKey = ActivityReports.ImportPrefix + row.RowId.ToString(CultureInfo.InvariantCulture) + "|" + row.DateCreated,
        };
    }

    /// <summary>Splits Playback Reporting's method text, such as "Transcode (v:h264 a:direct)".</summary>
    /// <param name="text">Method text.</param>
    /// <returns>Method and what was transcoded.</returns>
    internal static (string Method, string? Transcode) Method(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, null);
        }

        if (!text.StartsWith("Transcode", StringComparison.OrdinalIgnoreCase))
        {
            return (text.Trim(), null);
        }

        var parts = new List<string>(2);
        if (text.Contains("v:", StringComparison.Ordinal) && !text.Contains("v:direct", StringComparison.Ordinal))
        {
            parts.Add("video");
        }

        if (text.Contains("a:", StringComparison.Ordinal) && !text.Contains("a:direct", StringComparison.Ordinal))
        {
            parts.Add("audio");
        }

        return ("Transcode", parts.Count == 0 ? null : string.Join(", ", parts));
    }

    /// <summary>Parses a time with an offset as given, and one without in <paramref name="zone"/>.</summary>
    /// <param name="text">Time text.</param>
    /// <param name="zone">Zone for times without an offset.</param>
    /// <param name="utc">The time in UTC.</param>
    /// <returns>Whether it parsed.</returns>
    internal static bool TryLocal(string text, TimeZoneInfo zone, out DateTime utc)
    {
        utc = default;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset)
            && (text.EndsWith('Z') || text.Contains('+', StringComparison.Ordinal) || text.LastIndexOf('-') > 9))
        {
            utc = withOffset.UtcDateTime;
            return true;
        }

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return false;
        }

        try
        {
            utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
        }
        catch (ArgumentException)
        {
            // A wall-clock time skipped by a daylight-saving change; an hour later is what it meant.
            utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.AddHours(1), DateTimeKind.Unspecified), zone);
        }

        return true;
    }

    private static bool TryUtc(string text, out DateTime utc)
    {
        utc = default;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
        {
            return false;
        }

        utc = value.UtcDateTime;
        return true;
    }

    private static (DateTime From, DateTime To)? Window(int days, string? from, string? to)
    {
        var end = DateTime.UtcNow;
        if (to is not null && !TryUtc(to, out end))
        {
            return null;
        }

        DateTime start;
        if (from is not null)
        {
            if (!TryUtc(from, out start))
            {
                return null;
            }
        }
        else if (days is >= 1 and <= MaxDays)
        {
            start = end.AddDays(-days);
        }
        else
        {
            return null;
        }

        return start < end && (end - start).TotalDays <= MaxDays ? (start, end) : null;
    }

    private static ActivityFilter? Filter(string? userId, string? type, string? q)
    {
        Guid? user = null;
        if (!string.IsNullOrEmpty(userId))
        {
            if (!Guid.TryParse(userId, out var parsed))
            {
                return null;
            }

            user = parsed;
        }

        if (type is { Length: > 40 } || q is { Length: > 200 })
        {
            return null;
        }

        return new ActivityFilter(user, string.IsNullOrEmpty(type) ? null : type, q);
    }

    private ContentResult Json(JsonNode body) => Content(body.ToJsonString(), MediaTypeNames.Application.Json);

    private async Task<ActionResult?> DenyAsync(bool allowApiKey = true)
    {
        Response.Headers.CacheControl = "private, no-store";
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (info is not { IsAuthenticated: true })
        {
            return Unauthorized();
        }

        if (info.IsApiKey)
        {
            return allowApiKey ? null : Forbid();
        }

        return info.User is { } user && user.HasPermission(PermissionKind.IsAdministrator) ? null : Forbid();
    }

    /// <summary>Import request.</summary>
    public sealed class ImportBody
    {
        /// <summary>Gets or sets the IANA time zone of the rows' DateCreated, such as America/New_York. Defaults to the server's.</summary>
        [JsonPropertyName("timeZone")]
        public string? TimeZone { get; set; }

        /// <summary>Gets or sets the rows.</summary>
        [JsonPropertyName("rows")]
        public List<ImportRow>? Rows { get; set; }
    }

    /// <summary>One Playback Reporting PlaybackActivity row.</summary>
    public sealed class ImportRow
    {
        /// <summary>Gets or sets the SQLite rowid.</summary>
        public long RowId { get; set; }

        /// <summary>Gets or sets when playback started, in the server's local time.</summary>
        public string? DateCreated { get; set; }

        /// <summary>Gets or sets the user id.</summary>
        public string? UserId { get; set; }

        /// <summary>Gets or sets the item id.</summary>
        public string? ItemId { get; set; }

        /// <summary>Gets or sets the item type.</summary>
        public string? ItemType { get; set; }

        /// <summary>Gets or sets the item name.</summary>
        public string? ItemName { get; set; }

        /// <summary>Gets or sets the playback method text.</summary>
        public string? PlaybackMethod { get; set; }

        /// <summary>Gets or sets the client name.</summary>
        public string? ClientName { get; set; }

        /// <summary>Gets or sets the device name.</summary>
        public string? DeviceName { get; set; }

        /// <summary>Gets or sets the seconds played.</summary>
        public long PlayDuration { get; set; }
    }
}
