# Zorofin plugin for Jellyfin

Optional server features for Zorofin apps. Install it on a Jellyfin server, switch
on the modules you want, and Zorofin apps signed in to that server pick them up
automatically. Without it, Zorofin still works as a plain Jellyfin client.

**Setting up a server?** Start with [the setup guide](docs/SETUP.md). It covers
installing the plugin and the optional companion, every Dashboard tab, how the
two fit together, replacing other Jellyfin plugins, and troubleshooting. This
README is the technical reference.

Status: **pre-release (0.1.0)**. Not yet published to a plugin repository. Zorofin
apps need the `requests` version 2 support planned for the first update after 1.0
before they use the Requests module.

License: GPL-3.0 (see `LICENSE`). Jellyfin plugins link Jellyfin's GPL
libraries, so this plugin is open source. Zorofin's apps and its optional
companion service are separate programs that only talk to it over HTTP. See
[CONTRIBUTING.md](CONTRIBUTING.md) to help and [SECURITY.md](SECURITY.md) to
report a vulnerability. "Zorofin" and the Zorofin logo are not covered by the
code license; see [TRADEMARKS.md](TRADEMARKS.md).

This plugin is built largely with AI coding assistants, reviewed and tested by
its maintainer.

## Modules

| Module | Switch | Needs | Routes |
| --- | --- | --- | --- |
| Capabilities | always on | nothing | `GET /Zorofin/Capabilities` |
| Requests | Enable Requests | Seerr (Jellyfin users imported); optional Radarr/Sonarr for progress | `/Zorofin/Requests/*` |
| Popular this week | Record anonymous watch totals | nothing | `GET /Zorofin/Stats/Popular` |
| Home rows | Home tab: Zorofin Home rows, one switch per row | nothing (Requests and Popular this week add their rows) | `GET /Zorofin/Home/Sections`, `GET /Zorofin/Home/Rows` |
| Play history | Record play history (Activity page) | nothing | `/Zorofin/Admin/Activity/*` (administrators and API keys) |
| Seerr jobs | Next season requests; Tell Seerr about new arrivals | Seerr address and key | none (background) |
| Extras | Extras tab, one switch per feature | Sonarr/Radarr for Calendar, Downloads and admin links; an MDBList key for ratings; Seerr for watchlist sync | `/Zorofin/Extras/*`, `/Zorofin/Arr/*` |
| Announcement | Announcement tab | nothing | `GET /Zorofin/Announcement`, `POST /Zorofin/Announcement/Dismiss` |
| Support inbox | Support tab | nothing | `POST /support/ticket`, `GET /support/notifications`, `POST /support/notifications/{id}/read`; `/Zorofin/Admin/Support/*` (administrators) |
| Store-app addresses | follows Announcement, Support and the companion's modules | see below | `GET /support/announcement/me`, `GET /support/recommendations`, `GET`/`POST /support/request-alerts/*`, `GET`/`POST /Zorofin/Alerts/ConfirmEmail` (no sign-in) |
| Companion bridge | Companion URL and secret | an optional Zorofin companion service | `GET`/`POST /Zorofin/Companion/{module}/*` |

Everything is off on a fresh install, and the manifest advertises a module only
when it is switched on and configured. If Seerr is down, only Requests is
affected. Browsing and playback never depend on this plugin.

### Capabilities

`GET /Zorofin/Capabilities` (any signed-in user):

```json
{"schemaVersion":1,"features":{"requests":{"version":2,"enabled":true},"popularThisWeek":{"version":1,"enabled":true}}}
```

`requests` is **version 2** because version 1 meant the older Jellyfin Enhanced
proxy paths. Apps that only know version 1 hide Requests instead of calling
routes that don't exist.

### Addresses today's store apps use

The released Zorofin apps (iOS and Apple TV 1.3, Android 200, LG and Samsung)
read alert settings, Notifications, Support and Recommended to request from
`/support/*`, the addresses a server's own support service answered before this
plugin existed. The plugin answers them, so those apps get the features on any
server, and advertises their version-1 names only when it can answer:

