# Media Server Windows Client

A Tauri 2 / React / TypeScript desktop client with a native Rust cookie/session and streaming boundary. Connects to any compatible Media Server regardless of its OS; does not bundle or start the backend.

See [Windows setup, security and playback limitations](../../docs/clients/WINDOWS.md), [API contract](../../docs/clients/API.md), and [validation status](../../docs/clients/VALIDATION.md).

Build from this directory on Windows: `npm run tauri -- build --bundles nsis,msi`. Install both frontend and Windows npm dependencies first as documented. The installers contain the native application and local UI. Signing and automatic updates are not enabled; no stable release is published.
