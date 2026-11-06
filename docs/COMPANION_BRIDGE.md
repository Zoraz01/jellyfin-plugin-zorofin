# Companion bridge, version 1

This is the public HTTP contract between the Zorofin Jellyfin plugin and an
optional companion service. The plugin works without a companion. Any program
that implements this contract can act as one; the plugin shares no code with it.

## Roles

- **Plugin:** checks the viewer's Jellyfin session, then calls the companion on
  the viewer's behalf. It never forwards the viewer's Jellyfin token.
- **Companion:** trusts the viewer identity only when the request carries a
  valid signature made with the shared secret.

The operator sets the same secret (at least 32 characters) in the plugin's
dashboard page and in the companion.

## Companion routes the plugin calls

| Route | Viewer | Purpose |
| --- | --- | --- |
| `GET /v1/capabilities` | none | Returns `{ "schemaVersion": 1, "features": { "<module>": { "version": n, "enabled": bool } } }` |
| `GET` or `POST /v1/modules/{module}/{path}?{query}` | required | Module routes, JSON in and out |
| `GET /v1/admin/settings` | administrator | Settings sections for the Dashboard: `{ "schemaVersion": 1, "sections": [...] }` |
| `POST /v1/admin/settings/{section}` | administrator | Saves one section (`{ "enabled"?: bool, "values": { key: value or null } }`); returns the section, or 4xx with `{ "error" }` |
| `POST /v1/admin/settings/{section}/test` | administrator | Same body; returns `{ "ok": bool, "checks": [{ "label", "ok", "detail" }] }` without saving |

The plugin:

- asks for capabilities with a 2-second timeout and caches the answer for 60
  seconds. If the companion is down, its features are left out of the manifest;
  plugin features are unaffected;
- merges companion features into `GET /Zorofin/Capabilities`. A companion
  feature can never replace a feature the plugin provides;
- forwards `GET`/`POST /Zorofin/Companion/{module}/{path}` only when `{module}`
  is an enabled companion feature. Request bodies are limited to 64 KB and must
  be JSON; responses are limited to 4 MB.

- forwards the settings routes from `/Zorofin/Admin/Companion/Settings...` only
  for Jellyfin administrators, with the administrator's id and
  `X-Zorofin-User-Admin: 1`. The companion refuses settings calls from anyone
  else with 403. Section ids are `connections` or a module id.

Module ids match `^[a-z][a-zA-Z0-9]{0,31}$`.

## Signed headers

| Header | Value |
| --- | --- |
| `X-Zorofin-Timestamp` | Unix time in seconds |
| `X-Zorofin-Nonce` | 16 random bytes, 32 lowercase hex characters, new for every request |
| `X-Zorofin-User` | Viewer's Jellyfin user id, 32 lowercase hex characters without dashes; empty for plugin-level calls |
| `X-Zorofin-User-Admin` | `1` if the viewer is a Jellyfin administrator, otherwise `0` (always `0` without a viewer) |
| `X-Zorofin-Signature` | Lowercase hex HMAC-SHA256 of the canonical string with the shared secret |

Canonical string, lines joined with `\n` and no trailing newline:

```
zorofin-bridge-v1
{METHOD, uppercase}
{path and query exactly as sent, e.g. /v1/modules/recommendations/rows?limit=10}
{timestamp}
{nonce}
{user id or empty}
{0 or 1}
{lowercase hex SHA-256 of the raw body; the empty-body hash when there is none}
```

The companion rejects a request with status 401 when the signature does not
match, the timestamp is more than 60 seconds away from its clock, the nonce was
already used within the window, or a header is malformed.

## Test vectors

Secret: `zorofin-test-secret-0123456789abcdef`

| | Viewer call | Plugin-level call |
| --- | --- | --- |
| Method | `POST` | `GET` |
| Path and query | `/v1/modules/recommendations/rows?limit=10` | `/v1/capabilities` |
| Timestamp | `1793232000` | `1793232000` |
| Nonce | `00112233445566778899aabbccddeeff` | `ffeeddccbbaa99887766554433221100` |
| User | `4f1c2d3e4a5b6c7d8e9f0a1b2c3d4e5f` | empty |
| Admin | `0` | `0` |
| Body | `{"seed":"abc"}` | empty |
| Body SHA-256 | `c08af8e316ceedb4139c26c2c1cb1bafe46aced46a611c97b0cdd69d15df6709` | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| Signature | `726ef4520137fb3f18099e1e3c84f8579b1ff2a6c9fcbda68a6a1cc718254a0d` | `69898191872a06870b424cc2aabe76e7cd18600d85224a389a40cfc746edb71c` |

Check with OpenSSL:

```sh
printf 'zorofin-bridge-v1\nPOST\n/v1/modules/recommendations/rows?limit=10\n1793232000\n00112233445566778899aabbccddeeff\n4f1c2d3e4a5b6c7d8e9f0a1b2c3d4e5f\n0\nc08af8e316ceedb4139c26c2c1cb1bafe46aced46a611c97b0cdd69d15df6709' \
  | openssl dgst -sha256 -hmac 'zorofin-test-secret-0123456789abcdef'
```

## Changes

A breaking change gets a new scheme name (`zorofin-bridge-v2`) and new route
prefix (`/v2/`). Companions should keep serving the previous version for at
least one plugin release.
