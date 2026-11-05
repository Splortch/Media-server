# Movie and TV identification

The web title information page offers **Identify or correct title** to administrators. Metadata is stored in SQLite; JPEG/PNG/WebP posters and backdrops are copied into the server data directory. Ordinary title reads, scans and playback never contact a provider. Existing filenames and playback continue to work without identification, an account, a provider key or network access. Native clients retain their existing title presentation; this first editor is in the web client.

## Local library files and manual corrections

Choose **Read local NFO and artwork** to import the adjacent file with the same stem (`Movie.nfo` beside `Movie.webm`). Supported roots are `movie`, `tvshow` and `episodedetails`; supported fields are title, year, plot, genre, actor/name, showtitle, season and episode. TV episodes can carry series, season and episode information. XML DTDs/entities are prohibited; documents are limited to 256 Ki characters. NFO links, image URLs and arbitrary file paths are ignored.

Artwork uses fixed adjacent names: `<stem>-poster.jpg`, `<stem>-poster.png`, `poster.jpg`, `poster.png`, and the corresponding `<stem>-fanart` / `fanart` names. Symbolic links and junctions are rejected using the existing library path policy. Original files are never changed. A failed read preserves existing metadata.

Edit title, year, synopsis, genres, cast and series/episode fields and choose **Save title details**. Explicit titles survive rescans; file properties continue to refresh. Updates require the current revision, so another administrator's newer correction returns HTTP 409 instead of being overwritten. Reload the title after a conflict. Identification never changes content ratings, grants or import approval. Provider attribution is retained when editing text with provider artwork.

## Optional TMDB provider

An operator must review and comply with the [TMDB API terms](https://www.themoviedb.org/api-terms-of-use), obtain the appropriate permission for their use, and supply an application **API Read Access Token** through server configuration:

```text
Metadata__Tmdb__Enabled=true
Metadata__Tmdb__ReadAccessToken=<private application read access token>
```

Use environment variables or a private server configuration file outside the repository. The token is never returned by the capabilities endpoint or embedded in frontend assets, artwork URLs or provider URL queries. Do not put it in frontend configuration. The provider is off by default. The `IMetadataProvider` interface permits replacing the implementation without changing stored metadata or requiring a cloud account.

In the admin editor, enter a search string and choose **Search provider**. Only this text and media type are sent; no automatic library upload, filename search, playback history or background matching occurs. Select a movie or TV result to explicitly apply it. For an episode, provide both season and episode; leave both blank for series metadata. The selected kind must match the library kind. Incorrect matches can be replaced or manually corrected.

Requests use the application Bearer token only with `api.themoviedb.org`; image requests use `image.tmdb.org` without the token. Transport uses the existing exact-host HTTPS, public-IP/DNS, no-proxy, no-redirect policy. JSON responses are limited to 1 MiB, artwork to 4 MiB and each request to 20 seconds. API calls are serialized at no more than one per second, bounded search results are cached for five minutes, and HTTP 429 triggers a 60–300 second backoff. No automatic retry storm occurs. Search and match failures leave stored metadata intact.

The metadata credits area displays the approved TMDB logo and required notice, including when cached TMDB artwork is shown after disabling the provider. The bundled logo comes from [TMDB's official attribution assets](https://www.themoviedb.org/about/logos-attribution); it remains TMDB's trademark. Provider data/artwork remains subject to its owners' rights and terms. Review terms before redistribution or commercial use. See official [application authentication](https://developer.themoviedb.org/docs/authentication-application), [rate limiting](https://developer.themoviedb.org/docs/rate-limiting), and [attribution FAQ](https://developer.themoviedb.org/docs/faq).

## Cache, access and API

Artwork lives at `<server data>/artwork-cache`, under content-addressed filenames. Files are capped at 4 MiB each, 128 MiB total and 1,024 entries. MIME must match JPEG, PNG or WebP signatures; SVG/HTML and mismatched provider MIME are rejected. Copies are atomic, never overwrite existing media, and are served with `nosniff`. Cache limits include unreferenced files from failed or replaced matches. When full, matching fails without replacing existing metadata. To reclaim space, stop the server, back up the data directory and remove unreferenced cache files; automatic eviction is deliberately absent so offline artwork is not silently lost. Removing referenced files leaves text usable and artwork returns 404 until reimported.

The built-in backup stores the metadata database but does not include artwork-cache files. Copy this bounded directory separately while the server is stopped if artwork must survive an offline restore.

Authenticated users can read `/api/media/{id}/metadata` and `/api/media/{id}/artwork/{poster|backdrop}` only when they can access the underlying media item, including library and rating restrictions. Unknown or restricted items return 404. Admin-only endpoints are `/api/metadata/providers`, `POST /api/metadata/search`, `POST /api/media/{id}/metadata/local`, `POST /api/media/{id}/metadata/tmdb`, and `PUT /api/media/{id}/metadata`. Mutations retain CSRF protection. Equivalent `/api/v1` routes are in the client contract. No local source path, token or raw provider exception is returned.

Automated tests cover local NFO and cached images, malformed XML, restricted users, stale revisions, manual corrections, size/MIME/link rejection, provider search caching, fixed request hosts, credential separation and rate-limit backoff. Provider tests use controlled responses; live TMDB matching requires an operator key and is not claimed as verified against a real account.
