# Prioritized roadmap

The source backlog is [roadmap.json](roadmap.json). The Roadmap workflow creates labels, milestones and issues idempotently by title; GitHub Issues are the working board. It does not create hundreds of speculative tickets.

Recommended next five tasks:

1. Harden setup, sessions and filesystem boundaries
2. Verify native distributions, container licensing and reproducible alpha builds
3. Expose useful playback errors and streaming diagnostics
4. Audit setup, library and player accessibility
5. Add bounded software transcoding and HLS sessions

| Priority | Issue | Depends on |
|---|---|---|
| P0 | Harden setup, sessions and filesystem boundaries | None |
| P0 | Verify native distributions, container licensing and reproducible alpha builds | None |
| P1 | Expose useful playback errors and streaming diagnostics | None |
| P1 | Add bounded software transcoding and HLS sessions | diagnostics |
| P2 | Add adaptive bitrate and network-aware quality selection | transcode |
| P2 | Implement NVIDIA NVENC acceleration with safe fallback | transcode |
| P2 | Implement Intel and AMD hardware acceleration adapters | transcode |
| P2 | Validate AV1, HEVC, HDR and tone-mapping compatibility | transcode |
| P1 | Add subtitle and audio-track selection | transcode |
| P1 | Add movie and TV identification with cached artwork | None |
| P1 | Add library monitoring, reconciliation and multiple library types | None |
| P2 | Add indexed search and smart collections | metadata, libraries |
| P1 | Add multi-user administration, permissions and parental controls | hardening |
| P2 | Add watchlists, local recommendations and UI personalization | accounts, metadata |
| P1 | Audit setup, library and player accessibility | None |
| P2 | Implement authenticated playback handoff between devices | accounts, tracks |
| P2 | Define client SDK and ship a first Windows desktop client | transcode, accounts |
| P2 | Build Android, Android TV and Smart TV client foundations | clients, tracks |
| P1 | Design and validate secure remote streaming | hardening, accounts, transcode |
| P2 | Define a permissioned plugin API and example extension | metadata, hardening |
| P1 | Add resource, active-stream and storage monitoring | diagnostics, transcode |
| P1 | Implement consistent backups, safe updates and rollback | distribution, hardening |
