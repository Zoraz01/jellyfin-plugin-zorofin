# Set up Zorofin on your Jellyfin server

This guide is for people who run a Jellyfin server. It explains what the
Zorofin plugin and the Zorofin companion do, how to install them, what each
setting in the Dashboard means, and how to replace other Jellyfin plugins with
them. Viewers don't need any of this: they sign in to your server from a
Zorofin app or the web, and the features you turn on show up by themselves.

Contents:

1. [What you get](#what-you-get)
2. [Do I need the companion?](#do-i-need-the-companion)
3. [Before you start](#before-you-start)
4. [Install the plugin](#install-the-plugin)
5. [Run the companion (optional)](#run-the-companion-optional)
6. [Turn on features, tab by tab](#turn-on-features-tab-by-tab)
7. [How it works](#how-it-works)
8. [Replacing other Jellyfin plugins](#replacing-other-jellyfin-plugins)
9. [Update, back up, remove](#update-back-up-remove)
10. [Troubleshooting](#troubleshooting)
11. [Questions](#questions)

## What you get

Zorofin works with any Jellyfin server as it is. Two free, open-source add-ons
switch on the extras. Every feature is off until you turn it on.

| | Zorofin plugin | Zorofin companion |
| --- | --- | --- |
| What it is | A Jellyfin plugin (one DLL) | A small Docker container that runs next to Jellyfin |
| Where you set it up | Dashboard → **Zorofin** | The same Dashboard page, once it is connected |
| What it does | Requests through Seerr, Home rows, Popular this week, the server announcement, Support messages, play history and the Activity page, Seerr background jobs | "Ready to watch" alerts, a better Live TV guide, recommendations to request (optional AI), the Zorofin look and player in Jellyfin's web app |
| License | GPL-3.0 | AGPL-3.0 |

You can run the plugin on its own. Add the companion only for the features that
need it.

## Do I need the companion?

Zorofin is in early release (0.1). The **Today** column says where each
feature works now. "Apps" means the current Zorofin apps (1.3 and later); older
versions need an update from their app store. "Coming" means the server side is
built and the screen that uses it is still being finished.

| Feature | What viewers get | Needs | Today |
| --- | --- | --- | --- |
| Requests | Browse, search and request movies and shows, then follow progress | Plugin, Seerr. Radarr and Sonarr are optional (download progress) | Apps, and the web (Requests hub, with the companion) |
| Next season requests | The next season is requested for a viewer as they finish the current one | Plugin, Seerr | Works |
| Seerr availability scan | Requests show as available as soon as a title arrives | Plugin, Seerr | Works |
| Home rows | Continue Watching, My requests, rows picked for each viewer and library shelves | Plugin, and the companion for the web | Apps and web |
| Popular this week | A row of what your server watched most over the past week | Plugin | Apps and web, as a Home row (needs Home rows) |
| Announcement | One message for everyone, at the top of Notifications | Plugin | Apps and web |
| Support | Viewers send you a message from the apps; your reply shows in their Notifications | Plugin | Apps |
| Play history and Activity page | Charts and a play log for administrators | Plugin | Works |
| "Ready to watch" alerts | Email or push when a requested title arrives | Companion, Seerr webhook, your own Resend account (email) or ntfy server (push) | Apps (Notification settings). Web: coming |
| Live TV guide | Matchup titles for games, artwork for shows and games, missing channel logos | Companion, Jellyfin Live TV with an XMLTV guide | Works (it improves Jellyfin's own guide, so every app sees it) |
| Recommended to request | A personal row of movies a viewer might want to request | Companion, Seerr. An AI key is optional | Apps, while Support is on (see [For you](#for-you)). Web: coming |
| Zorofin look and player on the web | Jellyfin's web app looks and plays like the Zorofin apps, with a Home banner, Live TV pages and a Requests hub | Plugin and companion | Works |

If the companion is stopped, Jellyfin keeps working as plain Jellyfin: the
companion's features switch off and the plugin's keep running.

## Before you start

- **Jellyfin 12** (the plugin is built against Jellyfin 12.2 and tested on
  12.4). You need an administrator account.
- **Docker** with Compose 2.20 or later, only if you run the companion. Linux,
  NAS boxes and macOS with Docker Desktop all work.
- **Seerr** (or Jellyseerr), only for Requests, alerts and recommendations.
  In Seerr, import your Jellyfin users first (Seerr → Users → Import Jellyfin
  Users), because each viewer's requests are sent as their own Seerr account.

- **These Jellyfin plugins, installed alongside Zorofin.** Zorofin replaces
  most other plugins (see [Replacing other Jellyfin plugins](#replacing-other-jellyfin-plugins)),
  but it builds on these four, which do jobs Zorofin doesn't. Install them from
  Jellyfin's own catalogue (Dashboard → Plugins → Catalog) before you start:

  | Plugin | Why Zorofin needs it |
  | --- | --- |
  | **TheTVDB** | Show, season and episode details and artwork, so Home rows, Continue Watching and episode lists are complete and in the right order |
  | **TheSportsDB** | Sports details and artwork for the Live TV sports row and Your teams |
  | **Open Subtitles** | Downloads missing subtitles, which the apps and the Zorofin player show |
  | **Intro Skipper** | Finds intros and credits, so the apps and the Zorofin player can show Skip Intro and jump to the next episode |

  Open Subtitles needs a free opensubtitles.com account, entered in its own
  plugin settings. Intro Skipper may not be in Jellyfin's built-in catalogue:
  if you don't see it, add the plugin repository from Intro Skipper's own
  README under Dashboard → Plugins → Repositories first.

Pick a quiet time: installing the plugin needs one Jellyfin restart. Nothing
else in this guide does.

## Install the plugin

The plugin is not in a plugin repository yet, so it is installed by hand.

1. **Get the plugin.** Until the first release is published on the plugin's
   GitHub Releases page, build it with the .NET 10 SDK:

   ```sh
   dotnet build src/Jellyfin.Plugin.Zorofin -c Release
   # → src/Jellyfin.Plugin.Zorofin/bin/Release/net10.0/Jellyfin.Plugin.Zorofin.dll
   ```

2. **Stop Jellyfin.**
3. **Copy the DLL** into a new folder named `Zorofin_0.1.0.0` inside Jellyfin's
   `plugins` folder:

   | How Jellyfin is installed | Plugins folder |
   | --- | --- |
   | Docker (official or linuxserver image) | `/config/plugins` inside the container, which is the config folder you mounted |
   | Debian or Ubuntu package | `/var/lib/jellyfin/plugins` |
   | Windows | `%ProgramData%\Jellyfin\Server\plugins` (tray install) or `%LocalAppData%\jellyfin\plugins` |
   | macOS app | `~/Library/Application Support/jellyfin/plugins` |

   On Linux, make sure the folder and DLL belong to the user Jellyfin runs as.

4. **Start Jellyfin.** A **Zorofin** entry appears in the Dashboard sidebar
   (also under Dashboard → Plugins), next to **Zorofin activity**.

Everything is off after installing. Open **Zorofin** and work through the tabs
from left to right. The **Overview** tab always shows what is on, what needs
attention and what to do next.

## Run the companion (optional)

The companion lives in its own repository and ships a ready-made
`compose.yaml`. It has no published port and joins the Docker network your
Jellyfin and Seerr are on.

1. **Get the files.** Clone the companion repository (or copy its
   `compose.yaml`, `companion.env.example` and `tools/new-secrets.sh`).
2. **Create the secrets.** Run `tools/new-secrets.sh`. It makes a `secrets/`
   folder with a bridge secret (shared with the plugin) and a Seerr webhook
   secret. Add two more files there if your modules need them:
   `jellyfin-api-key` (Jellyfin Dashboard → API Keys → new key) and
   `seerr-api-key` (Seerr → Settings → General). You can also enter these two
   keys in the Dashboard later instead.
3. **Tell it where things are.** The defaults expect a Docker network named
   `yams_network` with containers called `jellyfin` and `seerr`. Most servers
   use other names: `docker network ls` lists your networks, and
   `docker inspect jellyfin --format '{{json .NetworkSettings.Networks}}'`
   shows the one Jellyfin is on. The network must already exist. Put the
   values that differ in a `.env` file next to `compose.yaml`:

   | Variable | Default | Change it when |
   | --- | --- | --- |
   | `ZOROFIN_NETWORK` | `yams_network` | Your services are on another Docker network |
   | `ZOROFIN_JELLYFIN_URL` | `http://jellyfin:8096` | Jellyfin has another name, or runs on the host |
   | `ZOROFIN_SEERR_URL` | `http://seerr:5055` | Seerr has another name, such as `http://jellyseerr:5055` |
   | `ZOROFIN_MODULES` | `notifications` | You want more modules: `notifications,liveTvGuide,recommendations,webUi` |

   For example, Jellyfin on a network called `media` with every module:

   ```sh
   ZOROFIN_NETWORK=media
   ZOROFIN_MODULES=notifications,liveTvGuide,recommendations,webUi
   ```

   Each module's own settings (email service, ntfy, guide address and so on)
   are easiest to enter later in the Dashboard. You can also put them in
   `companion.env` (copy `companion.env.example`).

4. **Start it.** Either include it in your existing stack:

   ```yaml
   # docker-compose.yaml
   include:
     - path/to/zorofin-companion/compose.yaml
   ```

   then `docker compose up -d zorofin-companion`, or run
   `docker compose -f path/to/zorofin-companion/compose.yaml up -d`.

   **Jellyfin installed on the host, not in Docker?** Add
   `compose.host-jellyfin.yaml` as well. It publishes the companion on
   `127.0.0.1:9310` only, and points the companion at
   `http://host.docker.internal:8096`.

5. **Connect it.** In Jellyfin, open Dashboard → **Zorofin** → **Connect**:
   - **Companion address:** `http://zorofin-companion:9310` when Jellyfin is a
     container on the same network, or `http://localhost:9310` when Jellyfin
     runs on the host.
   - **Companion secret:** the contents of `secrets/bridge-secret`.

   Save. The chip next to "Zorofin companion" turns to **Connected**, and the
   companion's settings appear in the tabs. Press **Test** to check that the
   companion can reach Jellyfin and Seerr.

Never publish the companion's port to the internet. Only Jellyfin talks to it.

## Turn on features, tab by tab

Each tab has one switch per feature, a **Save** button, and a **Test** button
where the companion can check the settings. A dot on the tab shows its state:
green is on, amber needs attention. Each tab also has a short **How this
works** note you can open.

Settings for the companion are stored by the companion (in its data folder,
readable only by it) and override the matching environment variables. Keys
are never shown again after saving; the page only says whether one is saved.
Values you set on the container are marked "Set on the container" and are left
alone unless you change them here.

### Connect

Where your companion and Seerr live. Every other tab uses these.

- **Zorofin companion:** address and secret, as above. Leave both empty if you
  don't run the companion.
- **Seerr:** the address as Jellyfin reaches it and the API key. Enter them
  once: the plugin and the companion both use them.
- **Your server** (with the companion): the Jellyfin address and API key the
  companion uses for read-only lookups, the server name shown in emails and
  alerts, and the address viewers open (links in alerts point there, and so
  does the button that confirms a viewer's email address, so use an address
  viewers can reach).
- **Older Zorofin apps:** leave every switch off. It only exists for servers
  that ran their own Zorofin support service before the plugin existed.

### Requests

Viewers browse what's trending, search, and request movies and shows, in the
Zorofin apps and on the web through the Requests hub (Look & player). Zorofin
apps older than 1.2 only show Requests on servers that use Jellyfin Enhanced;
viewers on those versions need to update the app.

- The plugin keeps your Seerr key on the server. For each viewer it finds their
  linked Seerr account and sends the request **as that viewer**, so Seerr's
  permissions, quotas and auto-approval apply exactly as on Seerr's own site.
- A viewer without a Seerr account sees why they can't request. Import them
  in Seerr to fix it.
- Optional **Radarr and Sonarr** addresses and keys add download progress to
  My requests. Viewers only see titles from requests they could already see.
- Answers are trimmed before they reach the apps: no emails, other people's
  accounts, file paths or service addresses.

**Keep Seerr up to date** (on the same tab) has two background jobs. They only
need the Seerr address and key, not the Requests switch:

- **Request the next season when a viewer nears the end.** When a viewer
  watches at least half of an episode in the newest season on your server,
  with two or fewer episodes left (you choose the number), the next season is
  requested in Seerr as that viewer. Nothing is sent if the season isn't announced, is more than a year
  away, is already there or was requested (or declined) before.
- **Tell Seerr when new movies and episodes arrive.** Runs Seerr's
  recently-added scan two minutes after a batch of arrivals goes quiet, so
  requests flip to available right away.

### Alerts (companion)

When a requested title lands in your library, the viewer who asked for it gets
an email, a push notification or both. An email address is used only after the
viewer confirms a code sent to it.

Viewers turn alerts on in the apps under **Notification settings**. Push works
there straight away. For email, the viewer enters their address and gets an
email with a **Confirm email alerts** button (and a 6-digit code), which opens a
page on your Jellyfin server. The web client's settings screen is still coming.

1. Turn on **Request alerts** and fill in at least one channel:
   - **Email:** a [Resend](https://resend.com) API key (or a compatible
     service's address) and a sender, such as `Movie Night <alerts@example.com>`.
   - **Push:** your own [ntfy](https://ntfy.sh) server address and publish
     token. Add an admin token to give each viewer a private, read-only ntfy
     login.
2. In Seerr → Settings → Notifications → **Webhook**: enable it, tick
   **Request Available**, and set:
   - Webhook URL: `http://zorofin-companion:9310/v1/hooks/seerr`
   - Authorization header: `Bearer ` followed by the contents of
     `secrets/seerr-webhook-secret`
   - JSON payload (no email fields, on purpose):

     ```json
     {
       "notification_type": "{{notification_type}}",
       "request_id": "{{request_id}}",
       "requestedBy_jellyfinUserId": "{{requestedBy_jellyfinUserId}}",
       "subject": "{{subject}}",
       "media_status": "{{media_status}}",
       "media_type": "{{media_type}}",
       "media_jellyfinMediaId": "{{media_jellyfinMediaId}}",
       "media_tmdbid": "{{media_tmdbid}}",
       "media_tvdbid": "{{media_tvdbid}}"
     }
     ```
3. Press **Test** on the Alerts tab. It checks the email service and ntfy settings.

Before an alert goes out, the companion checks the viewer can actually see the
title in Jellyfin. Failed alerts are retried every minute for six hours.

### Live TV (companion)

Makes your XMLTV guide nicer to browse. Sports listings get the matchup in the
title ("NBA Basketball — Bulls vs Lakers"), programmes without artwork get a
show poster (TVmaze) or a matchup card (TheSportsDB), and channels without a
logo get one from the public tv-logos collection. Artwork is copied to the
companion, so Jellyfin never depends on those sites staying up. If your guide
source breaks, Jellyfin keeps the last good guide.

1. Turn on **Live TV guide**. Enter the XMLTV address your tuner or guide tool
   publishes (Threadfin, xTeVe, an HDHomeRun or a listings service), a guide
   link token of 32 or more letters and digits, and the companion address as
   Jellyfin reaches it.
2. Choose the extras: matchup titles, TVmaze artwork, a TheSportsDB key, and
   channel logos for the countries you pick. Each one is your choice; with all
   of them off the module makes no outside calls.
3. In Jellyfin → Dashboard → Live TV → TV Guide Data Providers → XMLTV, replace
   your guide address with
   `http://zorofin-companion:9310/v1/public/liveTvGuide/<token>/guide.xml`.

The Sports row and Your teams in the apps work without the companion, as long
as your guide lists the games. The companion only makes the guide better.

### For you

Rows on Home that help viewers find something to watch.

- **Popular this week** (plugin): counts how long each movie or series is
  watched per day across the server, kept 35 days. It stores no user, device
  or session, and only shows each viewer titles they can access.
- **Recommended to request** (companion): a personal row of movies that aren't
  on your server or requested yet, based on what each viewer finished,
  favorited and requested. Candidates come from Seerr, so there's no extra
  key. A viewer needs at least three finished movies before the row shows.
  - The apps show it under Requests. Today's apps only ask for it while
    **Support** is on (Support tab), a quirk the next app update removes. The
    web client doesn't show it yet.
  - **AI ranking** is optional and off by default. With your own Anthropic or
    OpenAI-compatible key (or a local model such as Ollama), the AI reorders
    the picks and adds a one-line reason. It only sees titles and overviews,
    never names, emails or account ids. If it fails, the standard ranking is
    used. Any cost stays on your account.

### Home

Zorofin builds every Home row for each viewer, with their own library access,
parental limits and watch state, so you don't need the Home Screen Sections
plugin.

| Row | What's on it |
| --- | --- |
| Continue Watching | One card per show: the episode in progress, or the next one |
| My requests | The viewer's own requests that have arrived (needs Requests) |
| New episodes for you | New episodes of favorite shows from the last 14 days |
| Watch in 90 minutes | Unwatched movies of 90 minutes or less |
| 4K / HDR showcase | Movies with a 4K source, badged 4K, Dolby Vision, HDR10, HDR10+ or HLG |
| Seasonal picks | Holidays and seasons, from your collections first |
| Popular this week | Needs Popular this week |
| Your teams / starting soon | Live TV sports on now or in the next 12 hours, for the viewer's teams |
| Because you watched … | Up to two rows like titles the viewer finished |
| Favorites, Recently added, Watch again | Library shelves |

Each row has its own switch. Empty rows stay hidden. A viewer's Home is built
at most once a minute and never shared with anyone else.

The web shows these rows when the Zorofin look is on, and the Zorofin apps
(1.3 and later) read them too. With Home rows off, Jellyfin's own Home stays,
and the apps build their rows on the device.

### Announcement

One short message for everyone on the server: a kind (News, Good news, Heads
up, Maintenance), a title, a message, an optional button, and optional start
and end times. Viewers find it at the top of Notifications, with a dot on the
bell until they read it. It is never a banner over Home. It shows in the web
client and in the Zorofin apps' Notifications.

- Marking it read on one device hides it on that account's other devices.
- Editing it shows it again to everyone. **Show it again to viewers who read
  it** does the same without an edit.
- The preview on the tab shows exactly what viewers will see.

### Support

Viewers open **Support** in the Zorofin apps, pick what went wrong (for example
playback or a missing episode), write a short description and, if they like,
name the title. It lands on the **Support** tab, newest first. Write a reply
and press **Send reply**: it shows in that viewer's Notifications, with a dot on
the bell until they read it. Close or delete a message when you're done.

- Each viewer can send five messages an hour. The newest 500 are kept.
- Nothing is emailed and nothing leaves your server.
- This is a plain inbox. There is no automatic or AI help.

### Look & player (companion)

Gives Jellyfin's own web app the Zorofin look, so the browser matches the
apps. One switch turns it on; each part has its own switch:

- **Zorofin theme:** navigation, sign-in, Home, detail pages, search and
  colours.
- **Zorofin player:** the apps' playback controls over Jellyfin's own player.
  Streaming, subtitles, quality and progress stay Jellyfin's.
- **Home banner:** featured unwatched movies and shows across the top of Home,
  in place of the Media Bar plugin.
- **Live TV pages and guide,** with optional **team crests and game photos**
  for the sports row (your server downloads them; off by default).
- **Requests hub:** discover, request and follow titles on the web, through the
  plugin's Requests (no Jellyfin Enhanced needed).
- **Colours and logo:** preset colours or your own accent, and the Zorofin
  logo, your own (wide and square), or none.

Viewers reload their browser tab to see a change. Turn the switch off, or stop
the companion, and the web app is plain Jellyfin again on the next reload. The
plugin adds one script tag to Jellyfin's web page itself, so you don't need
JavaScript Injector or custom CSS.

### Zorofin activity

A separate Dashboard page with its own switch, **Record play history**. While
on, every finished play is kept: who, what, when, how long (paused time left
out), app, device, and direct play or transcode. Plays under 10 seconds are
dropped. History is kept for a year unless you change it, and only
administrators can see it.

The page shows watch time per day, a weekday-by-hour chart, top shows, movies,
viewers, apps, devices and Live TV channels, and every play with search and
CSV download. You can delete one viewer's history or all of it.

Popular this week stays separate and anonymous.

## How it works

```
Zorofin app / web ──Jellyfin sign-in──▶ Jellyfin + Zorofin plugin ──signed requests──▶ companion
                                                │                                       ▲
                                                └──▶ Seerr, Radarr, Sonarr               │
Seerr ──webhook (Bearer secret)─────────────────────────────────────────────────────────┘
```

- **Apps only talk to your Jellyfin server.** When an app signs in it asks
  `/Zorofin/Capabilities` what this server offers, and shows only those
  features. A server without the plugin is a plain Jellyfin server to the
  apps, and everything still works.
- **The plugin is the gatekeeper.** It checks each viewer's Jellyfin session,
  then calls Seerr or the companion on their behalf. Service keys stay on the
  server.
- **The companion never sees viewers' Jellyfin sign-ins.** The plugin forwards
  a viewer's request with only their Jellyfin user id and whether they are an
  administrator, signed with the shared secret. The companion rejects anything
  unsigned, older than a minute, or replayed. The contract is public:
  [COMPANION_BRIDGE.md](COMPANION_BRIDGE.md).
- **Failures stay small.** If Seerr is down, only Requests is affected. If the
  companion is down, its features drop out of the capability list within a
  minute and the apps hide them. Browsing and playback never depend on either.

**What is stored, and where:**

| Data | Where | Kept |
| --- | --- | --- |
| Plugin settings, including Seerr, Radarr and Sonarr keys | `<jellyfin config>/plugins/configurations/Jellyfin.Plugin.Zorofin.xml` | Until changed |
| Popular this week (titles and minutes only) | `<jellyfin data>/zorofin/play-stats.json` | 35 days |
| Play history (when on) | `<jellyfin data>/zorofin/play-history/` | 365 days by default |
| Announcement dismissals (user id and announcement id) | `<jellyfin data>/zorofin/announcement-dismissals.json` | Last 50 per account |
| Support messages and replies (user id and name, the message, your reply) | `<jellyfin data>/zorofin/support-inbox.json` | Newest 500 |
| Companion settings and keys | Companion data folder, `settings.json` (mode 600) | Until changed |
| Alert preferences, confirmed emails, delivery records | Companion data folder | Delivery records 30 days |
| Guide artwork, sports art, uploaded logos | Companion data folder | Pruned automatically |

## Replacing other Jellyfin plugins

The plugin and companion are meant to stand in for the add-ons a typical
Jellyfin server uses for these jobs, so you can run fewer plugins:

| If you use | Turn on in Zorofin | Then |
| --- | --- | --- |
| Home Screen Sections | Home (and Look & player for the web) | Compare Home for a couple of accounts, then uninstall Home Screen Sections |
| Media Bar | Look & player → Home banner | The Zorofin banner steps aside while Media Bar is installed; uninstall Media Bar to see it |
| Playback Reporting | Zorofin activity → Record play history | Import your history first (below), run both for a week, then uninstall Playback Reporting |
| Jellyfin Enhanced: auto season requests, Seerr scans | Requests → Keep Seerr up to date | Turn off Jellyfin Enhanced's versions in the same sitting so seasons aren't requested twice |
| Jellyfin Enhanced: Seerr requests | Requests, and Look & player → Requests hub on the web | Zorofin apps 1.2 and later use Zorofin's Requests. Viewers on older apps lose Requests when you remove Jellyfin Enhanced until they update |
| JavaScript Injector or custom CSS for a theme | Look & player | Remove the old scripts and CSS so nothing is drawn twice |

**Bringing in Playback Reporting's history.** The plugin's source repository
has `tools/import-playback-reporting.py`. Run it on the machine with
`playback_reporting.db`, with a Jellyfin API key, while Record play history is
still **off**:

```sh
export JELLYFIN_API_KEY=…   # Dashboard → API Keys
tools/import-playback-reporting.py --db <jellyfin data>/data/playback_reporting.db \
  --url http://localhost:8096 --dry-run
# the same without --dry-run to send the plays
```

`--time-zone` sets the zone Playback Reporting stored its times in (default:
this machine's). Re-runs skip what is already there, and plays recorded after
the plugin started recording are left out, so nothing counts twice. Then turn Record play history on.

**A safe order:** change one plugin at a time, at a quiet time, and check the
web and an app after each. Every Zorofin switch can be turned off again, and
reinstalling the old plugin restores its old behaviour because its settings
stay in Jellyfin's configuration folder.

**Keep these installed:** TheTVDB, TheSportsDB, Open Subtitles and Intro
Skipper. They are prerequisites, not things Zorofin replaces (see
[Before you start](#before-you-start)).

**Not covered yet.** Media Bar's trailers and some Jellyfin Enhanced extras
(for example its calendar, downloads page, pause screen and ratings). Keep
those for now if you rely on them.

## Update, back up, remove

- **Update the plugin:** stop Jellyfin, put the new DLL in a new
  `Zorofin_<version>` folder (remove the old one), start Jellyfin. Settings are
  kept.
- **Update the companion:** `docker compose pull zorofin-companion` (or rebuild)
  and `docker compose up -d zorofin-companion`. Its data volume is kept.
- **Back up:** the plugin's settings XML and `<jellyfin data>/zorofin/`, plus
  the companion's `secrets/` folder and data volume.
- **Remove:** uninstall from Dashboard → Plugins and restart Jellyfin.
  Optionally delete `<jellyfin data>/zorofin/`. Stop and remove the companion
  container. The apps go back to plain Jellyfin on their next start.
- **Jellyfin won't start after installing:** delete the `Zorofin_*` plugin
  folder and start Jellyfin again.

## Troubleshooting

| What you see | What to check |
| --- | --- |
| No **Zorofin** entry in the Dashboard | The DLL is in its own `Zorofin_0.1.0.0` folder inside `plugins`, Jellyfin was restarted, and Dashboard → Plugins lists Zorofin as Active. Jellyfin's log says why a plugin failed to load |
| Connect says **Not reachable** | The address is how Jellyfin reaches the companion (container name on the same Docker network, or `localhost:9310` with `compose.host-jellyfin.yaml`). The secret matches `secrets/bridge-secret` exactly. `docker compose logs zorofin-companion` shows a stopped or misconfigured companion |
| The companion keeps restarting (exit code 78) | A core setting is wrong: the bridge secret is missing or shorter than 32 characters, the port is taken, or `ZOROFIN_MODULES` names an unknown module |
| A tab says **Needs attention** | Open it: the card says what is missing. A module whose settings are incomplete stays off until they are fixed |
| Requests shows "no Seerr account" for a viewer | Import that Jellyfin user in Seerr |
| Requests fails for everyone | Press **Test** on Connect. Use Seerr's address as Jellyfin's machine reaches it, and a current API key |
| No alerts arrive | The Seerr webhook has **Request Available** ticked, the URL ends in `/v1/hooks/seerr`, and the header is `Bearer <webhook secret>`. Use **Test** on the Alerts tab for the email and ntfy side |
| The web app still looks like plain Jellyfin | **Look & player** is on and saved, the companion is connected, and the browser tab was reloaded. A browser extension that blocks scripts can stop it |
| Rows look doubled on Home | Another plugin or an old Injector script still draws its own rows. Remove it, or switch off that row |
| The Home banner doesn't show | The Media Bar plugin is installed, or the library has no unwatched titles with backdrop art yet |
| Recommended to request is empty | The viewer has fewer than three finished movies, or Seerr isn't connected. In today's apps the row also needs Support turned on |
| The email confirm button says the link isn't complete or opens the wrong site | Set "Address viewers open" on the Connect tab to your Jellyfin address that viewers can reach |
| The companion won't start: network not found | `ZOROFIN_NETWORK` must name a Docker network that exists, usually the one Jellyfin is on (`docker network ls`) |
| Live TV guide has no artwork | Set the companion address for Jellyfin on the Live TV tab, and turn on at least one artwork source |

## Questions

**Do viewers need to set anything up?** No. Apps find the features you turned
on by themselves. Viewers only choose personal things, such as alert channels
and their teams.

**Does the plugin change how Jellyfin streams?** No. Playback, transcoding,
libraries and accounts stay Jellyfin's. The web player only changes the
controls.

**Can I use other Jellyfin apps?** Yes. The plugin adds routes; it doesn't take
any away. Other apps simply don't use them.

**Is anything sent outside my server?** Only what you turn on: Seerr calls go
to your Seerr, alerts to your email service or ntfy, guide artwork lookups to
TVmaze, TheSportsDB and tv-logos, sports art to ESPN and TheSportsDB, and AI
ranking to the AI service you choose. Nothing is sent to Zorofin.

**Where are the technical details?** The plugin [README](../README.md) lists
every route, and the companion's README lists every environment variable.
