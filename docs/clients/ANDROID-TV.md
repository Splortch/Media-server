# Android TV architecture

Create a TV-specific application in this monorepo after the phone networking/security/playback libraries are functional. Share Kotlin API models, cookie/CSRF transport, Keystore-backed sessions, progress synchronization and Media3 playback. Keep the TV Compose UI separate from phone navigation and avoid duplicating authentication logic.

The first TV PR must provide a ten-foot home/library/Continue Watching UI, deterministic D-pad focus restoration, remote back/menu behavior, Media3 overlay controls and readable subtitle/audio menus only when server capabilities permit selection. Manual server connection comes first; future mDNS discovery offers hints without bypassing TLS verification. A later pairing-code design must use expiring single-use server challenges instead of requiring long passwords on a remote.

Acceptance requires real backend integration, emulator install/launch and testing on a physical TV/TV box. Cover focus after refresh/playback/back, long lists, subtitles, suspend/resume, slow networks and session revocation. Do not claim TV codec/HDR/remote compatibility from phone builds. Google Cast and Smart TV platforms outside Android require separate receiver/client designs.
