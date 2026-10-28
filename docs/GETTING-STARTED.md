# Your first Media Server setup

This guide is for trying the alpha on **one computer first**. Start with a small folder containing copies of a few videos you own. You do not need to write code, buy a subscription, or install the Windows client to use the browser interface.

The original walkthrough was checked against source `32645e5` and the `alpha.49` downloads on 28 October 2026. Use the version shown in your release if newer. [PR #55](https://github.com/Splortch/Media-server/pull/55) has since merged account management, monitoring, library watching and backup tools into source; those features require a newer build whose source includes that merge. They are absent from alpha.49. The basic setup/playback steps below apply to both.

## 1. Choose what to install

| Your goal | Choose | What it does |
| --- | --- | --- |
| Try it on a Windows x64 PC | **Windows server setup.exe (when available), or ZIP** | Runs the server and includes the browser interface. Recommended first test. |
| Run on Ubuntu 24.04 x64 | **Linux server DEB**, or portable tar.gz | DEB installs prerequisites; both include the web interface. See the [installer guide](deployment/DESKTOP-INSTALLERS.md#linux-server). |
| Run on a Mac | **macOS server PKG** for Apple Silicon or Intel | Self-contained server and browser interface; install FFprobe separately. See the [Mac guide](deployment/DESKTOP-INSTALLERS.md#macos-server). |
| You already use Docker | **Docker setup ZIP** | Runs a prebuilt Linux amd64 image through Docker Compose. |
| Watch through a dedicated Windows window | **Windows client EXE or MSI** | Connects to an already running server. Install later, after browser playback works. |

Linux DEB and macOS DMG desktop clients connect to an existing server; see [desktop setup](deployment/DESKTOP-INSTALLERS.md). The [Android phone alpha](clients/ANDROID.md) has an installable testing APK. Android TV, iOS, Windows ARM and Linux ARM packages are not built. New installers appear only when their CI validation and release publication succeed; the [categorized Downloads page](https://github.com/Splortch/Media-server/blob/downloads/README.md) shows actual availability. The web interface is bundled; there is no separate web installer. GitHub's **Source code (zip/tar.gz)** assets are for developers, not runnable applications.

Open [Releases](https://github.com/Splortch/Media-server/releases). Releases are separate by platform, so the first release on the page may not be yours. Choose the matching title and read its notes. Download its application asset and `SHA256SUMS` if you want to verify integrity. These alpha builds are unsigned: a checksum is not a publisher signature. Do not disable Windows protection or antivirus to install them; stop and investigate unexpected warnings.

Choose **one** server installation route below. Do not start native and Docker servers together on port 5050.

## 2A. Windows: recommended first installation

If your Windows **server** release includes `MediaServer-vVERSION-win-x64-setup.exe`, use the [server installer walkthrough](deployment/WINDOWS-SERVER-INSTALLER.md). It installs a Start menu/tray controller without administrator access. Then return to step 3 below. Older releases and portable installations use the ZIP steps that follow. The Windows **client** setup.exe is a separate application.

1. Confirm your PC runs Windows x64. ARM64 Windows is not a validated target for these downloads.
2. Create a test folder, for example `C:\MediaServer-TestMedia`, and copy a compatible video into it. Do not select all of `C:\`, a shortcut/junction, a cloud-only file, or a disconnected external drive.
3. Download the asset named like `MediaServer-v0.2.0-alpha.49-win-x64.zip` from **Media Server for Windows**.
4. Optional integrity check: open PowerShell in Downloads, run the following and compare the full hash with the matching filename in `SHA256SUMS`:

   ```powershell
   Get-FileHash .\MediaServer-v0.2.0-alpha.49-win-x64.zip -Algorithm SHA256
   ```

5. Use **Extract All**. Open the extracted application folder containing `Api.exe` and `Start-MediaServer.cmd`. Do not launch it from inside the ZIP.
6. Double-click **Start-MediaServer.cmd**, not `Api.exe`. The launcher configures data storage, the allowed media root and FFprobe.
7. In the folder picker, choose `C:\MediaServer-TestMedia` (or your own test folder). This permits access; it does **not** add or scan the library yet.
8. If FFprobe is missing, the launcher offers a pinned, checksum-verified FFmpeg-tools download from GyanD on GitHub. Read the prompt and choose whether to download. This needs an internet connection. Alternatively, install FFprobe yourself, make it available on PATH, and restart the launcher. Native archives do not include FFprobe.
9. Wait for startup. The console should show the data location and, on a fresh server, a **First-time setup token**. A browser should open at **http://127.0.0.1:5050**. If it does not, open that address yourself on this PC.
10. Keep the console open. It is the running server, not a completed installer.

**Checkpoint:** open [http://127.0.0.1:5050/health](http://127.0.0.1:5050/health). You should see `{"status":"ok"}`. The main address should show setup or sign-in. Then continue with step 3.

Windows data is stored in `%LOCALAPPDATA%\Splortch\MediaServer`, outside the extracted application. You can paste that path into File Explorer. It contains the database, protection keys, `launcher.json` and the setup token until setup succeeds. Keep it private. The launcher currently uses this fixed location even if you previously set a different `MediaServer__DataDirectory` environment variable; custom native configuration requires direct launch as described in [development setup](development/SETUP.md).

## 2B. Linux server

For an installer on Ubuntu 24.04 x64, follow the [server DEB steps](deployment/DESKTOP-INSTALLERS.md#linux-server), then continue at step 3. The following steps are the portable archive alternative.

These commands target an Ubuntu-style x64/glibc system. Other distributions need their equivalent package manager and .NET native dependencies; Alpine/musl and ARM64 are not supported by this archive.

1. Create a real test directory under your home directory and add a compatible video. Use its absolute path, for example `/home/alex/MediaServer-TestMedia`.
2. Download the **Linux server** tar.gz and optionally `SHA256SUMS`. In the download directory:

   ```bash
   sha256sum MediaServer-v0.2.0-alpha.49-linux-x64.tar.gz
   ```

   Compare with the matching checksum, then extract and enter the application folder:

   ```bash
   tar -xzf MediaServer-v0.2.0-alpha.49-linux-x64.tar.gz
   cd MediaServer-v0.2.0-alpha.49-linux-x64
   ```

3. Install and check FFprobe:

   ```bash
   sudo apt update
   sudo apt install ffmpeg
   ffprobe -version
   ```

4. Start the server as your ordinary user, not with `sudo`:

   ```bash
   ./start-media-server.sh
   ```

5. At the prompt, enter the existing absolute test-folder path. Leave this terminal open.
6. In a browser on that computer, open **http://127.0.0.1:5050**. In a second terminal, read the token:

   ```bash
   cat "${XDG_DATA_HOME:-$HOME/.local/share}/media-server/setup-token"
   ```

   If you explicitly overrode `MediaServer__DataDirectory`, use the path printed by the launcher instead. A headless server needs a separately secured access arrangement; do not change the listener to a public address just to follow this guide.

**Checkpoint:** `/health` returns `{"status":"ok"}`. Continue with step 3. If execution is denied, check the extracted `Api` and launcher execute permissions and whether the disk is mounted `noexec`. Do not run the server as root to bypass a permissions problem.

## 2C. Docker: prebuilt release

Use Docker Engine with Compose on Linux, or Docker Desktop in **Linux containers** mode on Windows. Confirm `docker version` and `docker compose version` work. No Node.js, .NET SDK or separate FFprobe installation is needed for this route.

1. Download **MediaServer-Docker-v0.2.0-alpha.N.zip**, extract it, and open a terminal in the folder containing `compose.yaml` and `.env.example`.
2. Copy the example configuration:

   ```powershell
   # Windows PowerShell
   Copy-Item .env.example .env
   ```

   ```bash
   # Linux
   cp .env.example .env
   ```

3. Edit `.env` in a text editor. Set an existing host folder, for example:

   ```dotenv
   # Windows Docker Desktop: use forward slashes
   MEDIA_PATH=C:/MediaServer-TestMedia
   ```

   Or on Linux: `MEDIA_PATH=/home/alex/MediaServer-TestMedia`. Save as `.env`, not `.env.txt`. Ensure Docker Desktop can access that drive/folder.
4. Start and inspect the service:

   ```bash
   docker compose up -d
   docker compose ps
   docker compose logs --tail=80 media-server
   docker compose exec media-server cat /data/setup-token
   ```

5. Open **http://127.0.0.1:5050** on the Docker host. Keep the token private.

**Checkpoint:** the service remains running and `/health` returns `{"status":"ok"}`. Continue with step 3, but use **`/media`** as the library directory. That is the read-only container mount; entering `C:\...` or your Linux host path inside the web UI will fail.

If image pulling says unauthorized, the GHCR package may still be private even after the repository becomes public. A maintainer must make that package public for anonymous pulls, or you must authenticate using GitHub's supported registry login. Do not paste tokens into a Compose file or issue. As a source-build alternative, clone the repository and follow the README's `docker compose up --build -d` route; it is a different Compose file from the release ZIP.

The `server-data` named volume stores accounts, database and keys. Keep the Compose directory/project name stable across restarts and updates. **Never use `docker compose down -v` as a troubleshooting step**: it deletes the data volume. Media is mounted separately and read-only.

## 2D. macOS server

Follow the [macOS server PKG steps](deployment/DESKTOP-INSTALLERS.md#macos-server), choosing Apple Silicon or Intel as appropriate. The guide covers FFprobe, the Terminal launcher, the setup token, updates and signing limitations. Then continue below.

## 3. Create your administrator and sign in

1. Open **http://127.0.0.1:5050**. Use this same hostname throughout the test rather than alternating with `localhost`.
2. On **Set up your server**, enter the locally generated setup token, choose a username and a password of **12–256 characters**, and select **Create administrator**. This is a local server account, not your GitHub account.
3. Setup then displays **Sign in**. Enter the same username and password again; setup does not automatically sign you in.
4. You should reach Home. An empty library at this point is expected.

The token is removed after successful setup. Restarting should display sign-in, not create another account. A missing token on an already configured server is normal. Never post tokens, passwords, browser cookies or protection keys when asking for help.

If setup is already complete, use your existing account. If you forgot its password, follow [offline administrator recovery](architecture/HARDENING.md#setup-and-credentials); do not delete the database or try to recreate the token.

## 4. Add a library and scan it

1. Select **Settings** in the browser interface.
2. Under **Add a media directory**, enter a **Library name**, for example `Test Movies`.
3. In **Directory on the server**, enter the actual allowed directory:

   | Installation | Example library directory |
   | --- | --- |
   | Windows launcher | `C:\MediaServer-TestMedia` — the folder you selected |
   | Linux launcher | `/home/alex/MediaServer-TestMedia` — the path you entered |
   | Docker | `/media` |

4. Select **Add library**. The library should appear under **Media libraries**.
5. Select its **Scan library** button. Wait for the job to change from `queued`/`running` to `completed`. Inspect the identified and failed counts; `completed` can still include individual failed files.
6. If failures appear, expand **Scan problems**. It reports filename, reason and a diagnostic reference; check FFprobe availability before assuming the library is empty.
7. Select **Library** after the scan finishes, or refresh it. You should see cards named after video filenames. No downloaded posters, online descriptions or TV episode matching are expected yet.

The scanner considers `.mp4`, `.m4v`, `.webm`, `.mkv`, `.mov` and `.avi` files, recursively. Being scanned does **not** mean a browser can decode the file. Audio-only files and corrupt files cannot become playable videos. Scanning reads originals; it does not convert, rename or delete them. In alpha.49, rescanning is manual and removed-file entries may remain visible. Newer builds including PR #55 add opt-in watching and reconciliation; an explicit first scan is still a useful setup check.

## 5. Play a video and verify resume

1. Choose a test video longer than about 30 seconds. Start with VP9/Opus WebM or H.264/AAC MP4; support still depends on browser, WebView2 and OS codecs. An `.mp4` extension alone does not guarantee H.264/AAC.
2. Open its details card and press Play. Confirm a moving picture and sound.
3. Use the player controls to pause/resume, seek forward, change volume and enter/leave fullscreen.
4. Open **Playback diagnostics** if it stalls. It shows the container/codecs and buffering information. This alpha does not transcode or provide HLS, alternate audio/subtitle selection, HDR conversion or universal codec support.
5. Play to roughly one-third of the video, **pause**, wait a few seconds, then return to Home. Avoid reaching the last 5% during this check; that counts as completed.
6. Confirm the title appears in **Continue Watching**. Reopen it and verify that playback starts near the saved position.
7. Refresh the browser and repeat. A progress-synchronization warning means persistence was not confirmed: reopen the video before trying again, rather than assuming the position was saved.

**Optional synthetic video:** if FFmpeg is installed on PATH, open a terminal in your test media directory and run this single-line command. It creates its own picture and sound, uses no copyrighted input and refuses to overwrite an existing file:

```bash
ffmpeg -n -f lavfi -i testsrc2=size=640x360:rate=24 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 60 -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -threads 2 -b:v 600k -pix_fmt yuv420p -c:a libopus -shortest sample.webm
```

Then scan again. The Windows launcher's downloaded tools are not automatically added to PATH. If you accepted that download, run its `ffmpeg.exe` from `%LOCALAPPDATA%\Splortch\MediaServer\tools\ffmpeg-8.1.2-essentials_build\bin` by its full path instead of `ffmpeg`. If your FFmpeg build lacks these encoders, use a known-compatible video rather than modifying an original file.

## 6. Optional Windows desktop client

Do this only after browser playback works. It helps separate server problems from desktop playback problems.

1. Keep your server running. On the **same PC**, install either the Windows client `...-setup.exe` **or** `.msi` from its separate release, not both. These are unsigned alpha installers. WebView2 is required; installation/runtime issues need investigation rather than disabling Windows security.
2. Launch the client. In **Server address**, enter **`http://127.0.0.1:5050`** — no `/api`, `/library` or media-file path.
3. Select **Connect**, then sign in with the server account created in step 3. Optional remembered sessions use Windows Credential Manager; the password is not saved.
4. Browse, play the same video and check seeking/fullscreen.
5. Pause around halfway and wait for synchronization. Return to the browser, refresh Home and confirm the new resume position. Pause one player before opening another to avoid progress conflicts.

The client does not install or start a server. Closing the server console stops playback for all clients. WebView2 codec support can differ from your browser; successful installer builds do not establish every codec or GPU path works on your PC.

`127.0.0.1` always means **the device you are using**. On a phone or a second PC it does not point to your server. Native launchers bind to loopback by default, and the Windows client rejects non-local HTTP. Cross-device access therefore needs deliberate HTTPS, trusted certificates, exact allowed hosts and network configuration. It is not a one-click alpha feature. First complete this local test; do not forward router ports or expose port 5050 to the internet. See [security and remote-access limits](architecture/SECURITY.md).

## 7. Stop, restart and keep your data

| Route | Stop | Start again |
| --- | --- | --- |
| Windows native | Press Ctrl+C in the launcher console; confirm the server exits | Run the same `Start-MediaServer.cmd` |
| Linux native | Ctrl+C in the server terminal | Run `./start-media-server.sh` again |
| Docker | `docker compose stop` | `docker compose up -d` from the same Compose project |

After restarting, sign in if needed. The library and resume point should remain. If setup reappears unexpectedly, stop and check that you selected the same data directory/Compose project; do not create a new administrator over what you think is your existing installation.

Before upgrading, stop the server and back up the **entire data directory/volume**, including SQLite sidecars and keys, to a private location. Back up media separately. Keep the old runnable build together with its data backup; do not launch an older binary against a database migrated by a newer version. The published alpha.49 builds do not contain PR #55's `--backup`/managed-update commands. See [deployment](deployment/DEPLOYMENT.md#backup-and-updates).

For a different Windows allowed media folder, stop the server, privately back up and edit `launcher.json` in its data directory, then restart. On Linux the saved folder is in `media-root`. Removing just that preference file while stopped allows the launcher to ask again; **do not remove the database or keys**. Previously added libraries outside the newly allowed root will stop working. Additional allowed roots need explicit process configuration.

## 8. Quick troubleshooting

| Symptom | What to check |
| --- | --- |
| Browser says connection refused | Keep the server console open; check startup output, port 5050 and `/health`. Do not start another instance against the same data directory. |
| Launch fails to lock the data directory | Stop the other server/recovery process. Do not delete `.server.lock` while a process owns it; the file may remain after a normal exit. |
| FFprobe missing or every scan fails | Run `ffprobe -version`, reconnect the tools/media drive, or check the verified downloader's error. Native and Docker have different tool locations. |
| Library path rejected | Use a real absolute directory beneath the configured root. Avoid filesystem roots, symlinks/junctions and cloud-only folders. Docker requires `/media`. |
| No files identified | Confirm supported extensions, readable local files, a completed scan and that the folder actually contains video. Check scan problems and refresh Library. |
| Video appears but does not play | Inspect codec/container and diagnostics. Try the synthetic WebM or known H.264/AAC MP4; scanning success does not imply decoding support. No transcoding fallback exists. |
| Wrong password / 401 | Setup and login are separate. Use the local account. Reload after an expired/revoked session; use documented offline recovery if needed. |
| 400 after changing sessions | Reload to obtain fresh cookies/CSRF state, using the same hostname. Do not disable CSRF. |
| 429 after repeated login attempts | Wait at least a minute; authentication is rate-limited. |
| Resume did not save | Pause before navigating, wait, check for a sync warning and reopen. Avoid simultaneous players updating the same title. |
| Docker permission denied reading media | Grant the container user read/traverse access or Docker Desktop folder sharing. Do not use `chmod 777` or make the media mount writable. |
| Docker image pull unauthorized | Package visibility is separate from repository visibility; see step 2C. |
| New setup screen after an update | Check the data-directory/volume/project identity before proceeding. Preserve the old data. |
| Other devices cannot connect | Expected with default loopback binding; remote access needs a separately reviewed HTTPS configuration. |

When reporting a problem, include platform, server/client version, installation route, browser/WebView2 version, exact step, expected/actual result and sanitized error/reference. Review logs and screenshots for filenames, personal paths, tokens and cookies before sharing. Never upload your database or key ring.

## 9. Your first hands-on acceptance checklist

Record pass/fail and versions when you try it. These boxes are intentionally unchecked; CI is not your PC.

- [ ] Downloaded the correct server asset and extracted it.
- [ ] Launcher starts; `/health` is OK and the web UI loads.
- [ ] Created the administrator and signed in.
- [ ] Added the allowed test folder and completed a scan.
- [ ] Discovered a real or synthetic video; original file remains unchanged.
- [ ] Picture, sound, pause, seek, volume and fullscreen work.
- [ ] Resume survives page refresh and server restart.
- [ ] A signed-out private browser window cannot browse or play private media.
- [ ] Optional Windows client connects, plays and shares the resume point.
- [ ] Stopped/restarted without losing the library or accidentally reopening setup.

Known review findings and the limits of automated validation are in [PROJECT-REVIEW.md](development/PROJECT-REVIEW.md). Developer/source setup is in [SETUP.md](development/SETUP.md); it requires .NET/Node tooling and is not needed for downloaded builds.


See [desktop installers for Windows, Linux and macOS](deployment/DESKTOP-INSTALLERS.md) for server/client package selection and step-by-step setup.
