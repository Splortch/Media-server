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

`IMediaProbe` isolates metadata extraction for tests. `IPlaybackPlanner` reports Direct Play compatibility warnings. Explicit HLS requests use bounded `PlaybackSessions` with isolated output caches, fixed FFmpeg argument lists, encoder capability checks and software fallback. Original media is never changed. See [playback pipeline](PLAYBACK-PIPELINE.md).

Scan requests create durable job records and enter a bounded in-memory queue. One worker performs scans serially. Upserts use library ID plus relative path; rescans refresh metadata without duplicating items. Restarts mark unfinished jobs interrupted. Enumeration excludes symlinks/junctions and never writes media. Opt-in watching and conservative reconciliation track unavailable files; optional hashing supports move preservation and duplicate views.

Streaming resolves a database media ID to a validated path. The client cannot supply filesystem paths to the stream endpoint. ASP.NET Core range processing returns 206/416 responses and seeks a read-only FileStream. HLS resources add session-lease and library authorization on every request; compatible encodes may be reused without sharing access rights. Cloud metadata and artwork retrieval remain separate roadmap work.

Cookies carry a protected identity and session ID. A database session row must still exist and be unexpired on each authenticated request. Progress keys include both user and media ID. Administrator UI supports viewer accounts, library grants, content-rating restrictions and session revocation. Progress uses per-user revision checks to reject conflicting cross-device writes.

The API offers `/hubs/status` as an authenticated administrator SignalR endpoint for scan job state changes. The initial web client polls scan status rather than requiring a persistent connection. Job state remains authoritative in SQLite.

## Decisions

- ASP.NET Core/.NET 10 with explicit dependency injection, structured JSON logging and SQLite EF migrations.
- Argon2id via a maintained NuGet adapter; password algorithm parameters encoded with each stored hash. Parameter evolution needs an explicit rehash migration.
- Same-origin cookie sessions and antiforgery protection, not tokens in localStorage or URLs.
- The shared custom player provides labelled playback controls, keyboard seeking, fullscreen, track/quality selection and buffering diagnostics. Android uses Media3 behind native controls.
- Docker media mounts are read-only; loopback is the default published interface.

Indexed server search, cursor pagination and opt-in filesystem watching are implemented. Further scaling work should follow measured bottlenecks; thumbnail workers and progressive transcoding remain separate follow-ups. Avoid introducing microservices without evidence.
