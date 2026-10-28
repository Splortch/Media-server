# Deployment

For your first installation, follow the [step-by-step setup guide](../GETTING-STARTED.md). Downloaded server builds already include the web UI and .NET runtime; the publish commands below are for developers. This alpha defaults to loopback and is not configured for public remote access.

## Native published build

```bash
cd frontend
npm ci
npm run build
cd ..
dotnet publish backend/src/Api -c Release -r linux-x64 --self-contained true -o artifacts/server
cp -r frontend/dist artifacts/server/wwwroot
```

For Windows replace `linux-x64` with `win-x64` and use PowerShell `Copy-Item -Recurse frontend/dist artifacts/server/wwwroot`. Run `Api` (Linux) or `Api.exe` (Windows) from the output directory. Linux may require `chmod +x Api`. Install FFprobe separately and set its path if it is not on PATH. Configure absolute data and media roots through environment variables before launch. No .NET runtime is required for self-contained output; OS native libraries still apply.

## Docker Compose

Copy `.env.example` to `.env`, set an existing media path, then `docker compose up --build -d`. Data persists in the `server-data` volume. Read the setup token with `docker compose exec media-server cat /data/setup-token`. Select `/media` in the web interface. Do not run `docker compose down -v` unless you intend to erase accounts, keys, configuration and watch history.

The image runs as an unprivileged user, drops capabilities, forbids privilege escalation, makes the root filesystem read-only and mounts media read-only. It installs the distribution FFmpeg package for FFprobe. Verify media read permissions for the container user; avoid making private media world-writable as a workaround.

## Backup and updates

Stop the server before copying the **whole data directory**, including SQLite and its sidecar files and the key ring. Back up media separately. Encrypt backup copies. Restore into an isolated test instance first. Keep the prior runnable build and data backup together; schema downgrade is not automatic. Never run two versions concurrently against one database. Safe automated rollback is planned, not implemented.

## GitHub configuration

A connected tool may not expose repository-settings, ruleset or Projects administration. `scripts/configure-repository.py` applies the confirmed public visibility, description, Discussions, security alerts and a modest branch-protection policy using an already authenticated GitHub CLI. `--project` optionally creates/configures the maintainer board and adds roadmap issues; it needs organization Project permissions and may require a token with `project` scope. Review its output; it reports each failed operation independently. It never accepts or stores tokens in source.

The Roadmap workflow seeds labels, milestones and actionable issues after a push to `main`. It is idempotent by title and requires Actions to have issues-write permission. It does not run on untrusted pull requests. Hosted settings and workflow success must be verified separately.

