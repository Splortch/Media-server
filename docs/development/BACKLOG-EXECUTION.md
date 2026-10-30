# Backlog implementation order

This is an implementation sequence, not a claim that the roadmap is finished.
Use the linked GitHub issues for acceptance criteria and PR evidence. Work stays
on feature branches and is reviewed before merging; physical-device and
maintainer-review requirements cannot be replaced by software CI.

## Release and security gates

| Issue | Remaining work / validation |
| --- | --- |
| [#1](https://github.com/Splortch/Media-server/issues/1) | FFprobe pathname/process boundary and both-maintainer security review; preserve existing setup/session/no-follow streaming protections. |
| [#2](https://github.com/Splortch/Media-server/issues/2) | SBOM/license inventory and redistribution evidence; clean physical-machine distribution checks. Base pinning and installer automation already exist. |
| [#4](https://github.com/Splortch/Media-server/issues/4) | Audit existing HLS/session/quota implementation against every criterion; fix measured gaps, do not build a duplicate pipeline. |
| [#6](https://github.com/Splortch/Media-server/issues/6) | Physical NVENC execution and evidence after the implemented capability/fallback adapter. |
| [#7](https://github.com/Splortch/Media-server/issues/7) | Physical Intel/AMD acceleration evidence after implemented adapters. |
| [#8](https://github.com/Splortch/Media-server/issues/8) | Physical HDR display/codec validation; keep synthetic tone-map and browser evidence distinct. |
| [#9](https://github.com/Splortch/Media-server/issues/9) | Finish actual-device track/subtitle acceptance against existing native/web controls. |
| [#15](https://github.com/Splortch/Media-server/issues/15) | Manual NVDA/VoiceOver, forced-color and physical-device review; automated axe/keyboard checks already exist. |
| [#17](https://github.com/Splortch/Media-server/issues/17) | Physical Windows installer, high-DPI and codec validation of the existing Tauri app. |
| [#19](https://github.com/Splortch/Media-server/issues/19) | VPN-first and TLS proxy integration tests; keep public hosting opt-in. |
| [#69](https://github.com/Splortch/Media-server/issues/69) | Coordinated Android SDK/toolchain migration; keep dependent PRs #72/#77 separate from feature work. |

## Product dependency sequence

| Order | Issue | Implementation boundary |
| --- | --- | --- |
| 1 | [#52](https://github.com/Splortch/Media-server/issues/52) | Information-first web/desktop pages, no streaming before Play/Resume, fresh progress and list-context restoration. Series navigation follows #10/#45. |
| 2 | [#51](https://github.com/Splortch/Media-server/issues/51) | Small feature registry, server policy and per-user visibility for shipped optional features; no switches advertising imaginary modules. |
| 3 | [#10](https://github.com/Splortch/Media-server/issues/10) | Safe local NFO/artwork and manual corrections first, then explicitly configured provider adapters. |
| 4 | [#53](https://github.com/Splortch/Media-server/issues/53) | Permission-filtered provenance, private file diagnostics and deduplicated match reports on title pages. |
| 5 | [#54](https://github.com/Splortch/Media-server/issues/54) | Family requests/support/status, privacy-aware duplicate search, admin-confirmed library linking, server feature gating. |
| 6 | [#14](https://github.com/Splortch/Media-server/issues/14) | Private watchlists, explainable local recommendations and accessible preferences; extend existing reduced-motion styling. |
| 7 | [#44](https://github.com/Splortch/Media-server/issues/44) | Ordered manual collections and portable import/export, distinct from existing smart collections. |
| 8 | [#46](https://github.com/Splortch/Media-server/issues/46) | Private bookmarks/notes, stable-ID import/export and timeline-safe seeking. |
| 9 | [#43](https://github.com/Splortch/Media-server/issues/43) | Optional account-owned profiles with server-side ownership, progress separation and rate-limited PINs. |
| 10 | [#45](https://github.com/Splortch/Media-server/issues/45) | Identified series/seasons, spoiler-aware next episode and undoable watched-state changes. |
| 11 | [#48](https://github.com/Splortch/Media-server/issues/48) | Time-budget discovery over authorized media and trustworthy remaining time. |
| 12 | [#50](https://github.com/Splortch/Media-server/issues/50) | Reversible editions with independent progress; never infer a cut solely from duration. |
| 13 | [#47](https://github.com/Splortch/Media-server/issues/47) | Off-by-default household activity, individual privacy and bounded retention. |
| 14 | [#16](https://github.com/Splortch/Media-server/issues/16) | Expiring, confirmed device handoff and cross-client revision-conflict coverage. |
| 15 | [#20](https://github.com/Splortch/Media-server/issues/20) | Permissioned isolated metadata extension after metadata/security contracts stabilize. |
| 16 | [#33](https://github.com/Splortch/Media-server/issues/33) | Real Android TV UI over shared security/playback modules, emulator then physical remote testing. |
| 17 | [#34](https://github.com/Splortch/Media-server/issues/34) | Real SwiftUI/AVPlayer client, simulator integration, then externally supplied device signing and physical validation. |

## First increment: title information

Web and desktop cards open an information view, not a mounted video element.
Play and Resume fetch fresh server progress before mounting the player; desktop
stream URLs are requested only after the action. Existing library query/filter
state is retained on Back, with focus/scroll restoration when the originating
card is still on the loaded page. A direct title URL falls back to the library.

Titles still come from filenames. No synopsis, poster, season, year or cast is
invented. This increment does not finish #10 or the series criteria of #52.
Android retains its existing separate details/playback flow.
