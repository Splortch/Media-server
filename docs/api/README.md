# API

OpenAPI JSON: `/openapi/v1.json` (administrator session required). Browser client calls same-origin `/api`. APIs use JSON, ordinary HTTP status codes and Problem Details where relevant. No API keys are needed.

| Method | Endpoint | Access / behavior |
|---|---|---|
| GET | `/health` | Public liveness only; no sensitive details |
| GET | `/api/setup` | Public; whether initial administrator is required |
| GET | `/api/auth/csrf` | Public; returns token and sets antiforgery cookie |
| POST | `/api/setup` | Setup token + CSRF; `{name,password,token}`; first account only |
| POST | `/api/auth/login` | CSRF; `{name,password}`; sets session cookie |
| POST | `/api/auth/logout` | Auth + CSRF; revokes current session |
| GET | `/api/auth/me` | Current authenticated identity and role |
| GET | `/api/status` | Authenticated server version and counts |
| GET/POST | `/api/libraries` | Admin; create uses `{name,path}` |
| POST | `/api/libraries/{id}/scan` | Admin + CSRF; 202 with scan ID |
| GET | `/api/scans/{id}` | Admin; queued/running/completed/failed/interrupted/rejected, counts |
| GET | `/api/media?offset=0&limit=100` | Auth; deterministic title ordering, max 200 per request |
| GET | `/api/media/{id}` | Auth; details and Direct Play plan/warnings |
| GET/HEAD | `/api/media/{id}/stream` | Auth; byte ranges, 206 and 416 support |
| GET | `/api/progress` | Current user's watch positions |
| PUT | `/api/media/{id}/progress` | Auth + CSRF; `{seconds}`; finite and within known duration |

Every write requires `X-CSRF-TOKEN` from `/api/auth/csrf` alongside that response's cookie. Refresh the token after login/logout because the authenticated identity changed. Cookie authentication avoids access tokens in stream URLs. Login/setup share a limit of ten requests per minute per source IP.

Stream paths never accept a filesystem path. Media list/detail payloads exclude source paths; only administrators can inspect configured library paths. SignalR `/hubs/status` sends `scanUpdated` events to authenticated administrators; REST scan polling remains available.

400: invalid data/CSRF. 401: login required or rejected credentials/token. 403: role denied. 404: unknown media or source unavailable. 409: setup/library/scan conflict. 429: rate limited. 503: scan queue full. Unexpected errors return a trace ID and a generic message.


## Hardening and diagnostics additions

These endpoints exist under both /api and /api/v1; the versioned OpenAPI/TypeScript contract is regenerated.

| Method | Path (after prefix) | Behavior |
| --- | --- | --- |
| POST | /auth/password | Auth + CSRF + sensitive-endpoint limiter; currentPassword and newPassword (12–256 characters). Transactionally replaces the hash and revokes every session for this user, including the caller. Returns 204; sign in again. A wrong current password returns 400 without expiring a valid session. |
| GET | /media/{id}/playback-info | Auth; available, bounded code, correlationId and the existing playback plan. Does not return a filesystem path. Not a codec or decode success guarantee. |
| GET | /scans/{id}/issues | Admin; up to 100 sanitized fileName/code/correlationId entries; no raw exception or FFprobe output. The scan count includes failures beyond the display cap. |

Setup now consumes/deletes its token on successful creation. Configured startup does not regenerate it.
Use local operator recovery for a forgotten administrator password; there is no token-based remote reset API.
