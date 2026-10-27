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

These checks do not sandbox FFprobe, prevent a trusted local owner from rewriting existing media,
or make hostile shared filesystems safe. Media roots and their ancestors must remain controlled by
the operator. Scanning still invokes FFprobe by pathname: a local writer can race scanning, so do
not treat uploads/untrusted writable roots as supported input. Kernel process isolation, resource
quotas and stronger scan-handle integration remain follow-up work; no internet-facing readiness or
independent security audit is claimed.

## Request policy review

- Host filtering defaults to localhost, 127.0.0.1 and ::1; configure exact hosts explicitly.
- Kestrel limits request bodies to 64 KiB; no upload API exists.
- Cookie and antiforgery defaults remain HttpOnly/Strict, with HTTPS secure-cookie guidance unchanged.
- Login, setup and password changes share the existing per-IP 10/minute limiter. Forwarded headers
  are not trusted implicitly. Use a private network/VPN; do not put this alpha on an open listener.
- Probe arguments are passed separately, network protocols are disabled, output is bounded and the
  process tree is killed on timeout. This is a process lifetime limit, not an OS sandbox.

The two maintainers should review these policies and Windows filesystem behavior before enabling
remote deployment. Physical Windows, SMB/UNC, case-sensitive Windows directory and screen-reader
coverage are not implied by a passing Linux test run.
