# Build inputs, image digests and unsigned alpha artifacts

Issue #2 tracks distribution acceptance. Pinning image bases prevents an unchanged Dockerfile from silently adopting a different SDK or runtime, but does not make the entire build bit-for-bit reproducible.

## Container bases

`docker/Dockerfile` pins the Node, .NET SDK and ASP.NET runtime references to SHA-256 manifest digests. The initial pins were retrieved on 29 October 2026 directly from Docker Hub's official `library/node` repository and Microsoft's `mcr.microsoft.com` registry. The SHA-256 of each returned manifest body was checked against the registry's `Docker-Content-Digest` header before committing it. Tags remain beside the digest to make version review clear; the digest selects the actual immutable manifest.

CI runs `python3 scripts/check-container-bases.py` before the real Docker build. The policy permits only the three reviewed external image repositories, requires a non-latest tag and full SHA-256 digest, and allows references to earlier local stages and `scratch`. A real Docker build then verifies that those image references can be pulled and used. Six policy tests exercise missing/malformed digests, unreviewed sources, missing bases and latest tags.

Dependabot's Docker ecosystem updates can propose new tags/digests. Review them and run CI; do not remove digests to make an update pass. To inspect a candidate with Docker installed:

```sh
docker buildx imagetools inspect node:26-bookworm-slim
docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0.403-noble
docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:10.0.14-noble
```

Use the top-level manifest/index digest, not an arbitrary platform's child digest, unless intentionally restricting platform resolution. Review the publisher and tag, then update the corresponding FROM line. CI must build and smoke-test the resulting image. An immutable base can still contain vulnerabilities; review updates rather than freezing it indefinitely.

## Other inputs and reproducibility limits

- `global.json` selects the .NET SDK feature band and patch roll-forward policy. Container SDK pins are exact. Native runners may resolve an allowed later patch; record the workflow's actual SDK version when reproducing a native build.
- NuGet package locks are checked with locked restore; npm uses `package-lock.json` and `npm ci`; Rust uses committed Cargo locks. Android has its checked Gradle wrapper and declared tool versions.
- Runner images, Rust's stable channel, Homebrew packages and apt repositories can still change. FFmpeg and its dynamically installed system dependencies are not frozen by the base-image digest. The Docker build installs current apt packages; its license/source bundle records the actual distributed packages.
- Do not call these builds hermetic or bit-for-bit reproducible. The remaining SBOM/license inventory, clean-machine and signing criteria are still tracked in #2. Container corresponding source and checksums are useful evidence, not substitutes for a complete SBOM.

## Signing and download verification

Alpha server/client installers and archives are currently unsigned. Windows may show SmartScreen and macOS packages are not Developer ID notarized. No signing key is committed, and no CI build alone establishes publisher trust. Android release outputs without signing credentials remain unsigned; the debug APK uses a debug identity and is for testing.

Check the expected platform/category and version on the repository's release page. Verify downloaded bytes against that release's SHA256SUMS before installing:

```powershell
Get-FileHash .\MediaServer-vVERSION-win-x64-setup.exe -Algorithm SHA256
```

```sh
sha256sum --check SHA256SUMS
# macOS: shasum -a 256 -c SHA256SUMS
```

A checksum detects mismatched or corrupted bytes; it does not independently authenticate a publisher when both file and checksum come from the same account. Future signing should use protected CI identities/certificates and document certificate rotation, verification and recovery. Never put private signing keys in repository files or public artifacts. Stable-release readiness remains separate from alpha build success.
