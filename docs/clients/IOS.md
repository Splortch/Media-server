# iOS client preview

`clients/ios` contains a SwiftUI iPhone/iPad app (iOS 17+) with connection/login,
Keychain session restoration, paginated library browsing, title details and an
AVPlayer Direct Play path. It uses API v1 independently of the server OS. This is
source preview work for #34, not an installable or device-validated release.

## Build and automated checks

On macOS with Xcode and its command-line tools selected:

```bash
python3 scripts/generate-ios-models.py --check
cd clients/ios
swift test
brew install xcodegen
xcodegen generate
xcodebuild -project MediaServer.xcodeproj -scheme MediaServer \
  -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath build CODE_SIGNING_ALLOWED=NO build
open MediaServer.xcodeproj
```

The iOS workflow runs the contract drift check, transport/progress unit tests on
macOS and a simulator SDK app build. Its `.app` artifact is for simulators, not an
IPA or TestFlight build. It does not establish real-server playback or physical
codec support. The generated Xcode project and build output are ignored. Change
`project.yml`, then regenerate. Swift Codable models are generated from the same
OpenAPI snapshot as Android/TypeScript; do not edit `Models.swift` manually.

## Connection and security

Complete server setup in the web interface first. Enter an origin such as
`https://media.example:8443` without credentials, path prefix, query or fragment.
API major compatibility is checked before passwords are sent and before restored
sessions are used. HTTP is allowed only for the device's own loopback, useful with
a simulator-hosted server; a physical phone's localhost is the phone itself.
LAN servers need trusted HTTPS. No TLS bypass or blanket ATS exception is added.

URLSession uses isolated ephemeral cookies and refuses API redirects. Writes
fetch a fresh CSRF token. Keychain stores only the chosen origin and revocable
cookies using WhenUnlockedThisDeviceOnly protection; passwords are cleared from
the form after submission and never persisted. A 401 clears cookies and Keychain
and returns to login. Logout always clears local credentials and reports when
remote revocation could not be confirmed. API response bodies and cookies are
not included in errors or logs.

`NSLocalNetworkUsageDescription` explains the user-selected LAN connection. Allow
the local-network prompt when connecting; if denied, use iOS Settings to grant it
and retry. No Bonjour discovery/service declaration or LAN scanning is included.

## Playback, codecs and lifecycle

The initial explicit Play/Resume action fetches current server progress, builds an
AVURLAsset using Apple's public `AVURLAssetHTTPCookiesKey`, seeks to the server
position and starts AVPlayer. Opening title details does not fetch stream bytes.
A completed title starts from zero. Cookies are scoped to the stream URL; no
public bearer URL or undocumented header option is used. This uses native iOS
playback controls, separate from the custom web/desktop player.

Only H.264 in MP4/MOV/M4V with AAC/MP3 audio (or no audio) is admitted by this
preview; actual profiles/levels, device support and files still need validation.
WebM/MKV, HEVC, AV1, HDR, alternate tracks and subtitles are not claimed. HLS is
not implemented: future work must create/poll/release server conversion leases
and demonstrate authenticated playlist **and every segment** request before
claiming adaptive support. Apple documents additional HLS cookie scope caveats:
https://developer.apple.com/documentation/avfoundation/avurlassethttpcookieskey

Progress writes occur approximately every ten seconds and on close/inactivity,
using serialized expected revisions. Conflicts and uncertain failures halt that
writer and pause playback with a visible message; reopen the title to fetch fresh
state. There is no offline replay queue. Inactive/background transitions pause
and attempt a final save; iOS suspension can lose unsent seconds. Foregrounding
does not auto-resume. Close detaches the asset and removes observers. No background
audio entitlement, PiP, AirPlay feature, downloads or App Store release is claimed.

## External signing and distribution

The repository intentionally contains no Apple signing identities, team IDs or
provisioning material. In Xcode, select your own unique bundle identifier and Apple
Developer team under Signing & Capabilities, enable automatic signing, connect a
device and run. Trust the development identity and enable Developer Mode where
required. Do not commit the generated project, certificates, keys or profiles.
For TestFlight, use an authorized Apple Developer account and matching App Store
Connect record, then Product → Archive on a device destination and Distribute App
→ App Store Connect. Signing and uploading are external operator steps; CI only
builds the unsigned simulator app. No distribution upload is automatic.

## Required physical-device acceptance evidence (still outstanding)

Before closing #34 or advertising a tested iOS build, record device model, iOS and
Xcode versions, backend commit/version, sanitized test file codecs and results in
`VALIDATION.md` (never cookies or account passwords):

1. Sign in through trusted HTTPS to a real server. Deny/regrant local-network
   permission, reject untrusted TLS, and verify incompatible API/setup errors.
2. Open a title without streaming, then play an H.264/AAC MP4. Verify authenticated
   initial/range requests and successful seeks. Confirm anonymous ranges fail.
3. Close at a known position, reopen and verify server resume. Change progress on a
   second client; confirm the old iOS writer stops on 409 without overwriting it.
4. Background/foreground, close during a write, disconnect/reconnect, expire/revoke
   the session and log out offline. Confirm visible errors and no stale retries.
5. Restore a session after relaunch; sign out and verify no restored credentials.
6. Archive/sign externally, install on a physical device and document the result.
   TestFlight additionally needs distribution provisioning and an external upload.

Linux development cannot supply this evidence. Simulator compilation and mocked
transport tests must not be recorded as successful real-device streaming.
