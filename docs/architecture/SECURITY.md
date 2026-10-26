# Threat model and remote access

## Protected assets and trust boundaries

Personal media, account credentials, watch history, configured paths, setup token and cookie-protection keys are private. Browser requests and media contents are untrusted. The server operator and OS account controlling the media filesystem are trusted.

Implemented: Argon2id (64 MiB, three iterations, parallelism one, 16-byte random salt, 32-byte output), constant-time hash comparison, HttpOnly SameSite Strict cookies, database session revocation, admin role checks, antiforgery tokens on every API write, login/setup rate limits, allowed-host filtering, no wildcard CORS, private media no-store, security headers and restricted media roots. Setup requires a random local-file token and a unique administrator primary key prevents two setup winners.

No arbitrary file URL is accepted. Stream IDs resolve to database paths and are checked beneath a configured allowed root. Absolute media-relative paths, traversal, sibling-prefix tricks, symlinks and junctions are rejected. Scan and streaming handles request read-only access. FFprobe arguments use `ArgumentList`, no shell, a file-only protocol whitelist, a timeout and bounded output. Run FFprobe with least privilege and patch it regularly; this alpha does not sandbox codecs at the OS level.

## Remaining risks and limitations

- Path checks followed by file opening are not a defense against a malicious local user swapping directories concurrently. Keep media directories controlled by trusted users and mounted read-only in the server/container. Strong no-follow descriptor-based traversal is future hardening.
- Loopback HTTP cookies are not forced Secure. HTTPS deployment must explicitly set secure cookies. The local data key ring is protected by OS permissions, not an application-managed external key vault. Windows operators must set restrictive ACLs themselves.
- A database copy contains salted password hashes and watch history. A key-ring copy plus cookies compromises sessions. Encrypt backups and protect the whole data directory.
- No MFA, password reset UI, account management, device/session-management UI or remote-access support is provided. Session rows are revoked by logout and expire after seven days.
- Login is throttled by source IP; no distributed protection is claimed. Don't disable the limits to resolve a proxy configuration issue.
- Scan errors intentionally avoid printing private filenames or raw FFprobe output. Request traces identify unexpected failures without echoing credentials. Avoid debug EF sensitive-data logging.
- Release artifacts and images need independent operational/security review. No stable support claim is made.

## Future remote-access path

Prefer a private authenticated VPN to a public port. Keep the server bound to loopback behind a correctly configured HTTPS reverse proxy with a dedicated hostname. Use an explicit allowed-host list, Secure cookies, trusted proxy addresses and a narrow forwarded-header configuration. Do not accept forwarded headers from arbitrary clients. These proxy settings are not automatically enabled in this MVP; test login, antiforgery, WebSocket upgrade, range responses, proxy timeouts and limits before deployment. Never forward ports to Vite.

## Dependencies

CI audits direct/transitive NuGet packages and npm packages and fails on reported vulnerabilities. Dependabot covers NuGet, npm, Actions and Docker. Audits are point-in-time checks rather than a security guarantee. Initial dependency upgrades resolved the NuGet advisories encountered during restore; see the validation record for actual commands and results.
