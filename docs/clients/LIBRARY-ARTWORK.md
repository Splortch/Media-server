# Library artwork, series and playback completion

Web and desktop library/search cards now use imported posters. If there is no poster, the client requests a generated JPEG frame; a failed/missing image falls back to the existing letter cover. Images load lazily, use the same media authorization rules and do not mount a video player. Continue Watching uses the same artwork component.

## Managing images

An administrator can open a title and expand **Manage artwork** in the web client. Choose Poster or Backdrop, upload a JPEG/PNG/WebP up to 4 MiB, select current/local artwork, use a generated frame, or remove the selected image. An image change needs the current metadata revision; conflicts preserve the existing image. Local candidates use only the existing fixed adjacent poster/fanart filenames and the no-link path policy. Up to twelve previously selected images are retained per title as gallery options, including its current poster/backdrop; other titles’ cached images are not exposed. Curated images remain in the artwork cache; original files are never rewritten. Provider matching and local NFO import remain available in the existing identification editor.

A removed poster returns to generated-frame/letter fallback. Desktop reads the same saved images through the authenticated nonce-scoped native bridge. Image management remains in the web administrator interface.

## Grouped TV libraries

`GET /api/v1/catalog` (also under `/api`) returns one card per explicitly identified series **within each library**, plus individual movie/unidentified-file cards. Grouping happens before pagination, after library/rating/missing/import filters. Episode counts include only accessible matching entries. Series searches use Unicode case folding; collection prefix rules cannot be broadened. `prefix`, `libraryId`, `videoCodec`, `collectionId`, `limit` and cursor/offset pagination are supported. Pages contain at most 200 cards; offsets are bounded at 100000. Filter-scoped cursors cannot be reused for another search.

A series card opens a series overview with seasons and episodes, without Play/Resume or a player. Selecting an episode opens its own information and playback actions. Filename guesses never group unknown videos. Exact saved series names identify a group, so separate same-named remakes using distinct names. Continue Watching retains individual episodes.

## Watch state and Continue Watching

Signed-in non-guest users can mark a title watched/unwatched or remove it from Continue Watching in the title's **Watch state** section. Each action requires confirmation and returns an Undo action. Removing a title preserves its resume position; starting playback again makes it eligible for Continue Watching. Marking unwatched resets the position. Revisions protect writes and Undo from changes made on another device. No other account's history or position changes.

`GET/POST /api/v1/media/{id}/watch-state` returns/changes the current user's state. POST accepts `watched`, `unwatched`, `hide` or a revision-checked `restore` snapshot. The database adds a default-false Continue Watching visibility field. Existing progress is preserved by the migration.

## Seek previews and next episode

Hover over the seek bar or focus/use it with the keyboard to display a debounced image preview and timestamp. Frames are sampled in buckets (at least ten seconds, approximately 120 buckets per title) so previews are approximate and repeated movement reuses generated output. Image errors never stop playback.

Automatic next-episode playback is **off by default**. The player offers an opt-in checkbox when another accessible episode exists. The preference is saved locally by account and server. On completion, opted-in playback shows a ten-second countdown with Cancel and Play next controls, including in fullscreen. Without opt-in, only the explicit Play next action is offered. Leaving playback cancels the countdown, and progress writes finish before advancing. Next follows actual accessible season/episode numbers; it is not a recommendation algorithm or automatic season completion. Spoiler preferences and season catch-up are documented in [EPISODE-TRACKING.md](EPISODE-TRACKING.md).

## Generated-image limits

Generated frames live separately in `<data>/preview-cache`: one generation at a time, a 40-second request deadline, one-MiB JPEG output limit, and a 256-MiB/4096-image FIFO cache. Thumbnail requests are limited to 200 per account per minute. Stale job directories are cleaned before generating another image. Cache keys include media identity/scan fingerprint fields; rescanning changed media invalidates its derivatives. Imported artwork is never evicted by this cache.

Linux generation reads an already validated pinned descriptor. Windows/macOS generation stages at most 8 GiB into one private job directory and removes it after FFmpeg exits. Seek previews cover positions up to six hours. FFmpeg/FFprobe and the existing HDR tone-mapping filters must be installed; source color is freshly probed and HDR frames convert to SDR. Missing tools, unsupported formats, quota/time limits and oversized staging sources leave the poster/letter or timestamp fallback usable. Generated images are convenience derivatives, not reference-color certification.

All image/metadata endpoints authorize the media item on each request. The native bridge accepts fixed poster/backdrop/thumbnail kinds and bounded numeric preview positions; it never accepts renderer-provided remote URLs or exposes server cookies. Uploaded/curated images stay subject to their owners' rights.
