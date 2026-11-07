# Automatic alpha releases and easy installation

Every successful main commit can publish one combined **prerelease** automatically. No release tag, personal token or signing key is needed.
GitHub's built-in repository token publishes Releases and the GHCR container image.
Organization policy must permit repository Actions to write Releases and Packages.

Maintainers can assign Beta or Stable status to an existing build without
rebuilding packages: see [GitHub release promotion](RELEASE-PROMOTION.md).

## Download and run

New users: follow [Your first Media Server setup](../GETTING-STARTED.md) for numbered instructions, expected results and troubleshooting. The [project review](../development/PROJECT-REVIEW.md) distinguishes automated evidence from hands-on testing still required.

Open the repository's **Releases** page and choose the newest alpha for your platform. These are runnable builds,
not source zips. No .NET SDK is needed by end users. The web UI is included in all server builds
and the Docker image; there is no browser-only file:// application to install.

| Download | What to do |
| --- | --- |
| `MediaServer-v0.2.0-alpha.N-win-x64-setup.exe` | Install the per-user server, start its tray controller, then complete the browser setup. See [server installer](WINDOWS-SERVER-INSTALLER.md). Available only in releases that actually contain this asset. |
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

Server packages: Windows x64, Linux x64 DEB/archive, and macOS Intel/Apple Silicon
PKG/archive. Desktop clients: Windows EXE/MSI, Ubuntu 24.04 x64 DEB, and macOS
Intel/Apple Silicon DMG. Android phone APK/AAB is validated by a reusable build and included in the same release.
Web is bundled with each server; Docker remains Linux amd64. Availability depends on
successful CI and a published release containing the actual package. Android TV/iOS,
Linux ARM and Windows ARM installers are not generated.

## One release for every platform

Each validated commit creates one GitHub Release named **Media Server 0.2.0-alpha.N**,
with tag `v0.2.0-alpha.N`. Its description links directly to every attached platform download:

| Platform | Downloads |
| --- | --- |
| Windows server | Windows x64 setup.exe and portable ZIP |
| Linux server | Linux x64 DEB and tar.gz |
| Windows desktop client | EXE and MSI alternative installers |
| macOS server | Intel and Apple Silicon PKGs and portable archives |
| Linux desktop client | Ubuntu 24.04 x64 DEB |
| macOS desktop client | Intel and Apple Silicon DMGs |
| Android phone client | Installable test APK, unsigned release APK and AAB |
| Docker server | Compose setup ZIP, package/license inventory and corresponding source |

The web client is bundled with each server. Android TV/iOS remain planned. Existing
platform-specific releases remain in the archive; future builds do not create new ones.
GitHub adds source archive links automatically; those are not application builds.

## Publication rules

`Automatic prereleases` listens for completion of the six required main workflows (including Desktop clients). It requires
the latest run of **every** required workflow to pass on the exact same main commit and current
repository. PR/fork upstream runs, failed/pending checks, stale commits and lookalike workflow
paths cannot authorize publication. The final publisher checks main again before publication.

The Windows CI run number N supplies deterministic versions for the commit: server
`0.2.0-alpha.N`, client `0.1.0-alpha.N`, MSI `0.1.N`. The first two MSI version fields stay stable
and its third field increases, allowing MSI to recognize newer alpha installers. The value
must fit 1..65535; the workflow fails before overflow instead of wrapping. Application config,
Cargo package/lock metadata, assembly versions and download names are stamped consistently.
The release uses the server version; desktop and Android clients retain their 0.1.0 component
versions with the same validated source commit and build counter. This is not a stable-release process.

All release builds and package smoke checks must pass before assembling named downloads and
SHA256SUMS. Container images are smoke-tested, saved, then the identical image is pushed to
`ghcr.io/splortch/media-server:v0.2.0-alpha.N`. The `alpha` tag advances only after publication;
`latest` is never written. Containers do not update themselves on a user's machine.

All platform assets upload to one private draft. Only after every upload succeeds and main
still points to the validated source commit does the publisher make that release visible.
A retry replaces files only in a same-source draft; published assets are not overwritten.
Another-source tag is an error. The planner checks the single combined tag to avoid duplicate
builds. A failed build or upload cannot expose a partially uploaded release. The Docker alpha
channel advances only after the combined release publishes.

The shared `release.json` records the exact source commit, server/desktop/Android component
versions and container image. One `SHA256SUMS` covers all downloads and the manifest; it
verifies integrity, not publisher identity.
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


## Independent Android phone prereleases

The Android client workflow runs independently for Android/API/backend changes and can also be called by `Automatic prereleases`. The coordinator always calls it for the exact release commit, passing the shared build counter to Gradle and packaging. APK assembly, lint/JVM tests and real-backend emulator playback must all pass before the `release-android` artifact can join the other release downloads. Android never publishes a separate release. Debug APKs use an ephemeral testing key; unsigned APK/AAB files require signing. PR/manual artifacts remain available from Actions.

Install the `-debug.apk` for testing. `-release-unsigned.apk` needs a production signature before installation; `-release-unsigned.aab` is not directly installable. Each release includes SHA256SUMS and source identity. Debug signing keys are ephemeral; uninstall may be required between runs. See [Android setup/signing](../clients/ANDROID.md). No signing keys are committed. Publication stages all assets in a draft, checks hashes and current main, then publishes; it never changes an existing published Android release.

## Find every supported platform

New releases contain a direct download table for their own platform assets. The catalog recognizes both combined releases and historical platform tags, distinguishes server/client installers, and shows each component's actual version. Older platform releases keep navigation to available builds; combined descriptions are refreshed from their attached assets so they never advertise a missing installer.

The web interface is included with the server packages. Android TV and iOS do not yet have installable builds or releases. Android's debug APK is the testing installer; its unsigned release APK/AAB need signing.

The Release descriptions workflow refreshes existing descriptions when this change reaches main, after successful main release/Android workflows, daily, or by manual dispatch. It preserves Android-specific instructions, platform assets, tags, signatures, versions and prerelease status. It updates descriptions only; it does not fabricate or rebuild missing artifacts. PR runs test the table and synchronization logic with read-only permissions and cannot edit published releases.

## Categorized download catalog

Use **[Browse by platform](https://github.com/Splortch/Media-server/blob/downloads/README.md)** to see Servers and Clients separately. Each platform has one latest-build entry and its own collapsed older-version history, so accumulated Windows or Android releases cannot hide another platform. GitHub's native Releases feed remains the complete archive.

The `Download catalog` workflow refreshes the generated `downloads` branch from actual published releases after validation. This works as a GitHub Markdown page without Pages configuration. The workflow also produces a responsive HTML preview; hosting that preview is separate from the always-available GitHub category index. See [catalog development and publication](../../downloads/README.md).


See [desktop installers for Windows, Linux and macOS](DESKTOP-INSTALLERS.md) for server/client package selection and step-by-step setup.
