# Optional download center

The source transfer manager is administrator-only; household members can submit requests through the existing request board. See [family approvals and managed imports](DOWNLOAD-IMPORTS.md). It is **disabled by default**. The media server and playback work without it. This is a built-in module; disabling it stops network transfers, but does not uninstall code or erase staged files.

| Capability | Status |
| --- | --- |
| Authorized public HTTPS URLs, real persistent queue | Implemented |
| Pause, validated range resume, cancel, priority | Implemented |
| Available/reserved space and staging limits | Implemented |
| Separate completed-file staging and administrator retrieval | Implemented |
| Explicit matching, private pending catalog and safe managed imports | Implemented |
| Family request approvals and Movies/TV library organization | Implemented: see [guide](DOWNLOAD-IMPORTS.md) |
| Rights-aware public-domain catalog integration | Planned: #110 |
| Compatible external download-client integration | Planned: #111 |
| Descriptions, posters, seasons and episode matching | Planned: #10 |

The parent roadmap is [#107](https://github.com/Splortch/Media-server/issues/107). Do not confuse a completed staged transfer with an imported, identified or playable library item.

## Step by step

1. Update the server normally and sign in as an administrator. SQLite migrations run at startup.
2. Open **Settings → Download center**. Check the available space. Staging uses the server **data-directory filesystem**, which may differ from your media-library disks.
3. Enter exact public source hostnames, one per line (no URL, port or wildcard). Include every host used by authorized redirects. Only approve sources you trust and are authorized to download from.
4. Check **Enable download center**, then **Save download settings**. Saving any policy change pauses existing active/queued jobs, so an old host policy cannot keep running in pooled connections.
5. Open **Downloads** in the sidebar. Give the transfer a name, paste its HTTPS URL, choose a maximum size in MiB, and optionally supply the publisher's SHA-256. Query tokens are hidden in the form, encrypted at rest, and excluded from queue responses.
6. Add the download. Higher priorities run first after the current transfer; changing priority does not interrupt a running transfer. Real bytes and state are polled from the server.
7. **Pause** retains partial data. **Resume** requires a strong ETag plus matching final source identity and correct Content-Range. A source without a validator requires **Discard partial & restart**. Changed sources and servers ignoring Range fail without appending their new response.
8. When complete, **Save NAME** retrieves the staged file as an authenticated attachment. It has a generated `.bin` filename: verify the expected checksum/type and place it in your own library manually. Files without an approved import plan are not automatically probed or imported. Approved matches follow the [managed import pipeline](DOWNLOAD-IMPORTS.md).
9. **Cancel & discard** stops a transfer and deletes its partial. **Remove NAME & staged file** explicitly deletes only that job's generated staging files and queue record. Existing libraries are never modified.
10. To stop the module, clear **Enable download center** and save. No jobs resume implicitly when re-enabled; use explicit Resume. Completed staged-file retrieval and safe cleanup remain administrator-authorized even while disabled. Queue browsing requires the feature enabled.

After an interrupted server restart, previously running/finalizing jobs become paused with `server-restarted`. Already-renamed completed files are verified against their stored SHA-256 before final recovery. Back up the database **and** existing ASP.NET Data Protection key storage: losing keys makes retained source URLs unreadable. Restarting a transfer needs its original protected URL; completed/cancelled jobs erase that field. Database backups/WAL may still contain older encrypted records.

## Limits and error handling

One transfer at a time, at most 100 records, a 100 GiB combined staging cap, and a 512 MiB free-space safety margin. Pending/paused jobs reserve their remaining expected bytes (or maximum budget when length is unknown). Planned import copies additionally reserve destination bytes, conservatively across the participating volumes. Failed/completed partials still count as occupied bytes. Admission, periodic writes and completion check space; unrelated writers can still fill a disk between checks. Disk-full errors stop the job and preserve safe cleanup/retry states. Limits are deliberately fixed in this alpha.

The worker enforces a six-hour attempt limit, ten-second connection limit, thirty-second header/read-stall limits, a 16 KiB response-header bound and at most three redirects. Unknown-length bodies are bounded by the supplied budget. Failure codes contain no URL, token, remote response text or exception message. Safe restart retries from zero; automatic retries do not hide changed content or silently consume storage.

## Security and architecture

All routes exist under `/api/downloads` and `/api/v1/downloads`, require the existing administrator authorization policy, and retain existing cookie/session and CSRF protections. The versioned OpenAPI and generated TypeScript describe queue/settings/control models. Web UI uses the actual backend; native Android/desktop downloader screens are not implemented.

Production transport uses `SocketsHttpHandler.ConnectCallback` to resolve and validate all DNS answers, rejects local/private/link-local/reserved addresses and pins the connection to a verified public IP. Original hostname/TLS verification remains enabled. Proxy, cookies, automatic decompression and implicit redirects are disabled. Every redirect is independently revalidated against the exact host allowlist. Private-network external clients need a separate future trust model, not an exemption here.

Source URLs are protected with an isolated ASP.NET Data Protection purpose. Persisted final-URI hashes, ETags and encrypted source URLs are internal and absent from queue DTOs. Controlled tests replace only the remote transport; production never accepts a test certificate or private source override.

Staging filenames are generated UUIDs under `data/downloads`; no request supplies a destination path. Handle-based writes reject symbolic links/reparse points, non-regular files and multiple hard links before writing. Exclusive creation never overwrites an existing file. Completed moves never overwrite destinations. The data directory must remain private to the server account; this is not a sandbox against a malicious process with that same account or an administrator replacing trusted parents. Media-library roots must not include the private server data directory.

Resume follows [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html): strong If-Range validator, exact byte offset and total, unchanged final URI, unchanged ETag, and 206 response. A 200 response is never appended. TLS uses the platform trust store; never disable certificate validation to make a source work.

## Build note for macOS

macOS server builds require Xcode Command Line Tools (`xcode-select --install`). The Media project compiles a small fixed-signature C bridge to Darwin's variadic `openat` API and copies `libMediaNative.dylib` into build/test/publish output. This avoids the different Apple Silicon variadic argument ABI. Build macOS artifacts on a macOS host; existing release jobs already do so. Linux and Windows do not use this library. CI exercises actual creation/reopening, 0600 permissions and hard-link refusal on Intel and Apple Silicon.

A staged SHA-256 is always calculated; it verifies publisher content only when you provide a trusted expected checksum.

## Validation

Backend integration tests run the real API, database, worker, protected URL persistence and file staging against a controlled HTTP source. They cover authentication, CSRF, default-off behavior, pause/resume/cancel, source changes/ignored ranges, priority, restart recovery, unknown length, budget/space failures, disconnection, range retrieval and cleanup. Filesystem tests exercise secure writes and hard-link refusal on CI platforms; macOS additionally checks the SDK ABI. Browser CI downloads this repository's Apache license over real public HTTPS, verifies its digest/content, checks the queue UI with axe, and removes the staged file. Build success is not physical-device validation.
