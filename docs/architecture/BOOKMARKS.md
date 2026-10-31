# Private playback bookmarks and notes

Issue #46 adds bookmarks in the web and shared desktop UI. On a title's details page or below playback controls, expand **Private bookmarks and notes**. Enter a name, timestamp in seconds and optional private note. In playback, **Use current position** copies the browser's current media time. Use zero for a general title note. **Edit**, **Save bookmark**, **Delete** and named jump buttons work with keyboard controls and native labels. Notes render as literal text, not HTML or subtitle markup.

Selecting a bookmark from details explicitly opens playback at that time; viewing or managing notes never loads media bytes. During playback it seeks the current stream. Bookmark positions are elapsed seconds from the current finite stream's first seekable position. The player subtracts that position when capturing a bookmark and adds the current stream origin when jumping or starting from details. It does not add raw FFprobe/container timestamps. This also handles a nonzero browser timeline; tests cover a stream spanning 100–130 seconds with a bookmark 10 seconds into the content. Existing playback progress remains separate. Live/sliding playlists are not supported. Unknown duration is rejected, and initial seeking clamps to the current stream duration. Changing editions/cuts is not a timeline conversion: import requires the exact content hash.

## Privacy and storage

SQLite `Bookmarks` records have an owning account, media FK, UUID, position, name, note and edit revision. Every operation checks current media authorization and explicitly scopes records to the authenticated account. Administrator status does not grant access to another user's notes. Guests cannot use these routes. The normal CSRF/authentication/session boundaries remain enabled. Mutations are serialized across the two API aliases; edits/deletions reject stale revisions. Removing media/account records cascades bookmark deletion. No originals, sidecars, posters or public metadata feeds are modified.

Names are bounded to 80 characters, notes to 500, with 15 bookmarks per title and 1500 per account. These bounds keep even JSON-escaped export/import within the server's existing 64 KB request limit. No unbounded filesystem reads, external requests or background hashing is introduced by bookmarks. Backups retain the database records.

## Portable transfer

1. Ask an administrator to enable **Hash LIBRARY files** in the library options and rescan if it has no fingerprint. Existing scanner hashing reads the complete file using its validated file handle. Rescan after editing/replacing a source; hashes describe the last scanned bytes.
2. Expand **Portable export and import**, then **Export bookmarks**. Copy the JSON to a file you control. It contains private notes; treat it as personal data.
3. On the target server, scan the exact same file with full content hashing enabled. Open its details and paste the JSON into **Portable bookmark JSON**, then **Import bookmarks**.

Version 1 exports contain only a version, `sha256:` content key and bookmark UUID/time/name/note records. No local numeric media IDs, library names, user IDs, device IDs, tokens or filesystem paths are exported. The target title is chosen explicitly in the UI/route. Import rejects a different file hash, malformed or duplicate IDs, out-of-range timestamps, oversized input and unsupported versions. Existing identical UUID records are skipped; conflicting records abort the entire import without overwriting notes. Reimport is idempotent. This permits transfer between installations with different database IDs and directory layouts. Files without content hashes remain usable for local bookmarks, but portable transfer reports how to enable hashing rather than guessing identity from filenames.

## API

Both `/api` and versioned `/api/v1` expose the following authenticated routes. The OpenAPI contract and generated TypeScript types describe their request and response schemas.

| Method | Path under prefix | Behavior |
| --- | --- | --- |
| GET | `/media/{id}/bookmarks` | Current account's ordered bookmark list |
| POST | `/media/{id}/bookmarks` | Create with `seconds`, `name`, `note` |
| PUT | `/media/{id}/bookmarks/{bookmark}` | Edit with `expectedRevision` |
| DELETE | `/media/{id}/bookmarks/{bookmark}/{revision}` | Delete only the supplied current revision |
| GET | `/media/{id}/bookmarks/export` | Private versioned content-hash archive |
| POST | `/media/{id}/bookmarks/import` | Validate and atomically add an archive |

Missing/inaccessible media and foreign bookmarks return 404, stale edits/import conflicts return 409, invalid content returns 400. JSON must fit the existing 64 KB request limit. The native renderer allowlist exposes only these exact bookmark route shapes and methods; arbitrary paths and query strings remain rejected.

## Validation and limits

API tests cover authentication, cross-account privacy, live media permissions, stale revisions, portable identity, idempotence, invalid atomic imports, bounds and CSRF. Guest regressions require denial. Shared component tests cover management, focus, literal note rendering, current-position capture and failed import. The real-backend browser flow adds keyboard-operated creation/jumping/edit/deletion and unsuppressed axe checks. A player regression covers near-end timestamps previously skipped by the resume guard.

Android can use the same API contract, but its native bookmark UI is not included in this increment. No physical screen-reader, Android/TV or Windows device testing is claimed; automated semantics and real browser coverage are distinct from that testing. Exact manual positions may land on nearby decodable frames depending on the browser/codec. Bitmap-subtitle and native-player limitations are unchanged.