| Name in `/Zorofin/Capabilities` | Listed when | Answered by |
| --- | --- | --- |
| `support` | Support tab is on | the plugin's Support inbox (`/support/ticket`); the apps also show Recommended to request only while this is listed |
| `notifications` | Support or Announcement is on, or the companion runs `notifications` | `/support/notifications` (Support replies) and `/support/announcement/me` |
| `notificationSettings` | the companion runs `notifications` | `/support/request-alerts`, forwarded to the companion's alert preferences |

`/support/recommendations` forwards to the companion's `recommendations` feed
and answers 404 when that module is off, which the apps treat as "no row".

Email alerts need the viewer to confirm their address. Today's apps have no box
for the code, so the companion's confirmation email also links to
`/Zorofin/Alerts/ConfirmEmail` on this server (built from the companion's
`ZOROFIN_APP_URL`, "Address viewers open" on the Connect tab). Opening the link
only shows a button; pressing it confirms. The code expires in 30 minutes and
locks after a few wrong tries.

A server whose own support service sits in front of Jellyfin keeps serving
`/support/*` itself; its `LegacyAppFeatures` list still wins in the manifest.
Newer apps move to `/Zorofin/*` addresses.

### Requests

The operator's Seerr API key stays on the server. For each call the plugin
finds the Seerr account linked to the signed-in Jellyfin user (Seerr's
`jellyfinUserId`) and calls Seerr **as that user** (`X-API-User`), so Seerr's
own permissions, quotas and auto-approval apply. Viewers without a linked Seerr
account get `403` and `user-status` says why.

Responses are reduced to the fields the apps use, so Seerr emails, other users'
accounts, service URLs and file paths never reach clients. The download queue
only lists titles from requests the viewer can already see.

| Route | Upstream |
| --- | --- |
| `GET user-status` | Seerr user list |
| `GET config` | Seerr Radarr/Sonarr settings (4K availability) |
| `GET settings/partial-requests` | Seerr main settings |
| `GET quota` | Seerr user quota |
| `GET discover/{trending\|movies\|tv\|upcoming}?page=` | Seerr discover |
| `GET discover/{movies\|tv}/upcoming?page=`, `GET discover/{movies\|tv}/genre/{id}?page=` | Seerr discover |
| `GET genres/{movie\|tv}` | Seerr genres (kept for an hour) |
| `GET search?query=&page=` | Seerr search (people removed) |
| `GET movie/{tmdbId}`, `GET tv/{tmdbId}` | Seerr details |
| `GET {movie\|tv}/{tmdbId}/recommendations?page=` | Seerr recommendations |
| `POST request` `{mediaType, mediaId, is4k, seasons?, tvdbId?}` | Seerr request |
| `GET activity?userOnly=&take=&skip=&filter=` | Seerr requests (+ details) |
| `GET queue` | Radarr/Sonarr queue, filtered |

Response bodies match the shapes Zorofin apps already decode. The companion's web
Requests hub uses the same routes, so web viewers don't need Jellyfin Enhanced. Seerr rejections
`400/403/404/409/422/429` pass through. An unreachable Seerr or a refused API key
returns `503`, and other upstream errors return `502`.

### Popular this week

Records, per movie or series, how long it was watched each day across the
server (plays shorter than 2 minutes or longer than 6 hours are ignored). Nothing
about users, devices or sessions is stored. Data lives in
`<jellyfin data>/zorofin/play-stats.json` and is kept for 35 days.

`GET /Zorofin/Stats/Popular?days=7&limit=20` returns only titles the signed-in
viewer can access: `{"days":7,"items":[{"id":"…","type":"Movie","plays":3}]}`.

### Home rows

Zorofin's own Home, so a server doesn't need the **Home Screen Sections** plugin.
The plugin builds every row for the signed-in viewer, with that viewer's library
access, parental limits and watch state:

