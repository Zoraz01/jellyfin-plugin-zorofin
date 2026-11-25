# Changelog

All notable changes to the Zorofin Jellyfin plugin. Versions follow semantic
versioning: minor for new features, patch for fixes. Jellyfin needs four parts,
so release `0.2.0` is `0.2.0.0` in the manifest, the DLL and the git tag.

## 0.2.0 (November 25, 2026)

First public release, installable from the plugin repository manifest.

- Home rows refresh from 1.5: a per-viewer cache so Home opens faster, and
  rows driven by TMDB and your library.
- Sports: the "Your teams" row only appears when one of your favorite teams is
  playing now or within the hour, and its cards carry the matchup and both
  team crests.
- Playback problem reports: the apps can send a report (with the stream and
  device details) to the Support tab.
- Notification read state is kept per account, so reading a notification on
  one device clears it on the others.
- Catch Up: Like or Skip, sharing taste with recommendations.
- Plays that end without a matching stop report are still saved to play history.
- "Recommended to request" titles picked by AI (optional, your own key) and by
  what viewers actually watch.

## 0.1.0 (November 6, 2026)

Source published; no installable release.
