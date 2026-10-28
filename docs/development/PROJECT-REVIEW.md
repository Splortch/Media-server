# Alpha project review — 28 October 2026

## Scope and conclusion

Update: PR #55 has since merged into main. The findings below describe the original reviewed alpha.49 snapshot and are retained as that historical review; use a release whose source includes the merge to obtain its fixes.

Reviewed the source, deployment configuration, launchers, web/client flows, test coverage, published asset names and hosted workflow results for main commit [`32645e5`](https://github.com/Splortch/Media-server/commit/32645e571b6d28568a9da42c6b2ca169c8157d00). The four separate `alpha.49` platform releases identify that source commit. [PR #55](https://github.com/Splortch/Media-server/pull/55) was unmerged at the original review and is assessed separately below.

**Suitable for a cautious, local, single-administrator alpha trial with copies of media. Not established as production-ready or safe for direct internet exposure.** The project has working server/API/browser/native-client code and real integration tests, rather than a static demonstration. The next useful milestone is a maintainer completing the [first-run acceptance checklist](../GETTING-STARTED.md#9-your-first-hands-on-acceptance-checklist) on their own computer.

This review does not certify the whole project. No fresh .NET/backend run was possible in this review environment because `dotnet` is unavailable; no physical Windows installation, WebView2 codec matrix, mobile device test, exhaustive Git-history secret scan or independent license/security audit was performed. Existing CI evidence is explicitly identified rather than described as new local testing.

Fresh documentation validation: checked 34 relative Markdown links and balanced code fences across the six changed/new documents. Ran the guide's synthetic-video command on Linux; FFprobe identified VP9 video, Opus audio and 60.008 seconds of WebM content. This validates the example generation command, not playback in a browser or on a physical Windows PC.

## Findings, in priority order

| Priority | Finding and effect | Evidence / action |
| --- | --- | --- |
| P1 before multi-user deployment | Released web client retains previous account state across logout/session expiration. Authentication still protects new API requests, but a later login can briefly reuse old in-memory items/progress/counts before replacement requests complete. | `frontend/src/App.tsx`: logout/expired handlers clear the user but not catalog/progress/server state. PR #55 adds state clearing, request-generation guards and an account-switch regression. Review and merge that fix before relying on separate-user privacy; do not describe this as a backend authorization bypass. |
| P1 for remote/untrusted use | FFprobe processes operator-controlled files with time/output/protocol limits, but has no OS sandbox. Scanning checks paths then probes by pathname, so hostile writers can race scanning. | `backend/src/Media/Ffprobe.cs`, `PathPolicy.cs` and [HARDENING.md](../architecture/HARDENING.md). Keep roots/ancestors trusted, media mounts read-only and the alpha local. Container hardening is useful but does not establish hostile-input safety. Stronger process isolation remains follow-up work. |
| P2 for larger catalogs | Released web browsing downloads every media page and filters locally, making catalog size affect request count, memory and time to display. | `frontend/src/App.tsx` loops over `/media?offset=...&limit=200` until exhausted. PR #55 replaces this with bounded indexed/keyset browsing and a synthetic 100k-item test. Its measurements do not describe the released alpha.49 UI. |
| P2 for maintainers | PR #55's final release assembly is unverified. Its platform builds passed, but the final job failed before runner allocation and retry did not execute any steps. The maintainer subsequently reported the Actions allowance was exhausted; the actual annotation was not available through the connector. | [Run 40615001846](https://github.com/Splortch/Media-server/actions/runs/40615001846). Resolve the quota/access condition and rerun assembly before merging/publishing the new batch. Do not infer a packaging-code failure or success from missing logs. Existing main alpha.49 releases are separate, successfully published artifacts. |
| P2 for first-time users | Previous installation instructions mixed server/client/developer routes and lacked expected outcomes and a complete manual acceptance record. | Addressed by [GETTING-STARTED.md](../GETTING-STARTED.md), linked from the README and deployment/release documentation. It distinguishes allowed roots from added libraries, setup from login, host from container paths, and install success from codec testing. |
| P2 for distribution | Windows installers/native releases are unsigned. Browser/WebView2 codec support, installer prompts, high-DPI behavior and physical-device usability are not established by successful compilation. | [Windows client](../clients/WINDOWS.md), [client validation](../clients/VALIDATION.md). Run the manual checklist, record exact versions, then test representative H.264/AAC and VP9/Opus media. Signing is a separate release task. |

These are scoped findings, not a claim that no additional bugs exist. PR #55 already addresses the two code findings above; this documentation PR does not duplicate those changes or claim them as released.

## What is working, and what the evidence actually proves

| Area | Evidence at the reviewed main commit | Limit |
| --- | --- | --- |
| Backend/security/database | [Backend run 40606492016](https://github.com/Splortch/Media-server/actions/runs/40606492016) succeeded on Windows and Ubuntu. Source tests cover setup/CSRF, salted Argon2id, cookie/session expiration/revocation, role denial, path/link restrictions, ranges, migrations, progress conflicts and recovery. | Automated fixture coverage, not a complete adversarial security audit. |
| Browser-to-server functionality | [CI run 40606492091](https://github.com/Splortch/Media-server/actions/runs/40606492091) succeeded: real setup/login, generated-video scanning, authenticated ranges, browser playback/progress/resume, diagnostic errors and automated accessibility checks. | Hosted Chromium with synthetic media, not every browser/codec or manual assistive-technology testing. |
| Windows desktop | [Windows run 40606492033](https://github.com/Splortch/Media-server/actions/runs/40606492033) succeeded: native transport/backend integration, credential storage tests, Rust checks, installers and executable startup. | No physical installer walkthrough or representative WebView2/GPU/media matrix. |
| Published distribution | [Windows server](https://github.com/Splortch/Media-server/releases/tag/server-windows%2Fv0.2.0-alpha.49), [Linux server](https://github.com/Splortch/Media-server/releases/tag/server-linux%2Fv0.2.0-alpha.49), [Windows client](https://github.com/Splortch/Media-server/releases/tag/client-windows%2Fv0.1.0-alpha.49), [Docker](https://github.com/Splortch/Media-server/releases/tag/docker%2Fv0.2.0-alpha.49). | Actual runnable assets exist; an asset's presence does not prove the user's OS can decode all media. Private repository/package settings can still require authentication. |
| Original-file safety | Scanner reads media and writes metadata/progress to SQLite; no original-media delete/rename/convert operation. Docker media bind mount is read-only. | Keep a backup of valuable media and use a small copied test collection first. Trusted local writers can still change files independently. |

## Features deliberately absent from the released version

- Transcoding, HLS/adaptive bitrate, hardware-acceleration management, tone mapping and universal codec compatibility.
- Functional alternate audio/subtitle selection, offline downloads or automatic playback handoff.
- Online movie metadata/posters and TV season/episode identification.
- Android phone/TV and iOS applications or corresponding installable artifacts.
- One-click secure remote access, service installation and signed automatic desktop updates.
- PR #55's account administration, rating policies, filesystem watching/reconciliation, smart collections, monitoring and managed backup/update commands.

The architecture keeps the server independent of clients and uses the same authenticated API for web/Windows. There is no need to restructure into microservices to perform the first user trial.

## Recommended next sequence

1. Try the published Windows server and web UI on a small copied library using the new guide. Record each checkpoint; report the first failing step rather than changing multiple settings at once.
2. Try the Windows client against that known-working local server and confirm shared resume. Test unsupported media deliberately and check that the error is understandable.
3. Unblock Actions, complete PR #55's final assembly and review its migration/security changes. Back up data before adopting it; do not test alpha upgrades on your only data copy.
4. Fix observed first-run problems before expanding scope. Then prioritize metadata/artwork and subtitle/audio selection based on actual usage.
5. Before changing repository visibility, review full Git history and issue/PR attachments for secrets/private data, distribution notices/source obligations and private vulnerability-reporting configuration. This source/installation review does not replace that pre-publication audit.

Use the [acceptance checklist](../GETTING-STARTED.md#9-your-first-hands-on-acceptance-checklist) as the manual test record. Leave unchecked items unchecked until someone actually performs them.
