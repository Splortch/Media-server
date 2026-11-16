# Title information and explicit playback

Web and desktop library, search and collection cards open title information. The player is not mounted and media bytes/stream URLs are not requested until Play, Resume, a chapter or a bookmark is explicitly activated. Artwork is an authorized metadata image request, not a stream.

The detail view shows available artwork, synopsis, duration, year, genres, cast and file/track details. Missing fields remain unavailable rather than being invented. Desktop uses the same identified-title view as web; identification/correction remains an administrator operation in the web client. Metadata and episode failures do not prevent explicit playback of the current title.

For an identified episode, select a season (including Specials), then an episode to open its information. Watched/resume labels use the signed-in account's progress. **View next episode** opens information for the next accessible season/episode, crossing seasons as necessary. It never starts a stream or automatically marks anything watched. Episode navigation retains the original library/search/collection return target.

Play/Resume fetches fresh progress before playback. **Back to title information** pauses and waits for queued progress saves, then refreshes progress and restores the detail heading. A save failure keeps the player open with its synchronization error. **Back to collection/library** preserves list filters and restores the original card's focus/scroll when still loaded.

The detail heading receives focus on entry and after selecting another episode. Season selection retains its focus; native buttons/selects support keyboard and directional remote activation through the host. Playback moves focus to the custom control surface. Use the same information-first contract in future TV/mobile clients; it does not establish physical TV remote or screen-reader certification.

## Desktop artwork and authorization

Renderer metadata/episode reads pass through the native allowlist. Only GET metadata and GET episode navigation are added; the latter accepts only bounded numeric `season`/`offset` parameters. Metadata writes and arbitrary resource URLs stay unavailable to the desktop renderer.

Artwork uses a nonce-scoped loopback URL for a numeric media ID and fixed `poster`/`backdrop` kind. Native code attaches the session, checks the requesting origin, accepts only JPEG/PNG/WebP and caps the image at 4 MiB. Disconnect/logout invalidates the URL. The renderer never receives a server cookie or fetches a provider's image directly. CSP permits images from the existing loopback bridge.

## Validation

Component and route tests cover information-only entry, season selection, pagination, errors/retry, next-episode selection, explicit playback, focus and list restoration. Server tests cover library/rating boundaries, other-user progress isolation, missing files, specials, cross-season ordering and pagination. Browser acceptance exercises the real server with synthetic local episodes and runs an axe check. Native integration verifies metadata/episode reads, artwork MIME/nonce/origin/logout behavior and the existing stream authorization flow. Physical Windows installers, screen readers and TV remotes still require device testing.
