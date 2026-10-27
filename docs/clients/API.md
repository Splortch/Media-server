# Client API v1

The versioned API is `/api/v1`. Existing `/api` browser endpoints remain available. Versioned and legacy routes share handlers, authorization, cookies, rate limits and CSRF validation; there is no weaker native-login endpoint. The default `/openapi/v1.json` remains administrator-protected. Client-specific schema is `/openapi/client-v1.json`, also protected.

## Connection and authentication

1. Validate an origin-only absolute URL: HTTPS remotely; HTTP only on the client's own loopback interface. No embedded username/password, query, fragment or path prefix is accepted by the Windows alpha. Subpath proxy deployments are not yet supported.
2. GET `/api/v1/server-info` without credentials. Require `apiVersion=1` and `minimumClientApiVersion<=1`; inspect `setupRequired`. Configure the server through its web interface first if required.
3. GET `/api/v1/auth/csrf` and retain its antiforgery cookie. POST `/api/v1/auth/login` with `{name,password,deviceName}` and `X-CSRF-TOKEN`.
4. Preserve server cookies, but do not expose them to the renderer or logs. Re-fetch antiforgery tokens before writes, particularly after login.
5. GET `/api/v1/auth/me` to validate restored sessions. A 401 expires local identity; 403 is permission denial, 409 a synchronization conflict and 429 throttling. Do not repeatedly retry rejected credentials.
6. POST `/api/v1/auth/logout` with CSRF, clear local credentials even if disconnected, and tell the user if remote revocation could not be confirmed.

Only the compatibility/CSRF/setup-check routes are public. Media, ranges, progress, sessions and reports require authentication. GET `/api/v1/sessions` lists only the current user's sessions, with hashed management handles rather than reusable credentials. DELETE `/api/v1/sessions/{handle}` revokes one of that user's sessions. Device names are bounded; current-session expiry is authoritative on the server.

## Media and playback

- GET `/api/v1/media?search=title&libraryId=1&offset=0&limit=200`: bounded, deterministic title/ID ordering. Basic server-side case-folded search; advanced Unicode/full-text filtering remains roadmap work.
- GET `/api/v1/media/{id}`: filename-derived technical details and a playback plan. Movie/series metadata identification is still issue #10; the client does not invent that information.
- GET `/api/v1/media/{id}/tracks`: FFprobe track index/type/codec/language/title after scanning. `canSelectAudio` and `canSelectSubtitles` are **false** until the server can honor track choices. Rescan pre-existing media to populate track data.
- GET/HEAD `/api/v1/media/{id}/stream`: authenticated Direct Play, including ranges. No token-bearing public playback URL is issued. Native transports must attach cookies to every request and preserve authorization on redirects (the Windows alpha disallows redirects entirely).
- GET `/api/v1/media/{id}/progress`, PUT `/api/v1/media/{id}/progress-sync` and GET `/api/v1/continue-watching`: see [synchronization](SYNC.md).
- PUT `/api/v1/capabilities`: bounded platform/player/container/codec report stored with the current session. These are client observations, not proof of hardware codec support or a transcoding negotiation promise.
- POST `/api/v1/media/{id}/playback-errors`: bounded enum `network|decode|unsupported|unknown` plus a bounded client name. Maximum 100 diagnostic reports retained per user; no raw paths, URLs, cookies, stack traces or free-form user text is logged.

No HLS/transcoding/download authorization exists yet. Clients must not synthesize unsupported media URLs or start offline downloads. No LAN port scanning or automatic service trust is performed; manual connection plus remembered server discovery works now. Future mDNS/Bonjour hints require TLS identity confirmation and local-network permissions.

## Generated contract workflow

```bash
# From the repository root; tests create an isolated fixture account, never using real credentials.
EXPORT_CLIENT_OPENAPI="$PWD/shared/api/openapi-v1.json" dotnet test MediaServer.sln --filter FullyQualifiedName~ClientOpenApiMatchesTheRunningServer
npm ci --prefix shared/api
npm run generate --prefix shared/api
```

Commit both the OpenAPI snapshot and generated TypeScript when schemas change. CI re-exports from the actual authenticated server and checks for drift. TypeScript consumers use generated schemas; Rust deliberately uses a scoped transport over bounded JSON and exposes only allowlisted operations, with typed response checks in the shared client. A future Android PR can generate Kotlin DTOs from the same contract without copying backend entities.

API major versions are independent of server/client product versions. Additive fields may be ignored; breaking semantics require a new version while retaining the supported old version. Wire numbers are strict JSON numbers; malformed numeric strings receive 400.
