# Automatic alpha releases and easy installation

Every successful main commit can publish separate platform **prereleases** automatically. No release tag, personal token or signing key is needed.
GitHub's built-in repository token publishes Releases and the GHCR container image.
Organization policy must permit repository Actions to write Releases and Packages.

## Download and run

New users: follow [Your first Media Server setup](../GETTING-STARTED.md) for numbered instructions, expected results and troubleshooting. The [project review](../development/PROJECT-REVIEW.md) distinguishes automated evidence from hands-on testing still required.

Open the repository's **Releases** page and choose the newest alpha for your platform. These are runnable builds,
not source zips. No .NET SDK is needed by end users. The web UI is included in both server builds
and the Docker image; there is no browser-only file:// application to install.

| Download | What to do |
| --- | --- |
| `MediaServer-v0.2.0-alpha.N-win-x64.zip` | Extract to a folder, then double-click `Start-MediaServer.cmd`. Choose a media folder, optionally download verified FFprobe tools, and complete setup in the browser it opens. Leave the console open. |
| `MediaServer-v0.2.0-alpha.N-linux-x64.tar.gz` | Extract, install FFprobe (`sudo apt install ffmpeg` on Ubuntu), then run `./start-media-server.sh`. Choose an absolute media folder. Open the displayed local URL and read the setup-token file. |
| `MediaServer-Windows-Client-v0.1.0-alpha.N-win-x64-setup.exe` or `.msi` | Install the desktop client and connect it to a running server. Installing the client does not install/start the server. |
| `MediaServer-Docker-v0.2.0-alpha.N.zip` | Install Docker/Compose, extract, copy `.env.example` to `.env`, set `MEDIA_PATH`, then run `docker compose up -d`. No source build is required. |

In the server browser UI, create the administrator using the locally generated setup token.
Add the allowed media folder as a library in Settings, scan it, then choose a compatible video.
Docker library paths use `/media`, not the host path. Original media is mounted/read read-only.

The server defaults to loopback access. HTTPS and safe remote access configuration are still
required for other devices; see DEPLOYMENT.md and the architecture security guide. No automatic
port forwarding or public exposure is added. Windows installers are unsigned, so Windows may
show publisher/SmartScreen warnings. Code-signing and a native auto-updater are future work.

Windows server data and media-root preferences live in `%LOCALAPPDATA%\Splortch\MediaServer`.
Linux data defaults to `${XDG_DATA_HOME:-$HOME/.local/share}/media-server`. Docker keeps a named
`server-data` volume. These are outside the versioned archive, so replacing a download does not
replace the database or setup state. Back up data before an alpha upgrade; database migrations
can make a downgrade unsafe. Keep the same data directory or Compose project name when updating.
Windows's launcher uses a process-only PowerShell execution-policy override for the bundled
script; it does not change the machine's policy or disable TLS certificate verification.

Windows's optional first-run media-tool download is pinned to FFmpeg 8.1.2 and a reviewed
SHA-256. The tools are obtained directly by the user from GyanD, not bundled in native archives.
An existing FFprobe on PATH is used instead when available. Offline installation is possible
with a separately installed FFprobe. The server does not require a cloud account.

## Platforms included today

Server: Windows x64 and Linux x64. Desktop client: Windows x64 (EXE/MSI). Container: Linux amd64.
Web: bundled into the server/container. ARM64, macOS desktop, Android phone/TV and iOS artifacts
are not generated because this repository has no validated distribution for them yet.

## Separate platform releases

Each validated commit creates four separate GitHub Releases, with independent tags, notes,
checksums and provenance manifests:

| Release | Tag | Downloads |
| --- | --- | --- |
| Media Server for Windows | `server-windows/v0.2.0-alpha.N` | Windows x64 server ZIP |
| Media Server for Linux | `server-linux/v0.2.0-alpha.N` | Linux x64 server tar.gz |
| Media Server Windows Client | `client-windows/v0.1.0-alpha.N` | EXE and MSI installers |
| Media Server Docker | `docker/v0.2.0-alpha.N` | Compose setup ZIP, package/license inventory and corresponding source |