| Row | Where | What's on it |
| --- | --- | --- |
| Continue Watching | pinned first | Resume points and Next Up, one card per show: the episode in progress wins, then the next one |
| My requests | pinned second | The viewer's own Seerr requests that are on the server: unwatched first (badged Ready), then watched. Needs Requests |
| New episodes for you | personal | Unwatched episodes of favorite shows added in the last 14 days |
| Watch in 90 minutes | personal | Unwatched movies of 90 minutes or less |
| 4K / HDR showcase | personal | Movies with a 4K video stream, badged from the streams (4K, Dolby Vision, HDR10, HDR10+, HLG) |
| Seasonal picks | personal | The same calendar as the apps: holidays, the season, then rotating moods. Matching collections first, then titles whose tags, text or genres fit. Titled after the theme, such as "Spooky season" |
| Popular this week | personal | Popular this week's ranking. Needs Popular this week |
| Your teams / starting soon | personal | Live TV sports on now or in the next 12 hours, filtered to the viewer's teams |
| Because you watched … | personal | Up to two rows: titles sharing the most genres with something the viewer finished |
| Favorites | library | Favorite movies and shows |
| Recently added movies / shows | library | Newest movies; shows with the newest episodes |
| Watch again | library | Movies and shows the viewer finished |

Rules match the Zorofin apps' own Home rows, and the seasonal plan is checked
against the same two-year fixture the apps use. Followed teams are read
from the viewer's Jellyfin display preferences (`zorofin-home`, client `zorofin`,
key `zorofinTeams`), where the apps and the web's "Your teams" dialog save them.

`GET /Zorofin/Home/Sections[?fresh=true]` (any signed-in user) lists the rows with
cards, for the web client. Empty rows are left out. `placement` is `pinned`,
`personal` or `library`. Each card has the item in Jellyfin's own field names,
reduced to what cards show (no paths or media sources):

```json
{"schemaVersion":1,"generatedAt":"…","sections":[{"id":"new-episodes","title":"New episodes for you","placement":"personal",
 "cards":[{"id":"…","subtitle":"New in your library","badges":["New episode"],"item":{"Id":"…","Name":"…","Type":"Episode","ImageTags":{…},"UserData":{…}}}]}]}
```

`GET /Zorofin/Home/Rows` answers in the shape of the older
`/support/home/rows` feed (schema 1, the seven app rows, empty and unavailable rows
included). The manifest lists `homeRows` as **version 2** because version 1 means
the `/support/home/rows` path, so today's apps ignore it until an app update
reads the new path. It also lists `homeSections` version 1.

A viewer's Home is built once a minute at most; requests from the same viewer
share one build, and nothing is shared between viewers. A row that fails is
left out; the others still show. `?fresh=true` rebuilds after a teams change.
The Home tab can switch off any row (`HomeRowsOff`). An optional dated calendar
for holidays that move (Ramadan, Eid, Diwali…) uses the
`curated-calendar.json` format in `HomeCuratedCalendar`.

The web client shows these rows when the Zorofin look is on: the companion pack's
`35-theme-home-plugin.js` draws them and hides Jellyfin's own and Home Screen
Sections' rows. With Home rows off, Jellyfin's own Home stays.

### Announcement

One message for everyone on the server, written in the setup page's
**Announcement** tab: kind (News, Good news, Heads up, Maintenance), title (80
characters), message (320), an optional button (a path on this server or an
https link), optional start and end times, and whether viewers can mark it
read. Viewers find it at the top of Notifications, with a dot on the bell until
they read it; it is never a banner over Home. The tab previews the
Notifications entry as viewers will see it.

`GET /Zorofin/Announcement` (any signed-in user) returns the same shape as
the older `/support/announcement/me` feed, so the web client and the apps read
one feed:

```json
{"enabled":true,"id":"a-3f9c0e12ab45cd67","type":"maintenance","title":"…","message":"…",
 "dismissible":true,"startAt":null,"endAt":null,"action":{"label":"…","href":"/web/#/home"},"dismissed":false}
```

Outside its start/end window, or with an empty title or message, it returns
`{"enabled":false}`. The `id` is derived from the content, so editing the
announcement shows it again to people who dismissed it; "Show it again to
viewers who dismissed it" does the same without an edit.

