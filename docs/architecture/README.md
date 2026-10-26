# Architecture

A modular monolith: one server process, one database, no cloud dependency.

| Module | Responsibility | Dependencies |
|---|---|---|
| Core | Domain entities and media abstractions | None |
| Infrastructure | EF Core persistence and Argon2id | Core |
| Media | Path boundaries, FFprobe process adapter, playback planning | Core |
| Services | Bounded background scanning and job orchestration | Core, Infrastructure, Media |
| Api | Composition, authentication, validation, REST, static client, SignalR | All modules |
| frontend | React/TypeScript browser client with same-origin API access | REST contracts |

`IMediaProbe` isolates metadata extraction for tests. `IPlaybackPlanner` currently chooses Direct Play and supplies codec warnings; future implementations can choose HLS/transcoding sessions. They must add isolated output caches and process limits instead of changing the original media file. Do not invoke shells with media filenames.

Scan requests create durable job records and enter a bounded in-memory queue. One worker performs scans serially. Upserts use library ID plus relative path; rescans refresh metadata without duplicating items. Restarts mark unfinished jobs interrupted. Enumeration excludes symlinks/junctions and never writes media. Deleted/moved source reconciliation is deferred.

Streaming resolves a database media ID to a validated path. The client cannot supply filesystem paths to the stream endpoint. ASP.NET Core range processing returns 206/416 responses and seeks a read-only FileStream. No transcoding, HLS, artwork downloads, cloud metadata or filesystem watching runs in this alpha.

Cookies carry a protected identity and session ID. A database session row must still exist and be unexpired on each authenticated request. Progress keys include both user and media ID. The UI exposes one administrator account; future account administration can use existing role and per-user database boundaries.

The API offers `/hubs/status` as an authenticated administrator SignalR endpoint for scan job state changes. The initial web client polls scan status rather than requiring a persistent connection. Job state remains authoritative in SQLite.

## Decisions

- ASP.NET Core/.NET 10 with explicit dependency injection, structured JSON logging and SQLite EF migrations.
- Argon2id via a maintained NuGet adapter; password algorithm parameters encoded with each stored hash. Parameter evolution needs an explicit rehash migration.
- Same-origin cookie sessions and antiforgery protection, not tokens in localStorage or URLs.
- Native browser video controls provide accessible seeking, volume, fullscreen and pause without duplicating platform controls.
- Docker media mounts are read-only; loopback is the default published interface.

Future scaling work: server-side search and cursor pagination, streaming directory enumeration cancellation, filesystem watchers and optional thumbnail workers. Avoid introducing microservices before measuring a concrete bottleneck.
