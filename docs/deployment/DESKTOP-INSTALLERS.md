# Desktop setup files

Choose **server** to host your files, or **client** to watch an existing server.
The server always includes the web interface and runs without a desktop client.
Use the categorized [Downloads page](https://github.com/Splortch/Media-server/blob/downloads/README.md).
New targets appear as unavailable until an actual validated release is published.

| OS / CPU | Server setup | Desktop client |
| --- | --- | --- |
| Windows x64 | `win-x64-setup.exe`; portable ZIP alternative | `win-x64-setup.exe` or MSI |
| Ubuntu 24.04 x64 | `linux-x64.deb`; portable tar.gz alternative | `linux-x64.deb` |
| macOS Apple Silicon | `osx-arm64.pkg`; portable tar.gz alternative | `osx-arm64.dmg` |
| macOS Intel | `osx-x64.pkg`; portable tar.gz alternative | `osx-x64.dmg` |

Windows ARM, Linux ARM, RPM/Flatpak/AppImage and BSD installers are not built.
Linux DEBs are validated on Ubuntu 24.04, not every Linux distribution. The macOS
server follows .NET 10's currently supported macOS baseline (14+); the client sets
14 as its deployment minimum, but the hosted build runs on macOS 15. Older macOS
versions have not been tested.

## Windows

Follow [Windows server setup](WINDOWS-SERVER-INSTALLER.md) or the
[Windows client guide](../clients/WINDOWS.md). Choose one client installer, not both.
The existing Windows setup, tray controls, authentication and release identity remain.

## Linux server

1. Download the **server** `.deb` and its release `SHA256SUMS`.
2. Open Terminal in Downloads and check the matching checksum entry.
3. Run `sudo apt install ./MediaServer-v<VERSION>-linux-x64.deb`, replacing
   `<VERSION>` with the downloaded alpha version. Apt installs FFprobe/FFmpeg and
   runtime prerequisites. Do not run the server itself with sudo.
4. Launch **Media Server** from the application menu, or run
   `/opt/media-server/start-media-server.sh` in Terminal.
5. Enter an existing absolute media directory. Keep this terminal open.
6. Open http://127.0.0.1:5050. Read the file path shown after “First-run token”
   using `cat`, copy that token into web setup, and create your administrator.
7. Sign in, open Settings, add a library using the allowed media path, then scan.
8. Open a discovered movie and play a browser-compatible video.

Data stays in `${XDG_DATA_HOME:-$HOME/.local/share}/media-server`. Control-C stops
the server. Stop it before upgrading with `apt install ./<new-package>.deb`.
`sudo apt remove media-server` removes the program, preserving user data and media.
Back up the data directory before updates. This installer does not register a
root/system service or enable startup automatically.

## Linux client

1. Download the **client** `.deb`, check the checksum, then run
   `sudo apt install ./MediaServer-Desktop-Client-v<VERSION>-linux-x64.deb`.
2. Open **Media Server Client** from the application menu.
3. Enter your running server's origin, for example `https://media.example.org`.
   Local testing can use `http://127.0.0.1:5050` on the same machine.
4. Sign in, browse, and play. Progress is saved on the server for other devices.

Linux playback uses WebKitGTK/GStreamer. The DEB declares the base/good/libav
media plugins as dependencies. Additional codec needs depend on the file; this does not add transcoding. Sessions remain
in memory, so sign in again after closing the app. There is no plaintext password
storage or fallback credential file. Use the package name reported by
`dpkg-deb -f <download.deb> Package` when removing it with apt.

## macOS server

1. In Apple menu → About This Mac, check **Chip** (Apple Silicon: arm64) or
   **Processor** (Intel: x64). Download the corresponding **server PKG**.
2. Check its SHA-256 against the release's `SHA256SUMS` with `shasum -a 256`.
3. Open the PKG and complete Installer. It installs into `/Applications/MediaServer`
   and requires administrator authorization; the server runs as your own user.
4. Install FFprobe/FFmpeg using `brew install ffmpeg` after installing Homebrew
   through its official instructions. The package does not bundle FFmpeg.
5. Open `/Applications/MediaServer/Start-MediaServer.command` in Finder. Enter your
   absolute media path, then follow the same browser/token/account/library steps
   described above. Approve folder access in macOS Privacy settings if required.
6. Leave Terminal open while hosting; Control-C stops the server.

Data stays in `~/Library/Application Support/Splortch/MediaServer`. Stop before
installing an update. Remove `/Applications/MediaServer` to uninstall the program;
your data and original media remain. Both CPU packages install to the same location;
install only the package matching your Mac. A tar.gz portable alternative is available.

## macOS client

1. Download the **client DMG** matching your Mac's CPU and verify its checksum.
2. Open it, drag **Media Server Client** into Applications, eject the disk image,
   then open the app from Applications.
3. Enter your server origin and sign in. HTTPS is required beyond localhost.
4. Browse, watch, and resume playback saved by another client.

The client uses WKWebView and macOS codecs. Sessions remain in memory until exit;
persistent Keychain sessions are a follow-up, not claimed here. Move the application
to Trash to uninstall; connection/theme preferences can remain in its app-config folder.

## Signing and troubleshooting

These alpha installers are unsigned and macOS packages are not notarized. Apple
Gatekeeper may block them even when CI builds succeed. Verify the download's origin
and checksum and use Apple's per-application approval process only if you trust it;
do not globally disable Gatekeeper or TLS verification. A completely smooth public
Mac setup requires Developer ID Application/Installer certificates and notarization
credentials, kept in GitHub secrets, not source control. No signing keys are committed.

If startup fails, run the launcher/executable from Terminal to see the error. Check
FFprobe availability, media-folder permissions, supported OS/CPU and whether port
5050 is already occupied. Media directories containing symlinks/junctions are rejected; on macOS use the canonical directory path, for example `/private/var/...` rather than its `/var/...` alias. Clients require a running server; installing a client alone
does not create an account/server. Servers listen only on localhost by default;
see existing HTTPS/reverse-proxy guidance before enabling remote access.

## Build and validation

`desktop-clients.yml` builds Linux DEB plus Intel/Apple Silicon DMGs, runs renderer
and native tests against the actual backend, installs/copies each resulting bundle,
and checks process startup. `release.yml` builds self-contained server payloads,
smoke-tests their archives, builds DEB/PKG installers, installs them, tests actual
setup/auth/scanning/range streaming/progress, reinstalls, and verifies data retention.

Publication remains main-only after exact-commit required workflows pass and every
package is collected and checksummed. PR builds never publish releases. Existing
Windows/Docker/Android releases remain separate; new Linux client and macOS server/
client releases get their own platform tags and category cards. A failed package
build blocks publication rather than releasing source archives as installers.

The macOS server uses descriptor-relative no-follow opens plus the platform's
64-bit-inode fstat ABI, preserving pinned reads and rejecting symlinks/directories.
The existing file-replacement security test runs on both Unix platforms. ABI
references: [Apple fcntl.h](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/fcntl.h),
[stat.h](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/stat.h), and
[cdefs.h](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/cdefs.h).

Hosted install/process/API tests are not physical desktop testing. In particular,
Finder/Gatekeeper prompts, touch/keyboard UX and codec playback on real Macs/Linux
machines still need manual checks. See PR CI for actual results; configuration alone
is not evidence that a platform works.