`POST /Zorofin/Announcement/Dismiss {"id":"…"}` hides the current announcement
for the signed-in account on all of its devices. The account comes from the
Jellyfin session, never the body. Dismissals (user id and announcement id only,
the 50 most recent per account) are kept in
`<jellyfin data>/zorofin/announcement-dismissals.json`. A non-dismissible
announcement answers `409`. When it is on, the capability manifest lists
`announcements` version 1; the iOS, Apple TV and TV apps can list the same
message in their Notifications once they support it.

### Play history and the Activity page

Off by default, with its own switch on the **Zorofin activity** page in the
Dashboard sidebar. When on, every finished play is kept: who, what, when, how
long (paused time left out), app, device, and direct play or transcode. Plays
under 10 seconds are dropped. History lives in
`<jellyfin data>/zorofin/play-history/<yyyy-MM>.jsonl` and is kept for a year
unless the retention setting says otherwise. Only administrators (and API keys)
can read it. Popular this week stays separate and anonymous.

The Activity page shows watch time per day, a weekday-by-hour chart, ranked
shows, movies, viewers, apps, devices, playback methods and Live TV channels,
and every play with search, paging and CSV download. It can delete one
viewer's history or all of it.

| Route | What |
| --- | --- |
| `GET Status` | switch, retention, plays stored |
| `GET Summary?days=&from=&to=&offset=&userId=&type=` | everything the charts draw (`offset` = viewer's minutes from UTC) |
| `GET Log?…&q=&skip=&take=` | plays, newest first |
| `GET Export?…` | the same as CSV |
| `GET Rows?since=&until=` | plays in Playback Reporting's column names, for Rewind |
| `GET Popularity?days=7` | watch time per item, for the Popular Home row script |
| `POST Import` | Playback Reporting rows, from `tools/import-playback-reporting.py` |
| `DELETE ?userId=` | delete history (administrators only, not API keys) |

**Bringing in Playback Reporting's history:** run
`JELLYFIN_API_KEY=… tools/import-playback-reporting.py` (in this
repository) on the machine that has `playback_reporting.db`, before turning
Play history on. Re-runs skip what is already there, and plays after the plugin
started recording are left out so nothing counts twice. Imported plays keep
Playback Reporting's row ids, so Rewind's saved history does not double up.

### Seerr jobs

Both are off by default, on the Requests step, and need only Seerr's address
and key (not the Requests switch).

- **Request the next season when a viewer nears the end.** After a viewer
  watches at least half of an episode in the newest season the server has, with
  two or fewer episodes of that season left (configurable), the next season is
  requested in Seerr as that viewer. Their Seerr permissions, quota and
  auto-approval apply. Nothing is sent if Seerr doesn't list the season, it is
  more than a year away, it is already available or being processed, or any
  request for it exists (a declined one included). Each viewer, show and season
  is tried at most once every 12 hours.
- **Tell Seerr when new movies and episodes arrive.** Runs Seerr's
  `jellyfin-recently-added-scan` job after the last arrival of a batch has been
  quiet for two minutes (configurable), and never more than ten minutes after
  the first, so requests show as available right away.

### Extras

These replace Media Bar and the Jellyfin Enhanced features most servers use, so
a server can run with only the Zorofin plugin, the companion and the data
plugins the Overview tab checks for (TheTVDB, TheSportsDB, Open Subtitles,
Intro Skipper). Each is off until switched on in the Extras tab. The web side
lives in the companion web pack.

| Feature | Route | Notes |
| --- | --- | --- |
| Which extras are on | `GET /Zorofin/Extras` | Signed-in viewers; the web pack reads it once every 5 minutes |
| Home banner trailers and titles | `GET /Zorofin/Extras/Trailer/{youTubeId}` | Where the studio intro ends, from SponsorBlock, asked by the server and cached a week |
| Ratings | `GET /Zorofin/Extras/Ratings/{itemId}` | MDBList, with the operator's key; an episode or season uses its show; cached a day, last answer kept if MDBList is down |
| Catch Up log | `POST`/`GET /Zorofin/Extras/CatchUp/Log` | GET is administrators only; JSONL, about 10 MB at most |
| Plugins Zorofin needs | `GET /Zorofin/Extras/Prerequisites` | Administrators; shown on the Overview tab |
| Calendar | `GET /Zorofin/Arr/Calendar?start=yyyy-MM-dd&days=28` | Up to 62 days; links titles already on the server |
| Downloads | `GET /Zorofin/Arr/Downloads` | Viewers see downloads of their own Seerr requests unless "Everyone sees every download" is on; no paths, clients or indexers are sent |
| Sonarr / Radarr links | `GET /Zorofin/Arr/Item/{itemId}` | Administrators only |
| Remove from Continue Watching | `POST`/`DELETE /Zorofin/Home/ContinueWatching/{itemId}/Hide` | Needs Home rows; progress is kept and the title comes back when it's played again |
| Watchlist sync | none (background, every 30 minutes) | Seerr watchlist ↔ the viewer's Likes; removals carry over only when both directions are on, and a title that left the library is never removed from Seerr |

### Companion bridge

Optional. When a companion URL and shared secret (32+ characters) are set, the
plugin asks the companion which modules it offers and adds them to
`/Zorofin/Capabilities`. A companion module can never replace a plugin module,
and if the companion is down its modules simply disappear from the manifest.

`/Zorofin/Companion/{module}/{path}` forwards a signed-in viewer's GET or POST
(JSON, up to 64 KB) to an enabled companion module. The companion receives the
viewer's id and admin flag in a signed header, never the viewer's Jellyfin
token; `api_key` query parameters are stripped. The contract is public:
[docs/COMPANION_BRIDGE.md](docs/COMPANION_BRIDGE.md).

**Setup page.** The plugin has its own **Zorofin** entry in the Dashboard
sidebar (`EnableInMainMenu`), as well as under Plugins. The page is a tabbed
setup in the Zorofin look, in setup order: Overview, Connect, Requests,
Alerts, Live TV, For you, and Look & player, plus a tab for any other part the
companion lists. Each tab has a short explanation, an on/off switch that hides
its settings while off, one Save (and Test where the companion can check
it), and a status mark. Overview shows what is on, what needs attention and
the next step. Seerr is entered once in Connect and used for both Requests and
the companion; companion settings are sent only when changed, so values set on
the container stay there. The page draws the companion's sections from what it
lists, and calls `/Zorofin/Admin/Companion/Settings`,
`/Settings/{section}` and `/Settings/{section}/Test`. These routes accept only
Jellyfin administrators and forward over the signed bridge with the
administrator's id and admin flag. The companion keeps the values and never
returns saved keys. Browser test:
`node tests/web/config-page-browser.test.cjs`.

### Web UI

Optional, off by default. With **Zorofin look in the web client** on (Look &
player in the setup page) and a
companion whose `webUi` module has files, the plugin adds one script tag to
jellyfin-web's `index.html` (its own response filter; no other plugin is
needed). The loader at `/Zorofin/Web/loader.js` adds the companion's styles and
scripts, passed through `/Zorofin/Web/ui/{file}`. Both routes are public
because the theme also styles the sign-in page. When the companion downloads
sports artwork, `/Zorofin/Web/ui/live-tv-logos/art/{file}` passes through its
`events.json` catalogue (cached a minute) and content-hashed crest and photo
images (cached a week); other names return 404. Turning the setting off, or
stopping the companion, gives the plain web client back on the next page load.

