# Backups, native updates and rollback

These are optional operator tools. Normal extracted-server launchers and Docker Compose remain available.
Stop the server before maintenance. A data-directory lease rejects a second server or a maintenance command
against live data. Do not delete .server.lock while its owner is running; the file can remain after exit.

## Create and inspect a backup

Use the executable from an extracted build that advertises `maintenance-v1` in server-info. Set
`MediaServer__DataDirectory` to your existing data directory before every command. On Linux:

```bash
export MediaServer__DataDirectory=/absolute/private/server-data
./Api --backup /absolute/private/backups/server-before-update.zip
./Api --restore-check /absolute/private/backups/server-before-update.zip
```

On Windows, set `$env:MediaServer__DataDirectory='C:\Private\ServerData'` in PowerShell and invoke
`.\Api.exe` with the same flags. Framework-dependent development builds can use `dotnet Api.dll`.
Commands return nonzero on failure and never accept passwords or tokens as arguments.

The archive contains a consistent SQLite backup, migration IDs, key-ring XML, launcher preferences when
present, and a reference-only snapshot of allowed roots/FFprobe/host/cookie/listener configuration. Original
media is never included, changed or deleted. Each entry has a SHA-256 and length in backup.json. Restore
validates names, archive expansion limits, checksums, SQLite integrity and migration compatibility before
writing application data. The default expanded-data limit is 4 GiB; change `MediaServer__BackupMaxBytes`
if a larger operator-controlled database is expected. The tool is for trusted backups, not arbitrary uploads.

Backups are **not encrypted**. They contain password hashes, watch history and cookie-protection keys.
Unix output is created with mode 0600; temporary working directories are private. Windows uses inherited
ACLs: keep data/backups in an account-private directory and review permissions. Encrypt off-machine backups
with your backup system. Environment secrets, certificates outside data, and external reverse-proxy settings
must be backed up separately. Never commit backups or key rings.

## Restore into a new directory

Preserve the current directory. Choose an empty destination and set MediaServer__DataDirectory to it, then:

```bash
./Api --restore /absolute/private/backups/server-before-update.zip
```

A populated destination is refused. Restoring clears all sessions so previously revoked cookies cannot be resurrected. Accounts, passwords and permissions revert to the checkpoint; review any later security changes before granting remote access. `--restore-check` does not replace the database. A backup from newer or
incompatible migrations is refused; use its matching/newer server version. Listener/proxy configuration is
not silently applied from the archive. Review the reference snapshot and configure the restored server
explicitly. A disposable title-search index is regenerated on startup.

An interrupted restore leaves `.restore-in-progress`; startup fails closed. Preserve that incomplete folder
and restore again into another empty directory. Existing source data and media stay available for recovery.

Docker operators can stop the service and run a one-off container with the same data volume and an
additional writable `/backups` bind mount, passing `--backup /backups/name.zip` after the image name. Ensure
the container's app user can write the backup directory. Restore into a new named volume, not the original.
Do not remove the original volume until the restored deployment is independently verified.

## Managed native updates (Python 3.11+)

The native archives include update-server.py. This optional offline manager is separate from OS installers;
it does not register a service, download executables automatically or change firewall settings. Stop a managed
server before using another manager command. Keep the installation root private and outside the data directory.
FFmpeg and FFprobe must be installed for the synthetic playback gate. Windows can use the same pinned tools
obtained by the ordinary launcher; put their bin directory on PATH.

Download the native archive for your OS and its SHA256SUMS from the separate platform release. Verify the
checksum through your trusted release source; a checksum provides integrity, not a publisher signature.

```bash
python update-server.py --root /private/media-install stage ./MediaServer-vVERSION-linux-x64.tar.gz --sha256 HEX_FROM_SHA256SUMS --version VERSION
python update-server.py --root /private/media-install activate VERSION --data /private/server-data
python update-server.py --root /private/media-install run
```

Use a Windows ZIP on Windows. VERSION is the exact server version (for example 0.2.0-alpha.N, without v).
Keep your normal allowed-root, FFprobe, host and secure-cookie environment configuration when running it.
The manager tests the real extracted binary's version, startup and bundled UI; generates its own synthetic
video; proves FFprobe discovery, scanning, authenticated byte ranges and progress. No source-only zip is
accepted in place of a native executable. It requires the maintenance-v1 protocol, so releases predating
this change must first be backed up/adopted with a new build; they are not advertised as managed rollback targets.

Activation takes a backup before touching the version pointer, restores a separate copy with the candidate,
and requires successful startup/migration health against that copy. Production data fingerprints are checked
before/after preparation; unexpected changes abort activation. Version directories are never overwritten.
The current.json pointer is replaced atomically only after validation. Staging failure, checksum failure,
failed clone migration or interrupted pointer replacement leaves the previous pointer in place. Old binaries,
checkpoints and original media are retained. Power-loss durability still depends on the filesystem/storage.

After at least two managed versions, stop the server and run:

```bash
python update-server.py --root /private/media-install rollback
python update-server.py --root /private/media-install run
```

Rollback restores the checkpoint to a **new sibling data directory** and health-checks the previous binary
before switching the pointer. The failed/newer database is retained. Progress written after the checkpoint
is not merged automatically; preserve that directory for operator reconciliation. Do not launch old binaries
against a newer production schema. The first managed activation creates a data backup but has no earlier
managed binary to roll back to automatically. Docker updates continue to use pinned image versions and a
new-volume restore; this manager is for native server archives.

## Verification

Backend tests cover private, consistent snapshots, dry-run preservation, checksum tampering, traversal,
future-schema rejection and copy-only restore. Python tests simulate failed candidate migration and interrupted
pointer switching and verify rollback retains failed data. Release packaging additionally runs the actual
archive through stage/playback, checkpoint, migration-copy and restore checks on Windows/Linux. These hosted
checks are not a claim of physical Windows installation, power-loss testing or a signed automatic updater.
