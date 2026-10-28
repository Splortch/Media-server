# Client ecosystem

All applications live in this monorepo. The ASP.NET server remains independently runnable on Windows/Linux/macOS; neither desktop nor mobile software is required by the server or web UI. Client/server OS combinations are independent.

| Component | Status in this change |
|---|---|
| Server and web | Existing functionality retained; versioned client API and revision-safe web progress added |
| Shared API | OpenAPI exported from the running API; generated TypeScript schemas and a transport-neutral client |
| Windows | Tauri/Rust host with a locally bundled React UI, authenticated networking and local streaming bridge; Windows CI builds EXE/MSI |
| Linux/macOS desktops | Shared Tauri implementation; Ubuntu 24.04 x64 DEB and Intel/Apple Silicon macOS DMGs. Memory-only sessions; hosted build/install checks differ from physical playback testing |
| Android phone | Native Kotlin/Compose phone alpha, Media3 Direct Play, encrypted sessions, real-backend emulator test and independent APK/AAB workflow |
| Android TV | Architecture and focused backlog; no placeholder application |
| iOS | Architecture and focused backlog; no placeholder application |

Start with [API/authentication](API.md), [Windows development and limitations](WINDOWS.md), [synchronization](SYNC.md), and [Android installation/development](ANDROID.md). Packaging success is distinct from real device playback validation.

## Repository boundaries

- `backend/`: API, persistence, media and service modules; no client dependency.
- `frontend/`: independently usable browser UI; the existing Player and visual styles are reused by all desktop builds.
- `clients/windows/src/`: dedicated connection/login/library/settings UI, never a remote web-page shortcut.
- `clients/windows/native/`: Rust transport, cookie session handling, credential-store abstraction, scoped media bridge, unit/integration tests. It can be tested without Tauri/GTK.
- `clients/windows/src-tauri/`: shared desktop window, permissions, native preferences and OS-specific installer configuration. The historical `windows` directory name is retained to avoid moving working code.
- `clients/android/`: reusable Android networking/security core and native Compose/Media3 phone app.
- `shared/api/`: exported version-1 OpenAPI, generated models, reusable transport-neutral TypeScript client.
- `docs/clients/`: future platform decisions and implementation scope. Android TV/iOS directories will be created when functional implementations begin.

## Two-maintainer split

Maintainer A owns API/schema migrations, native transport, authentication and release packaging. Maintainer B owns client UI, Compose phone implementation, playback UX and accessibility. Both review authentication, stream authorization, schema changes and synchronization. Keep each client change focused and validate it against the common backend. No merge or stable release is authorized by this change.

## Android phone alpha

See the [Android installation and development guide](ANDROID.md) for the native Kotlin/Compose application, APK artifacts, USB setup, signing and validation limits. Server and web operation do not depend on Android. Android TV and iOS remain future applications; no empty projects are added.


See [desktop installers for Windows, Linux and macOS](../deployment/DESKTOP-INSTALLERS.md) for server/client package selection and step-by-step setup.
