# Android phone client alpha

The native Kotlin application lives in `clients/android`: a reusable `core` Android library for API/security, plus a Compose phone application using Media3/ExoPlayer. It connects to the same independent server as the web and Windows clients; it does not install or bundle a server.

## Install and connect, step by step

1. Install and start the server using [First run](../GETTING-STARTED.md). Finish administrator setup in the browser, configure a media directory, and scan a small, legally obtained test video. Verify it appears in the web library first.
2. Open the **Android client** workflow in the repository's Actions tab. Choose a successful run for the Android PR/commit and download the `android-build` artifact. Extract the ZIP and locate `apk/debug/app-debug.apk`. After merge, successful main builds publish an independent `client-android/v0.1.0-alpha.N` prerelease with clearly named debug APK/unsigned APK/AAB files and checksums. Release publishing is separate from PR artifacts; do not install the instrumentation APK.
3. Transfer `app-debug.apk` to your Android phone (Android 8.0/API 26 or newer). Open it and allow installation from that specific file-manager/browser source when Android asks. This is an alpha development build, not a Play Store release.
4. Connect to the server using either a trusted HTTPS origin (`https://media.example.com`) or the private USB test route below. `localhost` on the phone means the phone, not your computer. Enter an origin only, without `/api`, credentials, query or fragment.
5. Tap **Connect**. If setup is incomplete or API version 1/prefix search is unavailable, the app explains what to update. Sign in with your existing server username/password. **Remember this session** stores encrypted session cookies; the password is never saved.
6. Browse **Your library**, filter by library, or search by **Title starts with**. Tap a title, inspect the real metadata, and select **Play / resume**. Controls include play/pause, seeking, volume and fullscreen. Compatible Direct Play media is required; this client does not add server transcoding.
7. Pause or close the player, then check Continue Watching on another client. The server owns progress. On a conflict or network failure, saving pauses: reopen the video to fetch the authoritative position instead of replaying stale offline updates.
8. Use **Sign out** to revoke the server session and clear local credentials. If the server is unreachable, credentials still clear locally and the app tells you revocation was not confirmed. Revoke that device in the web account/settings interface when reachable.

### Private USB testing with a Windows or Linux server

This tests your real phone without opening the server to your LAN or the internet.

1. Install Android SDK Platform Tools on the computer running the server. Enable developer options and USB debugging on your phone. Connect a trusted USB cable and approve that computer's debugging prompt.
2. Keep the server listening on its default local address/port, `http://127.0.0.1:5050`. Confirm browser playback works on the computer.
3. In a terminal run `adb devices`. The phone must say `device`, not `unauthorized`.
4. Run `adb reverse tcp:5050 tcp:5050`.
5. On the phone enter **http://127.0.0.1:5050**, tap Connect and sign in. Repeat the reverse command after reconnecting USB. For an emulator, the same reverse command works; debug builds also allow its `10.0.2.2` host alias.
6. When finished, run `adb reverse --remove tcp:5050` and disable USB debugging if you no longer need it.

Remote plain HTTP is blocked by both URL validation and Android network security configuration. Do not disable certificate verification or expose the default server port through router forwarding. For remote use, deploy an authenticated HTTPS reverse proxy with a certificate trusted by Android; see the server's remote-access documentation. Private networking alone does not replace HTTPS in this alpha.

## Development builds

Requirements: JDK 17, Android SDK command-line tools or Android Studio, platform `android-36`, build-tools `35.0.0`, and platform-tools. AGP 8.13.2, Kotlin 2.2.21 and the checksum-pinned Gradle 8.13 wrapper are checked in. The app compiles/targets API 36 and supports API 26+.

