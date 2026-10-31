# Releases and downloads

**[Browse the latest downloads by platform](https://github.com/Splortch/Media-server/blob/downloads/README.md)** · **[All GitHub releases](https://github.com/Splortch/Media-server/releases)** · **[Step-by-step setup](docs/GETTING-STARTED.md)**

Choose a **server** to host your media, or a **client** to connect to an existing
server. Every server includes the browser interface. Installing a client does not
install a server.

## Servers

| Platform | Packages | Latest published downloads |
| --- | --- | --- |
| Windows x64 | `setup.exe` installer or portable ZIP | [Windows server](https://github.com/Splortch/Media-server/blob/downloads/README.md#windows-server) |
| Linux x64 | DEB installer or portable archive | [Linux server](https://github.com/Splortch/Media-server/blob/downloads/README.md#linux-server) |
| macOS Intel / Apple Silicon | PKG installers or portable archives | [macOS server](https://github.com/Splortch/Media-server/blob/downloads/README.md#macos-server) |
| Docker (Linux amd64) | Compose setup and prebuilt image | [Docker](https://github.com/Splortch/Media-server/blob/downloads/README.md#docker-server) |

## Clients

| Platform | Packages | Latest published downloads |
| --- | --- | --- |
| Windows x64 | EXE or MSI installer | [Windows client](https://github.com/Splortch/Media-server/blob/downloads/README.md#windows-desktop-client) |
| Linux x64 | Ubuntu 24.04 DEB | [Linux client](https://github.com/Splortch/Media-server/blob/downloads/README.md#linux-desktop-client) |
| macOS Intel / Apple Silicon | DMG installers | [macOS client](https://github.com/Splortch/Media-server/blob/downloads/README.md#macos-desktop-client) |
| Android phone | Installable debug APK; unsigned release APK/AAB for signing | [Android client](https://github.com/Splortch/Media-server/blob/downloads/README.md#android-phone-client) |
| Web browser | Included with your server; no separate installer | [Browser access](https://github.com/Splortch/Media-server/blob/downloads/README.md#browser) |

The download catalog groups servers and clients separately and keeps older builds
collapsed. Its automatically generated links show actual published availability,
so this page does not hardcode version numbers or promise an unpublished build.
Android TV, iOS, and ARM Windows/Linux installers remain planned.

## Before installing or updating

These are early **alpha** releases. Read the notes on the selected platform's
release page, choose the correct architecture, and back up server data before
updating. Use that release's `SHA256SUMS` to check download integrity. Unsigned
artifacts are identified in the release notes; a checksum is not a publisher
signature. GitHub's automatic source ZIP/tar downloads are source code, not
installers.

- [Windows server installer and updates](docs/deployment/WINDOWS-SERVER-INSTALLER.md)
- [Desktop and other server installers](docs/deployment/DESKTOP-INSTALLERS.md)
- [Android installation and signing](docs/clients/ANDROID.md)
- [Versions, release automation and publication rules](docs/deployment/RELEASES.md)
- [Project changelog](CHANGELOG.md)