EXE and MSI are alternative installers in the same Windows client release. Web remains bundled
with the server. Future Android/TV/iOS distributions will receive their own release identities
when functional builds exist. The earlier combined `v0.2.0-alpha.40` release remains unchanged.
GitHub adds source archive links to each release automatically; those are not application builds.

## Publication rules

`Automatic prereleases` listens for completion of the five existing main workflows. It requires
the latest run of **every** required workflow to pass on the exact same main commit and current
repository. PR/fork upstream runs, failed/pending checks, stale commits and lookalike workflow
paths cannot authorize publication. The final publisher checks main again before publication.

The Windows CI run number N supplies deterministic versions for the commit: server
`0.2.0-alpha.N`, client `0.1.0-alpha.N`, MSI `0.1.N`. The first two MSI version fields stay stable
and its third field increases, allowing MSI to recognize newer alpha installers. The value
must fit 1..65535; the workflow fails before overflow instead of wrapping. Application config,
Cargo package/lock metadata, assembly versions and download names are stamped consistently.
Server and client versions belong to separate platform releases. They currently share a validated
source commit and build counter; this is not a stable-release process.

All release builds and package smoke checks must pass before assembling named downloads and
SHA256SUMS. Container images are smoke-tested, saved, then the identical image is pushed to
`ghcr.io/splortch/media-server:v0.2.0-alpha.N`. The `alpha` tag advances only after publication;
`latest` is never written. Containers do not update themselves on a user's machine.

Each platform's assets are uploaded to its own draft first, then that complete platform release
is made visible. Published releases are not overwritten. A retry skips completed platforms and
repairs missing platforms or same-source drafts; another-source tag is an error. All four must
exist before the coordinator considers the source commit complete. A network interruption can
leave some complete platform releases visible while the remainder await retry; it cannot expose
a half-uploaded platform release. The container alpha channel advances only after all four finish.
The Docker image keeps its version tag `v0.2.0-alpha.N` (Git tag prefixes are not Docker tags).
Source-commit provenance and the platform version are recorded in each release's `release.json`;
each SHA256SUMS covers only that release's downloads and manifest, and verifies integrity,
not publisher identity.
Compiler/package locks improve repeatability, but changing OS packages/hosted images prevent
claims of bit-for-bit reproducibility. Builds are unsigned and no stable release is published.

## Container licenses and source

The image retains project, browser and upstream package notices. Each release includes its
installed package/source-version inventory, FFmpeg license configuration, and
`Container-Corresponding-Source.tar.gz`. A separate Docker stage downloads the exact Ubuntu
source packages identified by the installed GPL/LGPL package notices (and any package with
missing notice metadata), including original source and Debian/Ubuntu changes. Publication
fails if a required source cannot be obtained. The source archive stays outside the runtime
image. Its README explains extraction and standard Debian rebuild instructions. Project source
is available at the release's source tag. Do not describe this aggregate image as wholly Apache-2.0.

## Validation and recovery

On release-related PRs the entire packaging pipeline runs without publication privileges:
self-contained server builds on Windows/Linux, extraction/startup/UI/auth/version checks,
the reusable real Windows client build, container smoke/source export, and final named assets.
Download `release-downloads` from the workflow to inspect it before merging. Policy tests cover
failed/stale/fork checks, exact source identity, retries, version overflow and metadata stamping.
This does not replace physical Windows installation/playback or Android/iOS device tests.

The initial combined alpha publication and subsequent separate platform publication succeeded.
The reviewed alpha.49 releases identify main commit 32645e5. This historical result does not
establish that a later PR's release pipeline passed; PR dry runs cannot publish Releases. Repository
and package visibility remain administrator settings: private repos require sign-in for downloads,
and a private GHCR package requires `docker login`. Make the package public in GitHub Packages
settings if anonymous installation is intended. The connector cannot change those admin settings.

To build manually, run **Automatic prereleases** from Actions. Its default is build-only. Select
`publish=true` on main to publish an already validated main commit; it cannot bypass the CI gate.
Re-running a failed release can repair its draft. To pause automatic releases, disable that workflow
in Actions; existing releases/running servers remain unaffected. Do not delete data volumes to update.

