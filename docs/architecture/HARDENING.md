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

## Streaming and scan handles

PathPolicy still rejects traversal, outside-root paths, symbolic links and junctions. Streaming now
uses a validated open handle instead of reopening a checked pathname. Linux walks every component
with openat/O_NOFOLLOW and validates the inode is a regular file with statx; nonblocking open avoids
hanging on FIFOs. macOS walks components with its own openat/O_NOFOLLOW flags and checks the
opened file with fstat. Windows opens with FILE_FLAG_OPEN_REPARSE_POINT, refuses
non-disk/reparse/directory handles, compares the final handle path and denies write/delete
sharing while the stream is open. Linux requires a modern kernel/glibc with statx.

Library scans retain one validated handle for fingerprinting, size, modification time and probe
input. Replacing an already opened source's pathname cannot redirect those reads. On Linux,
FFprobe opens `/proc/<server-pid>/fd/<descriptor>` for metadata and HDR frame inspection. Both
passes use a seekable input, and the descriptor stays open until every probe subprocess exits.
This requires procfs access from the same-user FFprobe child. If container or procfs policy
denies access, the scan fails.

Windows and macOS copy from the validated handle to a seekable file in the private server data
directory. Scans stage one file at a time. They need room for the largest source plus 64 MiB
of free space on that volume, and each scan reads the full source to make the copy. The server
deletes the copy after FFprobe exits, including after cancellation, and reclaims an interrupted
copy at startup. Keep the data directory private, including its inherited ACLs on Windows.
Alternative scan probes must explicitly support open-file input. The scanner rejects a result
if the source handle's size or modification time changes during the operation.

The operator must control the media roots, their ancestors and the data directory. Size and
timestamp checks detect some in-place changes. Codec sandboxing and physical Windows filesystem
validation remain open under #1, including SMB/UNC and case-sensitive directories.

## Request policy review

- Host filtering defaults to localhost, 127.0.0.1 and ::1. The HTTPS remote-access profile
  requires an explicit external hostname in the allowlist.
- Kestrel defaults to a 64 KiB request body limit. The administrator artwork upload has
  its own 6 MiB limit.
- Session cookies are HttpOnly and SameSite Strict. API writes validate the antiforgery cookie
  and `X-CSRF-TOKEN` header. HTTPS remote access requires Secure cookies.
- Login, setup and password changes share the 10-per-minute per-IP limiter across both API
  prefixes. The proxy configuration accepts forwarded client IP and scheme from one explicitly
  trusted hop. Limits are per process. Test the actual proxy configuration and use a private
  network or VPN.
- Probe arguments are passed separately, network protocols are disabled, output is bounded and the
  process tree is killed on timeout. This is a process lifetime limit, not an OS sandbox.
- Source FFprobe invocations (library scan, download import, staged-source validation and HDR frame
  inspection) restrict demuxers to MOV/MP4, Matroska/WebM and AVI. A supported filename extension
  never authorizes HLS, concat or other indirect inputs to read additional local files. Real-media
  regressions cover all six discovered extensions and disguised HLS/concat inputs. This reduces
  parser exposure; it does not resolve the remaining platform boundaries above or sandbox codec parsing.
  A separate generated-HLS probe permits only HLS/MPEG-TS for server-authored manifests in the
  private conversion cache. Source files never use that policy; real HLS session tests cover it.

Both maintainers should record sign-off in #1 after confirming the request policies on the
intended proxy, testing the Windows filesystem cases above, and agreeing on OS-level isolation
for FFprobe and FFmpeg before handling media from untrusted writers.
