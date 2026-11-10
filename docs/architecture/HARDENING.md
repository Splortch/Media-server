# Alpha hardening and operator recovery

Issue #1 covers several trust boundaries. Automated regressions exercise concurrent setup through
legacy/v1 aliases, expired-cookie reads/ranges/writes, CSRF on credential changes, untrusted Host
headers, per-user progress isolation, password-change/recovery revocation, and pinned media handles.

## Setup and credentials

Setup uses a random local token, created with mode 0600 on Unix inside the private data directory.
It is deleted after administrator creation and cleaned up on startup for an already configured
server. Restarting does not reopen setup or generate a replacement token for a configured server.
Concurrent credential operations share a server-wide gate; SQLite's fixed administrator ID also
prevents two setup winners. An exclusive .server.lock lease enforces one server process per data directory, including local recovery.
The lock file may remain after exit; its open handle, rather than file existence, indicates ownership.
Do not delete the lock file while a server owns it or place data on a filesystem that ignores file locks.

Settings → Change password verifies the current password, writes a new Argon2id hash and revokes
all that user's sessions in one SQLite transaction. The current browser signs out too. Copied
cookies are rejected on their next request. Login and credential changes share the gate so an
in-flight login cannot commit a pre-change credential session after revocation.

If you forget the administrator password:

1. Stop the server and back up its complete data directory. Keep its database and key ring private.
2. Set `MediaServer__DataDirectory` to that exact existing directory.
3. Run `./Api --reset-admin-password` on Linux, `Api.exe --reset-admin-password` on Windows,
   or `dotnet Api.dll --reset-admin-password` for framework-dependent builds. For Compose,
   stop the service first, then use `docker compose run --rm --no-deps media-server --reset-admin-password`.
   The image entrypoint already invokes `dotnet Api.dll` and the one-off container retains the data volume.
4. Enter a new 12–256 character password at the local prompt. Interactive input is hidden. Never
   put the password in a command-line argument, source file or shell history. Redirected input is
   supported for operator tooling but that tool is responsible for keeping the input private.
5. Restart normally and sign in. All administrator sessions have been revoked; libraries, progress
   and media remain intact. No HTTP password-recovery endpoint is provided.

## Streaming handles

PathPolicy still rejects traversal, outside-root paths, symbolic links and junctions. Streaming now
uses a validated open handle instead of reopening a checked pathname. Linux walks every component
with openat/O_NOFOLLOW and validates the inode is a regular file with statx; nonblocking open avoids
hanging on FIFOs. Windows opens with FILE_FLAG_OPEN_REPARSE_POINT, refuses non-disk/reparse/directory
handles, compares the final handle path and denies write/delete sharing while the stream is open.
Unsupported operating systems fail closed. Linux requires a modern kernel/glibc with statx.

Linux library scans now keep one validated no-follow handle open for fingerprinting, probing,
size and modification time. FFprobe opens `/proc/<server-pid>/fd/<descriptor>` for both metadata
and HDR frame inspection. Replacing a filename or ancestor with another file/symlink cannot
redirect those reads. The input remains seekable without copying whole movies into temporary
storage. The descriptor is retained until every probe subprocess has exited, including on
cancellation. A changed size/modification time rejects the scan result rather than persisting
an inconsistent fingerprint. Alternative Linux scan probes must explicitly implement pinned
input; there is no silent pathname fallback.

This requires procfs access to the server's descriptor from its same-user FFprobe child. If
container/LSM/procfs permissions deny access, the probe fails; do not relax isolation or add
ptrace privileges to make it pass. Windows and macOS scans still use pathname-based FFprobe
input while holding the validated source open; eliminating their remaining pathname boundary
requires a separate platform-specific implementation and tests.

These checks do not sandbox FFprobe, freeze file contents against in-place writes, or make
hostile shared filesystems safe. Size/timestamp checks are best-effort mutation detection, not
an immutable snapshot. Media roots and their ancestors must remain controlled by the operator;
uploads/untrusted writable roots remain unsupported. Kernel process isolation, the remaining
platform scan boundaries and both-maintainer review stay open under #1; no internet-facing
readiness or independent security audit is claimed.

## Request policy review

- Host filtering defaults to localhost, 127.0.0.1 and ::1; configure exact hosts explicitly.
- Kestrel limits request bodies to 64 KiB; no upload API exists.
- Cookie and antiforgery defaults remain HttpOnly/Strict, with HTTPS secure-cookie guidance unchanged.
- Login, setup and password changes share the existing per-IP 10/minute limiter. Forwarded headers
  are not trusted implicitly. Use a private network/VPN; do not put this alpha on an open listener.
- Probe arguments are passed separately, network protocols are disabled, output is bounded and the
  process tree is killed on timeout. This is a process lifetime limit, not an OS sandbox.
- Source FFprobe invocations (library scan, download import, staged-source validation and HDR frame
  inspection) restrict demuxers to MOV/MP4, Matroska/WebM and AVI. A supported filename extension
  never authorizes HLS, concat or other indirect inputs to read additional local files. Real-media
  regressions cover all six discovered extensions and disguised HLS/concat inputs. This reduces
  parser exposure; it does not resolve the remaining platform boundaries above or sandbox codec parsing.
  A separate generated-HLS probe permits only HLS/MPEG-TS for server-authored manifests in the
  private conversion cache. Source files never use that policy; real HLS session tests cover it.

The two maintainers should review these policies and Windows filesystem behavior before enabling
remote deployment. Physical Windows, SMB/UNC, case-sensitive Windows directory and screen-reader
coverage are not implied by a passing Linux test run.
