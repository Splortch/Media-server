# Changelog

## Unreleased — client API / Windows alpha

- Add versioned `/api/v1` alongside the existing browser routes, generated OpenAPI/TypeScript contracts, server search, track metadata, device sessions/capabilities and sanitized playback reports.
- Add conditional revision-based progress synchronization and migrate the included browser player to it.
- Add a dedicated Windows Tauri application with native authenticated networking, Credential Manager protection and a scoped loopback streaming bridge.
- Add independent server, browser and Windows checks/build artifacts. Android phone/TV/iOS remain planned follow-ups; no stable release is published.

## Unreleased — 0.1.0-alpha foundation

- ASP.NET Core modular monolith with SQLite migrations, local administrator setup and revocable sessions.
- Read-only background scanning with FFprobe, configured directory boundaries and symbolic-link rejection.
- Authenticated range streaming, technical media details and persistent playback progress.
- React web client with setup, login, dashboard, library, player, Continue Watching and settings.
- Automated tests, dependency audits, development container configuration and alpha release workflows.

No official stable release has been published. Transcoding, hardware acceleration and native clients remain planned.
