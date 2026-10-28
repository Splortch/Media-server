# Windows server setup.exe

For Windows x64 releases built after this change, choose **`MediaServer-v0.2.0-alpha.N-win-x64-setup.exe`** from the **Media Server for Windows** release. This installs the **server**, including its .NET runtime and browser interface. The similarly named **MediaServer-Windows-Client** installer only installs a player. Existing releases without the server setup asset still use the portable ZIP; no historical assets are replaced.

## First installation

1. Download the server setup.exe and optionally compare its SHA-256 with that release's `SHA256SUMS`. Builds are unsigned early alphas; investigate unexpected security warnings instead of disabling Windows protection.
2. Run setup using your ordinary Windows account. It requests no administrator elevation, creates no service and opens no firewall/router ports.
3. Read the license and component choices. **Start automatically when I sign in** is optional and unchecked by default. It starts only after this user signs in; it is not a system service.
4. Finish with **Start Media Server and open first-time setup** selected. Later, use **Start → Media Server → Media Server**.
5. Select a real media subfolder outside the application/data directories. If FFprobe is missing, choose whether to download the pinned, checksum-verified GyanD tools. This separate download needs internet access and retains its upstream GPL/LGPL licenses. Offline users can install FFprobe on PATH beforehand. No video is uploaded.
6. A local setup-token window and browser open. Copy the token into **http://127.0.0.1:5050**, create a local administrator (12–256 character password), then sign in. The token is private and is removed after successful setup.
7. In browser **Settings**, add the selected folder as a library and select **Scan library**. Folder selection grants allowed-root access; it does not automatically create or scan the library.
8. Open **Library**, select a compatible video and test playback/resume. Follow [steps 3–9 of the first-run guide](../GETTING-STARTED.md#3-create-your-administrator-and-sign-in) for expected results and troubleshooting.

## Day-to-day use

The server runs with a **notification-area/tray icon**, possibly under Windows' hidden-icons arrow. No console needs to remain open. Double-click it, or use **Open Media Server**, to open the bundled browser UI. Its menu also shows the first-run token when applicable, opens private data/logs, toggles sign-in startup and provides **Stop server and exit**. Closing a browser tab does not stop the server. Signing out of Windows stops the per-user session; always-on hosting needs a future service design.

Starting the shortcut again while the tray host is already running opens the browser. It does not start another server. A running portable instance using the same data directory must be stopped first. Port conflicts and unavailable media/tools are startup errors rather than reasons to expose a new network listener.

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

Windows CI tests the actual installer in silent mode on a disposable runner: installation/shortcut registration, real tray/backend startup, generated-video scanning through FFprobe, cookie/CSRF login, authenticated ranges, progress persistence, rejection of running upgrades/uninstall, reinstall with an unchanged database, and uninstall retaining data/media/unknown files. The fixture also exercises cooperative tray stop. Linux policy tests cover the bounded uninstall manifest and release completeness. Passing automation does not establish physical installer interaction, screen-reader behavior, SmartScreen reputation or every codec/GPU path.

For local Windows build development, first stage the real `win-x64` release using the release workflow's publish/package steps, set `SERVER_VERSION`, install NSIS 3 (or set `MAKENSIS`), then run:

```powershell
python scripts/build-server-installer.py
# Disposable test machine only; refuses existing application/data directories:
./scripts/test-server-installer.ps1 -Installer (Resolve-Path "artifacts/packages/MediaServer-v$env:SERVER_VERSION-win-x64-setup.exe")
```

The artifact filename is versioned to distinguish server/client and architecture. You may save it locally as `setup.exe`; its payload remains the same. Actual publication follows a reviewed merge and the existing exact-commit CI gate; a PR build does not publish a release.
