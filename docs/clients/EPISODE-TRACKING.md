# Spoiler-safe episodes and season catch-up

Issue #45 adds per-account episode tracking to the existing shared web/desktop information pages. It does not require online metadata and never changes media files.

## Browsing and spoilers

Identified series show the selected season's accessible episode count and watched count. **View next unwatched** opens information for the first accessible incomplete regular episode, ordered by the saved season number, episode number and media ID. It never starts playback. Specials have their own next-unwatched selection when viewing season zero; they are not inserted ahead of season one. Missing, pending-import, restricted-library and age-restricted episodes are excluded. Gaps are preserved, and separate libraries or exact series names remain separate groups. Existing **View next episode** continues to mean the next numbered episode after the current one, regardless of completion.

By default, unseen episode summaries and poster/backdrop/generated-frame artwork are hidden in the shared clients, including library cards. An episode becomes visible after this account starts or completes it. This conservative rule also hides unseen episodes before a later watched episode; it does not assume that watching episode five means episodes one through four were watched. It applies to specials too. Movies and unidentified videos retain their existing presentation.

**Reveal summaries and artwork for unseen episodes**, in the seasons/episodes section, is an explicit account-wide preference saved on the server. Turning it off restores the conservative default. Guests keep the default and cannot change account preferences or watch state. Existing single-account installations migrate with spoilers hidden. The client hides content while its metadata request is pending so embedded descriptions cannot flash before the preference arrives.

This is a presentation preference, not a media access restriction. Authorized users can still play an unseen episode and inspect raw metadata/artwork APIs. Other clients must honor `spoilerHidden` to implement the same presentation. The Android/iOS presentation of this new flag has not been added in this change.

## Watch changes and Undo

Individual episodes retain the existing confirmed watched/unwatched actions and revision-checked Undo. **Mark season watched/unwatched** explicitly confirms the count of accessible episodes affected. Watched sets completion and the end position; unwatched resets completion and the position. Both clear removal from Continue Watching. The operation is atomic and scoped to the requesting account, exact series and library. Missing or restricted episodes retain their saved state.

Season Undo restores each episode's prior position, completion and Continue Watching visibility. Only the latest season operation per account is retained, for ten minutes. The server stores the snapshot; the client receives a random account-bound token instead of submitting arbitrary history. Undo is single-use. A newer progress write, metadata correction, membership/access change, or expired token produces a conflict rather than overwriting newer state. An episode with no previous progress is restored to an unwatched zero position with a new revision, preserving conflict detection.

Season changes are bounded to 2,000 accessible files per season and 20 requests per account/minute. Larger seasons still browse/count normally, but bulk controls are disabled. The snapshot is bounded to one row per account; expired rows are removed when another season operation succeeds. Guests cannot perform season changes. No other account's state is changed.

## API and storage

The routes exist under both `/api` and `/api/v1`:

- `GET /media/{id}/episodes` adds `nextUnwatched` and `seasonProgress` (`season`, `total`, `watched`, `revision`). The opaque revision covers the complete accessible season, independently of the paginated episode list.
- `POST /media/{id}/season-watch-state` accepts `{season, action: "watched" | "unwatched", expectedRevision}` and returns `{progress, undoToken}`. Undo accepts `{season, action: "undo", undoToken}` on the same anchor title.
- `GET/PUT /preferences/episodes` reads/saves `{revealSpoilers}` for the authenticated account.
- Metadata and image-option responses add `spoilerHidden`; hidden image options have null poster and thumbnail URLs so shared cards never request generated spoiler frames.

An additive migration adds `Users.RevealEpisodeSpoilers` (false) and the account-owned `SeasonWatchUndos` table. Existing progress is preserved. The desktop renderer allowlist permits only these fixed preference and season-action routes; arbitrary metadata writes remain unavailable.

## Verification

Backend regressions cover both API aliases, per-account progress, restricted/missing episodes, specials, undo replay/cross-account rejection and stale-progress conflicts. Shared UI tests cover confirmation, server-owned Undo tokens, conflicts, preference changes and hidden poster/backdrop/embedded summaries. The browser flow exercises default hiding, explicit reveal, season catch-up/Undo, keyboard navigation and explicit playback. Native integration checks the default image suppression and authenticated preference transport. Passing automated checks do not imply physical TV remote, screen-reader or installer verification.
