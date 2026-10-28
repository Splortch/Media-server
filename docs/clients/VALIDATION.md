# Client validation record

Local Linux validation uses synthetic media, a fresh database and the actual ASP.NET backend. The existing web UI remains independently runnable.

Observed locally: backend build and 11 API/database/security tests pass; the React web production build, two player tests and lint pass; the Windows renderer production build, five renderer/contract/progress tests and lint pass; Rust transport unit tests pass; the native transport's real-backend integration passes login, session restoration, protected ranges, resume, conflicts, revocation and unavailable-server handling. The first hosted Windows run passed the Credential Manager roundtrip, real-backend integration, native Tauri compilation, EXE/MSI packaging and executable startup smoke check. Final-commit results must still be checked in the PR; these are hosted Windows checks, not physical device playback tests.

No physical Windows machine, Android device, TV or iPhone was used. The original Windows foundation produced no Android or iOS build; Android validation is recorded below. No iOS archive exists. Chromium web playback validation is distinct from WebView2 decode validation. EXE/MSI compilation does not prove every codec, HDR path, installer interaction or Windows media-control integration works.

The schema snapshot is exported from the real authenticated OpenAPI endpoint and generated TypeScript is checked for drift in CI. Existing web end-to-end tests still exercise setup → login → scan → playback → resume. See the PR's live workflow results for the final commit; do not infer successful hosted builds from workflow YAML alone.

RustSec audit reports no vulnerability errors. The complete cross-platform Tauri lockfile includes two informational warnings: glib 0.18 (RUSTSEC-2024-0429, unsound iterator) and proc-macro-error 1 (RUSTSEC-2024-0370, unmaintained). `cargo tree --target x86_64-pc-windows-msvc -i <crate>` confirms neither is in the Windows target dependency graph. Do not advertise/build a Linux desktop client from this lockfile without revisiting those upstream GTK dependencies. Audit warnings remain visible in CI; they are not globally suppressed.

The API-compatible GLib soundness backport and its source/optimized regression checks are documented in [GLIB-SECURITY.md](GLIB-SECURITY.md). GitHub alert closure must be checked after merging; no advisory is dismissed by the fix.

## Android phone alpha — 28 October 2026

The Android implementation in PR #58 uses the unchanged backend API. [Android run 5](https://github.com/Splortch/Media-server/actions/runs/40697752489) at source `0875686364a0d91033ee555cb91c7202ebce8769` passed both build and device jobs. The post-validation cleanup formats Kotlin and adds APK license assets; final-commit checks remain authoritative.

Observed checks:

- Clean local JDK 17 / Android SDK 36 build: debug APK, instrumentation APK, unsigned release APK and unsigned AAB. Repeated after formatting/asset changes; successful.
- Seven JVM connection/security tests, generated DTO drift check and four release-publisher safety tests passed.
- Android lint reports **No issues found**. Android `apksigner verify` validates the debug APK signature; unsigned release artifacts are deliberately not represented as installable.
- Packaging exercised the actual assembled APK/AAB ZIP manifests and generated source metadata/checksums; all five checksum entries verified.
- Hosted API 35 Google APIs x86_64 emulator installed and launched the real debug APK. Instrumentation reported `OK (1 test)` against a fresh actual ASP.NET backend and synthetic 60-second VP9/Opus media.
- That test uses the Compose connection/login/library/details UI, waits for a Media3 rendered frame, verifies the seeded resume position, recreates the activity, checks AES-GCM ciphertext changes and a non-exportable Keystore key, forces a competing server progress revision without overwriting it, resumes at the authoritative position, revokes the phone session and verifies the player/private state/credentials clear. The fixture also verifies anonymous media requests receive 401.
- No physical Android phone, manufacturer decoder matrix, audible output, min-SDK emulator matrix, remote HTTPS deployment or production signing identity has been validated. The test app uses a development signing key; release APK/AAB remain unsigned.

The workflow publishes an independent Android prerelease only on main pushes after both Android jobs pass. PR runs upload artifacts but do not publish a release. Publication itself must be verified after a reviewed merge; no merge or stable release is part of this work.