## Build and test

Requires the .NET 10 SDK (Jellyfin 12.x).

```sh
dotnet test                                   # unit tests
node tests/web/config-page-browser.test.cjs   # setup page (needs Playwright)
node tests/web/activity-page-browser.test.cjs # Activity page (needs Playwright)
dotnet build src/Jellyfin.Plugin.Zorofin -c Release
```

The plugin is `src/Jellyfin.Plugin.Zorofin/bin/Release/net10.0/Jellyfin.Plugin.Zorofin.dll`.

## Install (manual, for testing)

1. Stop Jellyfin.
2. Copy the DLL to `<jellyfin config>/plugins/Zorofin_0.1.0.0/`.
3. Start Jellyfin, then open **Zorofin** in the Dashboard sidebar (also under Dashboard → Plugins).
4. To switch on Requests: import your Jellyfin users in Seerr, then enter the
   Seerr URL (as Jellyfin reaches it) and its API key.

**Upgrade:** replace the DLL in a new version folder and restart. Settings are
kept in `<jellyfin config>/plugins/configurations/Jellyfin.Plugin.Zorofin.xml`.
**Remove:** uninstall from Dashboard → Plugins (or delete the folder) and
restart. Optionally delete `<jellyfin data>/zorofin/`.
**Recover:** if Jellyfin fails to start after an install, delete the plugin
folder and restart.

