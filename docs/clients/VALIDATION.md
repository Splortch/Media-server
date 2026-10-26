# Client validation record

Local Linux validation uses synthetic media, a fresh database and the actual ASP.NET backend. The existing web UI remains independently runnable.

Observed locally: backend build and 9 API/database/security tests pass; the React web production build, two player tests and lint pass; the Windows renderer production build, three contract/progress tests and lint pass; Rust transport unit tests pass; the native transport's real-backend integration passes login, session restoration, protected ranges, resume, conflicts, revocation and unavailable-server handling. Installer compilation and Windows Credential Manager roundtrip are delegated to the Windows CI job and must be checked in the PR results before claiming success.

No physical Windows machine, Android device, TV or iPhone was used. No Android APK/AAB or iOS archive is produced by this PR. Chromium web playback validation is distinct from WebView2 decode validation. EXE/MSI compilation does not prove every codec, HDR path, installer interaction or Windows media-control integration works.

The schema snapshot is exported from the real authenticated OpenAPI endpoint and generated TypeScript is checked for drift in CI. Existing web end-to-end tests still exercise setup → login → scan → playback → resume. See the PR's live workflow results for the final commit; do not infer successful hosted builds from workflow YAML alone.