```sh
python3 scripts/generate-android-models.py --check
cd clients/android
# On Windows use gradlew.bat instead of ./gradlew
./gradlew :app:assembleDebug :app:assembleDebugAndroidTest :core:testDebugUnitTest :app:lint
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

Set `ANDROID_HOME` to your SDK, or set `sdk.dir` in an untracked `clients/android/local.properties`. Never commit local paths, keystores or passwords. Import `clients/android` as the Android Studio project; the server/web build remains independent.

```sh
./gradlew -PandroidBuildNumber=2 :app:assembleRelease :app:bundleRelease
```

`androidBuildNumber` is an independent Android version code, 1–65535; the displayed version is `0.1.0-alpha.N`. Release outputs are `app-release-unsigned.apk` and `app-release.aab`. **The unsigned APK cannot be installed.** An AAB is a distribution bundle, not a directly installable APK.

### Release signing

No production key or signing password is stored in this repository. Generate and back up an offline release key, keep its passwords outside git, align the release APK with `zipalign`, then sign/verify it with SDK `apksigner`. Sign AABs with `jarsigner` or a protected CI signing job/Play App Signing. A consistent key is required for upgrades. Review the Android signing documentation before distributing production artifacts.

Debug APKs use an ephemeral development key on hosted runners. A later run may use a different key: Android will reject an in-place upgrade. Uninstall the old debug app before installing the new one (this removes remembered sessions and local settings). Playback progress remains on the server. Debug builds are for testing only; do not treat their signing key as a production trust identity.

## Behavior and boundaries

| Capability | Status |
| --- | --- |
| Manual server connection, compatibility/setup checks | Implemented |
| Cookie/CSRF login, logout, encrypted remembered session | Implemented |
| Bounded prefix search, library filters, pagination | Implemented |
| Real details/track metadata and Continue Watching | Implemented |
| Authenticated Media3 Direct Play, seek/fullscreen/volume | Implemented; decoder support varies by device |
| Resume, revision-checked progress, conflict handling | Implemented; stale offline updates are not replayed |
| Expiry/revocation clears private state and stops playback | Implemented when next authenticated request detects expiry |
| Rotation/recreation within process | ViewModel retains player; no password saved state |
| Audio/subtitle track switching | Not implemented; server selection flags are currently false |
| PiP/background audio, Cast, automatic discovery | Planned |
| Offline downloads, server HLS/transcoding | Not implemented |
| Android TV, Play Store distribution | Separate follow-up work |

Playback pauses when the activity backgrounds. A foreground MediaSession supplies platform media control integration; there is no background playback service. Android's available decoders are reported as capabilities, without claiming hardware acceleration or universal HEVC/HDR compatibility. No demo media/posters or cloud services are injected into the real library.

## Tests and troubleshooting

`core:testDebugUnitTest` checks URL restrictions, origin-bound cookies, logout race protection, corrupt storage, compatibility, redirects, unauthorized responses, response-size limits and disconnected servers. DTO generation derives shared types from `shared/api/openapi-v1.json`; the current CSRF endpoint lacks a response schema, so its tiny token transport model is explicitly handwritten.

The Android workflow builds debug/test/release APKs and AAB, runs lint/JVM tests, installs the debug APK on an API 35 emulator, and runs `scripts/test-android-backend.py` against a fresh actual ASP.NET backend. The script generates a synthetic VP9/Opus video, scans it, seeds progress and exercises rendered playback, resume, activity recreation, Keystore encryption, competing progress updates and session revocation. Run it locally after building the backend and Android test APKs with an emulator/device attached. The fixture owns localhost port 5050; stop your normal server first. Fixture credentials exist only in the temporary test database.

A successful build proves compilation; an emulator first-frame test proves playback for that fixture. Neither proves all codecs, minimum Android versions, physical phones, remote HTTPS configurations or manufacturer-specific behavior. See the PR/check results for observed validation; this document does not imply all device tests have passed.

- **Cannot connect:** check origin/port and server health. On USB, repeat `adb reverse`; on remote connections verify DNS and the trusted certificate chain.
- **No media:** configure/scan in web administration, check library permissions, clear the title prefix or filter.
- **Playback error:** test a short browser-compatible MP4 or WebM; review server diagnostics. This alpha has no transcoding fallback.
- **Progress conflict:** close/reopen the video to reload the server position. It deliberately does not overwrite another device's revision.
- **Saved session fails:** reconnect/sign in; Keystore state is device-bound and excluded from backups.
- **Install/update rejected:** use the debug APK, not the unsigned release or test APK; uninstall an older debug build signed by a different key.
