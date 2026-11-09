using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Zorofin.Configuration;

/// <summary>
/// Operator settings. Credentials stay on the server and are never returned to viewers.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the Requests module is switched on.</summary>
    public bool RequestsEnabled { get; set; }

    /// <summary>Gets or sets the Seerr base URL as seen from the Jellyfin server.</summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Seerr API key.</summary>
    public string SeerrApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional Radarr base URL used for download progress.</summary>
    public string RadarrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional Radarr API key.</summary>
    public string RadarrApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional Sonarr base URL used for download progress.</summary>
    public string SonarrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional Sonarr API key.</summary>
    public string SonarrApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether anonymous play statistics are recorded and served.</summary>
    public bool PlayStatsEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the next season is requested in Seerr, as the viewer, when they near
    /// the end of the newest season on the server.
    /// </summary>
    public bool AutoSeasonRequestsEnabled { get; set; }

    /// <summary>Gets or sets how many episodes may remain in the season when the next one is requested.</summary>
    public int AutoSeasonEpisodesLeft { get; set; } = 2;

    /// <summary>Gets or sets a value indicating whether Seerr's recently-added scan runs when movies or episodes arrive.</summary>
    public bool SeerrScanOnLibraryAdd { get; set; }

    /// <summary>Gets or sets how long to wait after the last arrival before starting Seerr's scan, in seconds.</summary>
    public int SeerrScanDelaySeconds { get; set; } = 120;

    /// <summary>
    /// Gets or sets a value indicating whether the admin play history (who watched what, when, on which device)
    /// is recorded. Separate from <see cref="PlayStatsEnabled"/>, which stores anonymous totals only.
    /// </summary>
    public bool PlayHistoryEnabled { get; set; }

    /// <summary>Gets or sets how many days of play history to keep. 0 keeps everything.</summary>
    public int PlayHistoryRetentionDays { get; set; } = 365;

    /// <summary>Gets or sets the optional Zorofin companion base URL as seen from the Jellyfin server.</summary>
    public string CompanionUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the secret shared with the companion, used to sign bridge requests.</summary>
    public string CompanionSecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin's web client loads the Zorofin look and web features
    /// from the companion's webUi module. Off until an administrator turns it on.
    /// </summary>
    public bool WebUiEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin builds Zorofin's Home rows (for the web client and,
    /// later, the apps), so servers don't need the Home Screen Sections plugin.
    /// </summary>
    public bool HomeRowsEnabled { get; set; }

    /// <summary>Gets or sets the Home row ids the operator switched off, comma separated.</summary>
    public string HomeRowsOff { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional dated seasonal calendar, the curated-calendar.json format:
    /// {"schemaVersion":1,"events":[{"theme":"eid","start":"2027-03-09","end":"2027-03-12"}]}.
    /// </summary>
    public string HomeCuratedCalendar { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the older app features this server still serves itself, comma separated (for example
    /// "support,requests,homeRows"). Each is listed at version 1 in the capability manifest, ahead of the plugin's
    /// own version, so Zorofin apps that predate the plugin keep working. Only for servers that already run those
    /// routes, such as a server moving from its own support service to the plugin. Empty on a new server.
    /// </summary>
    public string LegacyAppFeatures { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the announcement banner is shown.</summary>
    public bool AnnouncementEnabled { get; set; }

    /// <summary>Gets or sets the announcement kind: info, success, warning or maintenance.</summary>
    public string AnnouncementType { get; set; } = "info";

    /// <summary>Gets or sets the announcement title, up to 80 characters.</summary>
    public string AnnouncementTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the announcement message, up to 320 characters.</summary>
    public string AnnouncementMessage { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether viewers may dismiss the announcement.</summary>
    public bool AnnouncementDismissible { get; set; } = true;

    /// <summary>Gets or sets when the announcement starts showing (ISO 8601), or empty for now.</summary>
    public string AnnouncementStartAt { get; set; } = string.Empty;

    /// <summary>Gets or sets when the announcement stops showing (ISO 8601), or empty for no end.</summary>
    public string AnnouncementEndAt { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional button label.</summary>
    public string AnnouncementActionLabel { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional button link: a path on this server or an https URL.</summary>
    public string AnnouncementActionHref { get; set; } = string.Empty;

    /// <summary>Gets or sets a number the setup page raises to show an unchanged announcement again to viewers who dismissed it.</summary>
    public int AnnouncementRevision { get; set; }

    /// <summary>Gets or sets the Home banner's hand-picked titles: Jellyfin item ids, comma separated, in order. Empty picks at random.</summary>
    public string HomeBannerItemIds { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the Home banner plays each title's trailer, muted, behind the slide.</summary>
    public bool HomeBannerTrailers { get; set; }

    /// <summary>Gets or sets a value indicating whether trailer intros and sponsor segments are skipped using SponsorBlock.</summary>
    public bool HomeBannerSkipIntros { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether viewers get the Calendar of upcoming episodes and movies (Sonarr and Radarr).</summary>
    public bool CalendarEnabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the Requests hub shows Sonarr and Radarr downloads.</summary>
    public bool DownloadsEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether viewers who aren't administrators see every download. Off: only titles
    /// they can see in Seerr's requests.
    /// </summary>
    public bool DownloadsShowAll { get; set; }

    /// <summary>Gets or sets a value indicating whether administrators get Sonarr and Radarr links and tags on detail pages.</summary>
    public bool ArrLinksEnabled { get; set; }

    /// <summary>Gets or sets the Sonarr address administrators open in their browser, when it differs from <see cref="SonarrUrl"/>.</summary>
    public string SonarrPublicUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Radarr address administrators open in their browser, when it differs from <see cref="RadarrUrl"/>.</summary>
    public string RadarrPublicUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether detail pages show ratings from MDBList.</summary>
    public bool RatingsEnabled { get; set; }

    /// <summary>Gets or sets the MDBList API key. Stays on the server.</summary>
    public string MdbListApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether titles on a viewer's Seerr watchlist are marked as liked (their Jellyfin watchlist).</summary>
    public bool WatchlistFromSeerr { get; set; }

    /// <summary>Gets or sets a value indicating whether titles a viewer likes in Jellyfin are added to their Seerr watchlist.</summary>
    public bool WatchlistToSeerr { get; set; }

    /// <summary>Gets or sets a value indicating whether viewers get the Catch Up page.</summary>
    public bool CatchUpEnabled { get; set; }

    /// <summary>Gets or sets a value indicating whether Catch Up choices are logged for administrators.</summary>
    public bool CatchUpLogEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether viewers can send the server owner a message from the Zorofin apps,
    /// answered from the Support tab. Today's apps also show Recommended to request only while this is on.
    /// </summary>
    public bool SupportInboxEnabled { get; set; }
}
