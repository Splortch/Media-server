# Client validation record

Local Linux validation uses synthetic media, a fresh database and the actual ASP.NET backend. The existing web UI remains independently runnable.

Observed locally: backend build and 11 API/database/security tests pass; the React web production build, two player tests and lint pass; the Windows renderer production build, five renderer/contract/progress tests and lint pass; Rust transport unit tests pass; the native transport's real-backend integration passes login, session restoration, protected ranges, resume, conflicts, revocation and unavailable-server handling. The first hosted Windows run passed the Credential Manager roundtrip, real-backend integration, native Tauri compilation, EXE/MSI packaging and executable startup smoke check. Final-commit results must still be checked in the PR; these are hosted Windows checks, not physical device playback tests.

No physical Windows machine, Android device, TV or iPhone was used. No Android APK/AAB or iOS archive is produced by this PR. Chromium web playback validation is distinct from WebView2 decode validation. EXE/MSI compilation does not prove every codec, HDR path, installer interaction or Windows media-control integration works.

The schema snapshot is exported from the real authenticated OpenAPI endpoint and generated TypeScript is checked for drift in CI. Existing web end-to-end tests still exercise setup → login → scan → playback → resume. See the PR's live workflow results for the final commit; do not infer successful hosted builds from workflow YAML alone.

RustSec audit reports no vulnerability errors. The complete cross-platform Tauri lockfile includes two informational warnings: glib 0.18 (RUSTSEC-2024-0429, unsound iterator) and proc-macro-error 1 (RUSTSEC-2024-0370, unmaintained). `cargo tree --target x86_64-pc-windows-msvc -i <crate>` confirms neither is in the Windows target dependency graph. Do not advertise/build a Linux desktop client from this lockfile without revisiting those upstream GTK dependencies. Audit warnings remain visible in CI; they are not globally suppressed.
