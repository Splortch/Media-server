# Security policy

The initial alpha has no stable support guarantee. Security fixes target `main`; update before reporting duplicates.

Report suspected vulnerabilities privately using [GitHub private vulnerability reporting](https://github.com/Splortch/Media-server/security/advisories/new) **if enabled**. Repository administrators must enable that capability. If unavailable, use a maintainer's published private contact channel; a dedicated security mailbox is not configured yet. Never publish credentials, personal filenames or exploitation details in a normal issue.

Include affected commit/version, operating system, reproduction steps, impact, and redacted logs. Maintainers aim to acknowledge within seven days; two volunteer maintainers cannot guarantee a response SLA.

Default deployment is loopback-only. Keep media read-only, restrict access to the data directory, patch FFprobe and .NET, and keep setup tokens, database and key ring private. Do not expose development servers publicly. Read the [threat model](docs/architecture/SECURITY.md) before considering remote access.