**Servers that already run their own Zorofin support service** (such as the
reference server): apps stop asking that service's `/support/capabilities` once
`/Zorofin/Capabilities` answers. Before installing, plan to switch on the
features it serves under Connect → **Older Zorofin apps** (stored as
`LegacyAppFeatures`, for example
`support,notifications,requests,homeRows,liveArtwork,enhancedSettings,accountApprovals,notificationSettings,liveTVAvailability`).
Each listed feature is advertised at version 1 and wins over the plugin's own
version, so apps made before the plugin keep working: `requests` 1 keeps them
on Jellyfin Enhanced and `homeRows` 1 on `/support/home/rows`, while the web
client still uses `/Zorofin/Home/Sections`. A support service in front of
Jellyfin keeps answering its own `/support/*` routes; the plugin's versions of
some of them (see "Addresses today's store apps use") only answer when a request
reaches Jellyfin. Untick a feature once its replacement is live and the apps that
need it are updated. A new server leaves them all off.

## Smoke test performed

On October 28, 2026 this build loaded on Jellyfin **12.4.0** (Docker) and was
tested against a fake Seerr/Radarr:
- capabilities empty until configured;
- every Requests route returned shaped data acting as the linked Seerr user;
- the request POST carried `X-API-User`;
- an unlinked user got `403`;
- unauthenticated calls got `401` and viewers got `403` on plugin settings;
- Seerr outages left core Jellyfin working;
- a real playback session appeared in Popular.

A live Seerr has not been tested yet.

On October 29, 2026 the companion bridge was tested on Jellyfin **12.4.0**
against the companion skeleton with a test-only echo module:
- the companion's module appeared in `/Zorofin/Capabilities` beside plugin
  modules once the URL and secret were saved;
- a viewer's GET and an admin's POST reached the module with the right user id
  and admin flag; an `api_key` query parameter was not forwarded;
- anonymous calls got `401`, unknown modules `404`, non-JSON bodies `415` and
  bodies over 64 KB `413`;
- with the companion stopped, proxied calls got `503` and Jellyfin kept working.

On November 3, 2026 the web UI loader was tested on Jellyfin **12.4.0** (Docker)
against the companion's `webUi` module with a test theme and two scripts:
- with the setting off, `index.html` was unchanged and kept its `ETag`;
- with it on, `/web/` and `/web/index.html` had the loader once, including for a
  browser asking for gzip with a stale `If-None-Match`;
- in Chromium the sign-in page loaded the theme and both scripts in order, with
  no page errors;
- disallowed names (`notes.txt`, an encoded `../`) and missing files got `404`;
- turning the setting off, or stopping the companion, served the plain sign-in
  page with no Zorofin styles or scripts and no errors.

On November 4, 2026 play history and the Seerr jobs were tested on Jellyfin
**12.4** (Docker) with a generated test movie and a fake Seerr:
- "Zorofin activity" appeared in the Dashboard sidebar and drew its charts,
  lists and play log with no page errors;
- a play of 8 seconds, a 6-second pause and 8 more seconds was stored as 16
  seconds, with the app, device and direct play;
- viewers got `403` and anonymous calls `401` on the Activity routes, and an API
  key could read but not delete;
- the import tool added a Playback Reporting row once (a second run skipped it),
  converted its New York time to UTC, left out a row from after the plugin
  started recording and refused one with a bad user id;
- adding a movie to the library started Seerr's `jellyfin-recently-added-scan`
  once, about ten seconds later.

- with a three-episode show, finishing episode 1 sent nothing; finishing
  episode 2 (one left) looked up the viewer's Seerr account, read the show as
  that viewer and requested season 2 with `X-API-User`; playing it again sent
  nothing.

A live Seerr has not been tested with these jobs yet.

