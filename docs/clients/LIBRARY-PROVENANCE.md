# Library explanations and wrong-match reports

Issue #53 adds a collapsed **Why is this here?** section to the shared web/desktop title information page. Opening it loads an explanation for an authorized item only. It names the library, identifies a filename-derived title, embedded file metadata, local NFO metadata, manual correction, configured TMDB match, or an administrator's import selection. No online metadata lookup or telemetry is needed. Provider credentials, absolute/relative paths, fingerprints and scan diagnostics are absent from the public response.

New entries record when first discovered/reserved in the catalog, and each successful scan records its time. Rescans and detected moves retain the original discovery time. Existing entries migrate with unknown dates, displayed as **Not recorded for this item**; a later rescan records only its successful scan time. No backdated date is inferred from the source filesystem timestamp.

## Reporting and review

An authenticated non-guest account can report a wrong title, artwork, or series/episode match with an optional note of up to 500 characters. Notes go only to administrators and their author. Only one report row exists per account/title. Concurrent duplicate submissions return the same open report and preserve its original note. A reviewed report can reopen after five minutes; its revision advances. Requests are limited to ten/account/minute and 200 open reports/account. Missing, importing, age-restricted and non-granted titles cannot be reported. Item access and deduplication are checked in the same database transaction.

Administrators can open **Settings → Administration → Metadata match reports**, or expand **Administrator investigation** in the title explanation. The queue pages through 50 reports at a time and optionally includes reviewed reports. Investigation exposes the validated exact file, relative path, current missing/import status, latest library scan state, scan fingerprint, and saved provider/reference/revision. If the current path cannot pass the normal allowed-root/no-link policy, it is unavailable rather than opened. Admin diagnostics can investigate missing entries; this does not grant ordinary users access to them.

**Correct this title's metadata** opens the existing manual identification editor on the title page. Report review never guesses or applies a match automatically. The administrator can then explicitly resolve or dismiss the report. Revision checks reject concurrent/stale reviews. The reporter sees their own current status on reopening the explanation; other users' reports are never included. Original media bytes and filenames are unchanged.

## Optional feature and clients

`library-provenance` is enabled by default. Administrators can disable it in server feature settings; individuals can hide it using Customize. Disabling/hiding returns no public explanation and blocks new reports, preserving saved report data and core playback. Administrator review is blocked while the server feature is disabled. Guests can read an authorized plain-language explanation but cannot report or inspect private diagnostics.

The web client has the administrative queue/editor. Desktop uses the authenticated native bridge for public explanations and report creation; the renderer cannot request admin diagnostics or review endpoints. Android/iOS have not adopted this UI. The native allowlist permits only the fixed public routes, without arbitrary query parameters.

## API and schema

All routes exist under `/api` and `/api/v1` and require authentication; writes require normal CSRF protection.

| Route | Access and behavior |
| --- | --- |
| `GET /media/{id}/provenance` | Authorized plain-language explanation, dates and this account's own report; 204 if hidden/disabled |
| `POST /media/{id}/match-report` | Non-guest; `{kind: "title" | "artwork" | "episode", note?}`; deduplicated report response |
| `GET /admin/media/{id}/provenance` | Admin-only private file/matching/scan context |
| `GET /admin/match-reports` | Admin-only; `mediaId?`, `offset?` (0–100000), `closed?`; bounded page and next offset |
| `POST /admin/match-reports/{id}/review` | Admin-only; `{state: "Resolved" | "Dismissed", expectedRevision}`; stale reviews return 409 |

The additive migration adds nullable `Items.FirstSeenAt`/`LastScannedAt` and `MatchReports`, indexed by user/title and state/ID. Reports are deleted with their owning account or catalog item. Feature switches retain data. The versioned contract and generated TypeScript models are updated.

Automated tests cover private diagnostics, library/rating/missing/import permissions, duplicate requests, user isolation, review conflicts/cooldown, guest behavior, feature disable/visibility, scan dates, lazy UI loading and safe rendering of notes. The real browser flow covers report creation, the admin queue, file inspection and opening manual correction, while asserting unchanged source bytes. Physical screen-reader and installer validation remain separate.
