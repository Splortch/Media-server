# Windows server setup.exe

For Windows x64 releases built after this change, choose **`MediaServer-v0.2.0-alpha.N-win-x64-setup.exe`** from the **Media Server for Windows** release. This installs the **server**, including its .NET runtime and browser interface. The similarly named **MediaServer-Windows-Client** installer only installs a player. Existing releases without the server setup asset still use the portable ZIP; no historical assets are replaced.

## First installation

1. Download the server setup.exe and optionally compare its SHA-256 with that release's `SHA256SUMS`. Builds are unsigned early alphas; investigate unexpected security warnings instead of disabling Windows protection.
2. Run setup using your ordinary Windows account. It requests no administrator elevation, creates no service and opens no firewall/router ports.
3. Read the license and component choices. **Start automatically when I sign in** is optional and unchecked by default. It starts only after this user signs in; it is not a system service.
4. Finish with **Start Media Server and open first-time setup** selected. Later, use **Start → Media Server → Media Server Control**. The original Media Server shortcut remains available too.
5. Select a real media subfolder outside the application/data directories. If FFprobe is missing, choose whether to download the pinned, checksum-verified GyanD tools. This separate download needs internet access and retains its upstream GPL/LGPL licenses. Offline users can install FFprobe on PATH beforehand. No video is uploaded.
6. A local setup-token window and browser open. Copy the token into **http://127.0.0.1:5050**, create a local administrator (12–256 character password), then sign in. The token is private and is removed after successful setup.
7. In browser **Settings**, add the selected folder as a library and select **Scan library**. Folder selection grants allowed-root access; it does not automatically create or scan the library.
8. Open **Library**, select a compatible video and test playback/resume. Follow [steps 3–9 of the first-run guide](../GETTING-STARTED.md#3-create-your-administrator-and-sign-in) for expected results and troubleshooting.

## Day-to-day use

1. Open **Start → Media Server → Media Server Control**. It shows whether this local server is running or stopped. Opening the launcher for the first time starts the server as before.
2. Choose **Stop server** to turn hosting off. The controller stays open, and all connected devices lose access until you start it again.
3. Choose **Start server** to turn hosting back on, or **Restart server** to stop and start it. Saved accounts, libraries and playback progress remain in the same data directory.
4. Choose **Open in browser** to use the server. This button is disabled while stopped.
5. Closing the control window hides it; it does **not** stop hosting. Double-click the tray icon (possibly under the hidden-icons arrow), or open the Start-menu shortcut again, to bring it back.
6. To quit completely, choose **Stop server and exit control** from the tray menu. Use this before an upgrade or uninstall, which also needs the controller's lock to be released.

The tray menu also offers Start, Stop and Restart, private logs, the first-run token, and opt-in **Start when I sign in**. Closing the browser does not stop the server. Signing out of Windows ends the per-user session; this is not a Windows service. Controls apply only to the server process owned by this launcher, not a remote server or a separately started portable instance. Stopping interrupts active streams; a client may not yet have saved its latest few seconds of playback.

Opening a second shortcut reopens the existing control window instead of starting another backend. A portable instance using the same data directory must be stopped first. Port conflicts and unavailable media/tools remain startup errors; no remote shutdown endpoint or new network listener is introduced.

## Data, upgrades and uninstall

- Application files: `%LOCALAPPDATA%\Programs\Splortch\MediaServer\app`.
- Accounts, SQLite, keys, tools, preferences and logs: `%LOCALAPPDATA%\Splortch\MediaServer` — the same data location as the portable Windows launcher. Existing portable settings can be reused after stopping that server.
- Media stays in the folder you selected. Never store media or personal files inside the application directory.
- Start menu and optional Startup shortcuts, and the Apps uninstall entry, belong to the current user only.

Before updating, stop the tray server, back up its complete data directory privately and keep the previous build with the matching backup. Run the newer setup.exe. Installation does not migrate the database itself; the newly launched backend applies its normal migrations. The installer refuses an older installed alpha release number. This is not transactional migration rollback, a signed auto-updater or a substitute for a backup.

Use **Settings → Apps → Media Server (server) → Uninstall** after stopping the server. Updates/uninstall refuse to proceed while the data or tray-host lease is held. Uninstall removes only known application files, registration and its shortcuts. It deliberately preserves server data, downloaded tools, media and unknown files; it may leave an application directory containing unknown/older-version files. No recursive deletion of user-selected folders is performed. Reinstalling will find the retained account/library; it does not reset setup.

The tray writes backend output to `server.log` and `server-error.log` in the private data folder, preserving one previous pair on restart. Review disk usage for long-running instances and sanitize logs before sharing. The launcher uses a **process-only** PowerShell execution-policy override, as the portable launcher does; it changes no machine policy and never disables TLS certificate verification.

## Build and validation

The normal release workflow publishes a self-contained Windows backend and web UI, stages the portable ZIP, then compiles `release-resources/installer/server.nsi` using NSIS 3 and `scripts/build-server-installer.py`. It adds the actual setup.exe to the existing Windows **server** release with checksums, alongside its portable ZIP; Linux, Docker and Windows-client releases remain separate.

Windows CI tests the actual installer in silent mode on a disposable runner: installation/shortcut registration, real tray/backend startup, generated-video scanning through FFprobe, cookie/CSRF login, authenticated ranges, progress persistence, rejection of running upgrades/uninstall, reinstall with an unchanged database, and uninstall retaining data/media/unknown files. The fixture also clicks the actual Start/Stop/Restart button handlers through local control signals, verifies that stopping leaves the controller alive, checks that restart replaces the backend process, and rechecks persisted account/library/progress after startup. Linux policy tests cover the bounded uninstall manifest and release completeness. Passing automation does not establish physical installer interaction, screen-reader behavior, SmartScreen reputation or every codec/GPU path.

For local Windows build development, first stage the real `win-x64` release using the release workflow's publish/package steps, set `SERVER_VERSION`, install NSIS 3 (or set `MAKENSIS`), then run:

```powershell
python scripts/build-server-installer.py
# Disposable test machine only; refuses existing application/data directories:
./scripts/test-server-installer.ps1 -Installer (Resolve-Path "artifacts/packages/MediaServer-v$env:SERVER_VERSION-win-x64-setup.exe")
```

The artifact filename is versioned to distinguish server/client and architecture. You may save it locally as `setup.exe`; its payload remains the same. Actual publication follows a reviewed merge and the existing exact-commit CI gate; a PR build does not publish a release.
