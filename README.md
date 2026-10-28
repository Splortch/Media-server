# Media Server

A modern, open-source, self-hosted media server focused on performance, simplicity, and a seamless streaming experience.

**Early alpha foundation.** Independent of Jellyfin, Plex and Emby. No cloud account, subscription, advertising or telemetry. Host only media you are entitled to use. Apache-2.0; see [LICENSE](LICENSE) and [NOTICE](NOTICE).

## Cross-platform clients (proposed alpha)

The [client ecosystem](docs/clients/README.md) adds a dedicated Windows Tauri client and versioned API without requiring any client on the server. [Windows development](docs/clients/WINDOWS.md) covers actual installer builds and limitations. Android phone, Android TV and iOS are separately scoped follow-ups in this monorepo; no placeholder apps or fake APK builds are included. Existing web installation below remains valid.

## What works

| Capability | Status | Scope |
|---|---|---|
| First-run setup and login | Implemented | One local administrator; Argon2id, password change/local recovery, session revocation, consumed setup token, CSRF |
| Libraries and scanning | Implemented | Explicit allowed roots, background jobs, FFprobe metadata, idempotent updates, admin-only scan failure references |
| Media browsing | Implemented | Responsive React UI, search, technical details, honest empty states |
| Direct Play | Implemented | Authenticated byte ranges through validated handles on Windows/Linux; controls, resume and playback diagnostics |
| Continue Watching | Implemented | Per-user persisted position; completed titles excluded |
| SQLite schema migrations | Implemented | Versioned EF Core migration, applied at startup |
| REST/OpenAPI and scan status | Implemented | Protected OpenAPI; scan polling and SignalR event transport |
| Automated quality checks | Implemented configuration | Backend, frontend, browser integration, audits and Docker build |
| Native alpha releases | Implemented workflow | Self-contained Windows/Linux builds, bundled web UI, separate automatic alpha releases |
| Hardware acceleration / HLS / transcoding | Planned | Direct Play only in this alpha |
| Artwork / online metadata / TV matching | Planned | Current titles derive from filenames; no fabricated posters |
| Multi-user administration / parental controls | Planned | Role authorization exists; no account-management UI yet |
| Windows desktop client | Alpha | Separate Tauri client; Windows build and actual-backend tests in CI |
| Android / TV / iOS / plugins / secure remote streaming | Planned | No mobile binaries yet; localhost deployment by default |
| Accessibility | In progress | Automated axe and keyboard regressions; manual assistive-technology review still required |

Verification results and environment limits are recorded in [VALIDATION.md](docs/development/VALIDATION.md). Workflow presence is not a claim that hosted CI passed.

## Download and run

**First time? Start with the [step-by-step setup guide](docs/GETTING-STARTED.md).** It covers Windows, Linux, Docker, first login, adding media, playback/resume checks and troubleshooting. For review findings and validation limits, see [the alpha project review](docs/development/PROJECT-REVIEW.md).

Automatic alpha release packaging is configured for Windows/Linux x64 servers, Windows EXE/MSI
clients and a prebuilt Docker image. The web UI is bundled. Download from [Releases](https://github.com/Splortch/Media-server/releases),
extract the server, and use its Start launcher. See [installation, updates and release automation](docs/deployment/RELEASES.md).
No SDK is needed for downloaded server builds. Publication requires all main checks to pass on the same source commit.

## Quick start: local development

Install the .NET SDK selected in `global.json`, Node.js 24, and FFprobe (usually supplied with FFmpeg). Git and npm are required. Ensure `dotnet`, `node`, `npm`, `ffprobe` are on PATH.

From the repository root, on Linux (Windows instructions are linked below):

```bash
export MediaServer__DataDirectory="$HOME/.local/share/media-server-dev"
export MediaServer__AllowedRoots__0="/absolute/path/to/media"
dotnet restore MediaServer.sln --locked-mode
dotnet run --project backend/src/Api
```

The default `data/` directory is ignored. Keep the database, setup token and key ring private and outside source control.

In another terminal:

```bash
cd frontend
npm ci
npm run dev
```

Open **http://127.0.0.1:5173**. Read `setup-token` in the configured data directory, create an administrator with a password of at least 12 characters, and sign in. In Settings add an absolute media directory under an allowed root, then choose **Scan library**. Open Library, select a compatible video and play it. A VP9/Opus WebM or H.264/AAC MP4 is a useful starting point; actual support depends on the browser and OS.

The backend listens on **127.0.0.1:5050**. Vite proxies `/api` and `/hubs` to it; no permissive CORS is required. Environment variables use .NET's `__` nesting. `.env` files are **not** automatically read by native .NET processes.

For PowerShell and deployment instructions see [development](docs/development/SETUP.md) and [deployment](docs/deployment/DEPLOYMENT.md).

## Docker

```bash
cp .env.example .env
# Set MEDIA_PATH in .env to an existing absolute media directory.
docker compose up --build -d
docker compose exec media-server cat /data/setup-token
```

Open **http://127.0.0.1:5050**. Inside Settings use `/media` (the container path), not your host path. The media mount is read-only; database and keys live in a named volume. Do not publish this alpha directly to the internet. [Remote access and threat model](docs/architecture/SECURITY.md).

## Check your changes

```bash
dotnet build MediaServer.sln
dotnet test MediaServer.sln
dotnet tool restore
dotnet csharpier check backend
python3 scripts/audit.py
cd frontend
npm ci
npm run build
npm test
npm run lint
npm run format:check
npm audit --audit-level=moderate
npx playwright install chromium
cd ..
bash scripts/e2e.sh
```

The browser test starts a fresh API database and generates its own short test video using FFmpeg. It never needs personal media. Port 5050 must be free. See [API](docs/api/README.md), [architecture](docs/architecture/README.md), [contributing](CONTRIBUTING.md), and the [prioritized roadmap](docs/ROADMAP.md).

## Two-maintainer workflow

One maintainer can own backend/media and the other frontend/experience. Share security-sensitive API and playback reviews. Use focused issues, feature branches, Conventional Commits and pull requests. Require CI; one review is recommended with an administrator bypass for urgent fixes or an unavailable teammate. No bureaucracy collection minigame required.

Approved AI implementation tasks use the [issue-to-PR workflow](docs/development/AUTONOMY.md): a maintainer approves one issue, Copilot proposes a tested PR, and a human reviews it. Repository configuration is required before activation; no automatic merges are enabled.


Security and troubleshooting updates: [operator recovery and trust boundaries](docs/architecture/HARDENING.md),
[playback diagnostics](docs/development/PLAYBACK-DIAGNOSTICS.md), and
[accessibility validation and remaining manual checks](docs/development/ACCESSIBILITY.md).

