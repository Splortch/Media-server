# Future iOS client

Use SwiftUI and AVPlayer in `clients/ios` when implementation starts. No placeholder Xcode project is included. Generate Swift request/response models from the same v1 OpenAPI contract where practical and keep a small URLSession transport boundary. API major compatibility must be checked before login; backend OS is irrelevant.

Store only revocable session credentials in Keychain, never passwords or unprotected UserDefaults. URLSession cookie handling, CSRF writes, session expiry and logout must match other clients. Investigate the supported AVURLAsset cookie options and verify authenticated requests on actual devices, including every HLS segment before claiming adaptive playback. Do not use undocumented AVPlayer header hacks, public token URLs or disabled TLS checks.

AVPlayer container/codec/HDR support is Apple-device dependent; WebM, HEVC, AV1 and alternate audio/subtitles need a tested compatibility matrix. Server HLS/remux/transcoding (#4) may be a prerequisite for broad coverage. Preserve progress revisions and stop stale/offline writes rather than overwriting another device.

Development/builds require macOS and a compatible Xcode/Apple SDK toolchain. Simulator builds do not establish physical codec compatibility. Device provisioning and distribution require Apple signing identities/profiles; TestFlight/App Store distribution requires the appropriate Apple developer account and review. Keep certificates, provisioning secrets and private keys out of the repository. Future Bonjour discovery requires declared services and local-network privacy permission; do not enable blanket App Transport Security exceptions.

First PR scope: real connection/login, library/details and one tested AVPlayer Direct Play path with resume. Add background audio, Picture-in-Picture, AirPlay, downloads and App Store packaging only with explicit lifecycle/security tests and corresponding server support.
