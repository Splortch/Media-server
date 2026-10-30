# Media Server alpha — start here

Follow the [step-by-step first setup guide](https://github.com/Splortch/Media-server/blob/main/docs/GETTING-STARTED.md) for expected results, testing playback/resume, and troubleshooting. It distinguishes released capabilities from unmerged work.

This folder contains a runnable self-contained server and its web UI. You do not need the .NET
SDK or Node.js. This is early alpha software; back up server data before updating.

Windows installed server: use the tray icon or Start menu. See the [setup.exe guide](https://github.com/Splortch/Media-server/blob/main/docs/deployment/WINDOWS-SERVER-INSTALLER.md). Portable Windows: double-click **Start-MediaServer.cmd**. On first use choose the media folder the server
may read. If FFprobe is missing, the launcher offers a pinned, checksum-verified media-tool
download from GyanD. It then opens http://127.0.0.1:5050 and displays your local setup token.
Create the administrator in the browser. In Settings add the selected folder as a library,
scan it, then play a compatible video. Leave the console open while the server is running.

Linux: install FFprobe (Ubuntu: `sudo apt install ffmpeg`), then run `./start-media-server.sh`.
Choose an absolute media folder, open http://127.0.0.1:5050 and read the setup-token file at the
printed location. For a preconfigured FFprobe/data/media root, export MediaServer__FfprobePath,
MediaServer__DataDirectory and MediaServer__AllowedRoots__0 before running.

Linux DEB: install with `sudo apt install ./MediaServer-v<VERSION>-linux-x64.deb`,
then open Media Server from the application menu. Apt installs FFprobe; the server
itself runs as your signed-in user, not root.

macOS (14+ server): choose osx-arm64 for Apple Silicon or osx-x64 for Intel. Install
the PKG, install FFprobe with `brew install ffmpeg`, then open
`/Applications/MediaServer/Start-MediaServer.command`. For portable use, extract the
archive and open its Start-MediaServer.command. Choose your media folder, open
http://127.0.0.1:5050, and read the setup-token file printed in Terminal. Keep Terminal
open; Control-C stops the server. Data stays in
`~/Library/Application Support/Splortch/MediaServer`. Alpha PKGs are unsigned and not
notarized; use Apple's per-application approval only if you trust the verified download.

See the included **DESKTOP-INSTALLERS.md** for setup, uninstall and troubleshooting.

Data lives outside this archive: Windows uses %LOCALAPPDATA%\Splortch\MediaServer, Linux uses
${XDG_DATA_HOME:-$HOME/.local/share}/media-server. Replacing the extracted application does not
replace that data. Back it up before upgrades. Database migrations may prevent safe downgrades.
You may also launch Api.exe/Api directly with explicit environment configuration; see the full docs.

The separate Windows desktop installer is a client: connect it to a running server. The web UI
already works without installing that client. All installers are unsigned; Windows may display
publisher/SmartScreen warnings. No automatic public-internet access or port forwarding is enabled.
Use HTTPS and the documented remote-access configuration before connecting remote devices.

Project code is Apache-2.0; dependencies retain their licenses. Native archives do not bundle
FFmpeg/FFprobe. Browser notices are included in licenses/. The separate Docker release includes
Ubuntu FFmpeg with its own license configuration, package inventory and corresponding source
archive. See docs/deployment/RELEASES.md in the source repository for distribution details,
checksums, source commit and the complete installation/update guide.


## Dependency inventory

Native server archives and installers include `inventory/SBOM.spdx.json`, an
SPDX 2.3 dependency metadata inventory, and `THIRD-PARTY-INVENTORY.json` with copied
license/notice evidence. These are generated from the exact restored NuGet graph
and installed production browser packages before packaging. Archive checksums
cover these files too. The inventory includes NuGet build/design packages, so
listing a dependency does not establish that its code is redistributed. Microsoft
runtime/OS components and native client dependencies are not fully inventoried by
this first generator. Native server packages do not bundle FFmpeg; separately
downloaded tools have their own notices. Container package/source evidence is
published separately. See [distribution evidence](DISTRIBUTION-EVIDENCE.md).
