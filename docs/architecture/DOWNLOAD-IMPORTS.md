# Family approvals and managed library imports

This extends the optional [download manager](DOWNLOADS.md). Downloads and movie requests remain off by default. The server still works without either module. No online catalog, metadata provider or external download client is enabled here.

## Family request → approval → playback

1. Configure a **writable destination library** inside the operator's allowed media roots. Choose its Movies or TV classification in library settings. Existing media can remain read-only; use a separate managed library when appropriate. The destination must not overlap the private server data directory.
2. In **Settings → Download center**, approve exact public HTTPS hosts and enable downloads. In **Settings → Customize → Server-wide availability**, enable movie requests.
3. Grant the household member access to the intended library and an appropriate maximum content rating. The member opens **Movie requests**, chooses that library, and submits a title. This alone never fetches anything.
4. An administrator opens the request's **Approve authorized download** panel. Confirm the authorized HTTPS URL, size budget, requested destination library, Movies/TV classification, actual container, content rating and title/year. A publisher SHA-256 is optional but recommended.
5. For TV, explicitly supply **series, season and episode** together. Leave all three blank if unknown: the transfer completes into an administrator inbox, with no guessed episode or visible library item. Use **Downloads → Match and organize** when the correct match is known.
6. Approve. The existing request becomes Considering, and a persistent transfer is queued. Duplicate or stale approvals are rejected. To decline, use **Reason for declining** and **Decline download request**; the reason is shown to the requester.
7. After a verified transfer, the server copies the staged file into its managed folder, verifies SHA-256 and media/container validity, and runs the normal library scanner. Import probing restricts FFprobe to supported container demuxers and local-file protocol, rejecting referencing playlists before the normal scanner runs. Existing manual-library probing is unchanged. The new catalog row stays hidden from **every** account until ingestion succeeds. It then becomes available and the request is marked Added, with its normal title/playback link.
8. Members see only their own download status, subject to current library/content access. The existing shared request board retains its library-scoped behavior; source URLs, paths, checksums and private transfer details remain administrator-only.

The destination is deliberately the library selected by the request, so approval cannot silently redirect a family request into another library. Correct an incorrectly selected library by withdrawing/recreating the request. Restricted users cannot receive unrated or above-limit approvals. Account disabling, removed grants, changed ratings, withdrawn/declined requests, changed review revisions and changed/deleted target libraries stop further authorized processing. Delivery rechecks permissions inside the final database transaction.

## Import an administrator's existing staged download

Open **Downloads**, expand **Match and organize NAME**, select a library, and confirm the classification, title, container and content rating. TV uses explicit episode matching as above. The original download remains staged until you explicitly remove it; importing does not delete its source.

Generated folders are confined to the selected allowed library:

- `MediaServer Imports/Movies/TITLE (YEAR)/JOB_ID/TITLE.ext`
- `MediaServer Imports/TV Shows/SERIES/Season NN/JOB_ID/SERIES - SnnEnn.ext`

Names are sanitized, job directories are unique, and pre-existing destination directories are refused. No request supplies a filesystem path. Each new job folder contains an ownership marker; cleanup requires its exact recorded proof. Files are created exclusively through the secure handle writer. Neither import nor cleanup recursively deletes library folders or overwrites/reorganizes pre-existing media.

## Hold, retry and discard

- **Pause/Resume** controls the source transfer as before.
- **Hold library import** stops pending copy/scan processing. Wait until the worker stops before cleanup.
- **Retry library import** retries a matched held import, only if permissions and policy still allow it. Hash-matching owned final files can be recovered without another download or overwrite.
- **Discard import files, keep staged NAME** removes only the claimed temporary/final import files and hidden pending catalog row. It retains the verified source for rematching. Unrelated files are preserved. Active library scans block cleanup until complete.
- **Remove NAME & staged file** cannot remove a source needed by an active/held planned import. Discard that import first. Once successfully imported, removing its staged download never deletes the library item or imported file.

An interrupted server restart holds copy/scan work with `server-restarted`. Hidden catalog rows stay hidden even if an ordinary library scan runs. Review and Retry, or Discard and rematch. Losing an ownership marker, changing a claimed file's contents, or a destination collision requires administrator review; the server does not adopt or delete an unproven file. A changed request review revision requires a fresh approval rather than silently resuming stale consent.

## Storage and cross-volume safety

A verified staged source is kept while a **new copy** is made on the destination filesystem. Only after hashing and probing is that destination-local temporary file atomically renamed without replacement. This works when staging and libraries are on different volumes and keeps a recoverable source after disk-full or copy failures.

Outstanding transfer bytes **and** remaining import-copy bytes are reserved. The initial policy conservatively checks the combined reservation pool against both staging and destination free space; it can reserve more space than necessary on independent volumes. Reservations are displayed under Download center settings. Admission, periodic copying and completion check the 512 MiB margin. External writers can still consume space between checks. Failed/held jobs retain their source and any owned partial for explicit recovery/cleanup. This is not a power-loss certification or a sandbox against a process with the same filesystem privileges.

Common states/errors:

| State/error | Action |
| --- | --- |
| `inbox` | Supply an explicit TV episode match; nothing is delivered yet |
| `import-container-mismatch` | Discard the held import, confirm the actual container and rematch |
| `destination-storage-insufficient` | Free space on the library volume, then Retry |
| `request-access-revoked` | Review grants, rating, request revision and library configuration; do not bypass permissions |
| `import-folder-collision` / `import-folder-needs-review` | Inspect the claimed managed folder; unrelated files are never automatically removed |
| `import-scan-failed` | Inspect library scan diagnostics and validate the source; it remains staged |

## Docker writable destination example

Keep existing `/media` mounts read-only. Add a separate import location explicitly, for example in a local Compose override:

```yaml
services:
  media-server:
    environment:
      MediaServer__AllowedRoots__1: /imports
    volumes:
      - ./downloaded-media:/imports
```

Use the actual service name from your Compose file. Create `downloaded-media/movies` and `downloaded-media/tv` on the host, make them writable by the container's configured server account, and add `/imports/movies` and `/imports/tv` as libraries. Avoid broad permissions such as granting everyone write access. No host signing key or API token belongs in this override.

## API and validation

Both legacy and v1 API aliases support administrator request approval/decline, staged-file match plans, and hold/retry/discard. `/requests/download-status` is authenticated and returns owner-scoped status, never credentials or paths. OpenAPI and generated TypeScript include these contracts. Existing authentication, CSRF, source SSRF/TLS rules, media ACLs and range streaming remain in force.

Integration tests use synthetic VP9 media and the real FFprobe/scanner/playback pipeline, including private progress, non-admin/stale approvals, rating and destination budgets, TV inbox matching, grant revocation, collision safety, scanner-coordinated cleanup and restart recovery after final rename. Browser coverage downloads the project's small Apache-licensed synthetic fixture over production HTTPS, approves/imports it and checks the real stream. Filesystem budget failures are controlled simulations; physical multi-disk/power-loss testing and physical screen-reader/device validation are not claimed.

Catalogs (#110), external download clients (#111), online descriptions/posters/season matching (#10) and richer TV catalog features remain separate work. Native desktop/Android download-management screens are not added by this web/server increment.
