# Optional features

Core browsing, search, playback, resume, authentication, permissions and security
settings cannot be turned off by a customization preference.

The first registry contains shipped functionality only:

| Key | Default | Personal preference | Server policy |
| --- | --- | --- | --- |
| `smart-collections` | Enabled, shown | Hide/show the library filter and collection settings | Admin may disable new collection creation and collection-filter queries |
| `watch-history` | Shown | Hide/show the navigation entry | No server-wide switch; hiding is not private playback and does not delete or disable watch history |

Use **Settings → Customize**. Reset restores personal visibility defaults only;
it never overrides an administrator's policy. The server policy section is a
single disclosure for administrators, not a list of switches for future modules.
Accounts, libraries, media files and stored progress are unaffected.

## Contract and persistence

Both `/api` and `/api/v1` expose these authenticated routes:

- `GET /features`: each known feature's key, name, `enabled`, `hidden`, effective
  `visible`, and `serverConfigurable`. Preferences belong to the current account;
  no user identifier is accepted from a client.
- `PUT /preferences/features`, `{ "hidden": ["watch-history"] }`: replace the
  current account's hidden-key set atomically. Unknown/duplicate keys are rejected.
- `DELETE /preferences/features`: reset this account's visibility preferences.
- `PUT /features/{key}`, `{ "enabled": false }`: admin-only policy for a key
  explicitly marked server-configurable. Missing flags and unsupported keys fail.

All mutations retain existing CSRF/session protections. Policy and personal
visibility use separate SQLite tables and an additive EF migration. Preferences
therefore survive logout/restart and are shared by devices using the same account.
They do not grant access to another user's collections or restricted media.

When smart collections are disabled, authenticated collection listing returns an
empty array so older clients can still load their library. Collection creation
and collection-filter queries are forbidden; an unfiltered library query still
works. Existing collection records are retained and return after re-enabling.
An explicit owner-authorized DELETE still deletes a collection; toggling the
module never does. Policy reads are not cached across requests.

## Client support

The web client reads capabilities on sign-in and browser focus, and applies its
own changes immediately. Personal hiding removes optional surfaces; it is not an
API authorization boundary. The Customize area distinguishes personal hiding
from an admin-disabled feature. Unknown future keys are ignored, malformed or
unavailable capability responses leave core playback usable, and settings offer
an explicit retry. No imaginary future module is advertised.

The native clients can consume the versioned contract, but do not yet offer the
web Customize editor. Older clients may still display an action; the server
continues to enforce disabled-module write/query restrictions. Existing desktop
and Android core browsing/playback do not depend on this capability endpoint.

Add a registry entry and enforcement/tests when a future module actually ships.
Do not gate security checks behind optional flags. Activity/requests/profiles and
other roadmap items keep their own authorization, privacy and retention rules.
