using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Zorofin.Notifications;

/// <summary>
/// Read notifications for the signed-in account (capability "notificationReads" version 1), so a notice read
/// on the web, a phone, a TV or a Roku is no longer new on the account's other devices.
/// </summary>
[ApiController]
[Authorize]
[Route("Zorofin/Notifications/Read")]
[Produces(MediaTypeNames.Application.Json)]
public class NotificationReadsController : ControllerBase
{
    private readonly NotificationReads _reads;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationReadsController"/> class.
    /// </summary>
    /// <param name="reads">Read-state store.</param>
    /// <param name="authorization">Jellyfin authorization context.</param>
    public NotificationReadsController(NotificationReads reads, IAuthorizationContext authorization)
    {
        _reads = reads;
        _authorization = authorization;
    }

    /// <summary>This account's read notifications.</summary>
    /// <returns>{"initialized":bool,"requests":[id],"replies":["ticketId|updatedAt"]}.</returns>
    [HttpGet]
    public async Task<ActionResult<NotificationReadState>> Get()
    {
        Response.Headers.CacheControl = "private, no-store";
        return await ViewerAsync().ConfigureAwait(false) is { } viewer ? _reads.Get(viewer) : Unauthorized();
    }

    /// <summary>Marks notifications read for this account on all its devices.</summary>
    /// <param name="body">What was read.</param>
    /// <returns>The account's read notifications after the change.</returns>
    [HttpPost]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<NotificationReadState>> Add([FromBody] ReadBody? body)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (await ViewerAsync().ConfigureAwait(false) is not { } viewer)
        {
            return Unauthorized();
        }

        var requests = body?.Requests ?? new List<int>();
        var replies = body?.Replies ?? new List<ReplyRef>();
        if (requests.Count > NotificationReads.MaxPerCall || replies.Count > NotificationReads.MaxPerCall
            || requests.Any(id => id <= 0) || replies.Any(r => NotificationReads.ReplyKey(r?.Id, r?.UpdatedAt) is null))
        {
            return BadRequest();
        }

        return _reads.Add(viewer, requests, replies.Select(r => NotificationReads.ReplyKey(r.Id, r.UpdatedAt)!), body?.Initialize == true);
    }

    private async Task<Guid?> ViewerAsync()
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return info is { IsAuthenticated: true, IsApiKey: false, User: not null } ? info.UserId : null;
    }

    /// <summary>What a client read.</summary>
    public sealed class ReadBody
    {
        /// <summary>Gets or sets request ids whose "available" notice was read.</summary>
        [JsonPropertyName("requests")]
        public List<int>? Requests { get; set; }

        /// <summary>Gets or sets support replies read, each with the updatedAt the inbox sent.</summary>
        [JsonPropertyName("replies")]
        public List<ReplyRef>? Replies { get; set; }

        /// <summary>Gets or sets a value indicating whether this call records the account's existing history.</summary>
        [JsonPropertyName("initialize")]
        public bool Initialize { get; set; }
    }

    /// <summary>One support reply.</summary>
    public sealed class ReplyRef
    {
        /// <summary>Gets or sets the ticket id.</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>Gets or sets the reply's updatedAt, exactly as the inbox sent it.</summary>
        [JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; set; }
    }
}
