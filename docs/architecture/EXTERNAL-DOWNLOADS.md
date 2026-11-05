# Optional aria2 adapter

The administrator Download Center supports the built-in HTTPS downloader and
**aria2 1.37 JSON-RPC**. aria2 is optional, not bundled. The built-in downloader
and local libraries remain usable without it installed.

## Operator configuration

Install aria2 separately. Run it with a private configuration, random RPC secret
of at least 16 characters, loopback RPC listening, no inherited proxies/netrc,
no session persistence and no debug logging. Example private aria2 config:

```ini
enable-rpc=true
rpc-listen-all=false
rpc-listen-port=6800
rpc-secret=REPLACE_WITH_A_RANDOM_PRIVATE_SECRET
all-proxy=
http-proxy=
https-proxy=
no-netrc=true
follow-torrent=false
follow-metalink=false
console-log-level=error
```

Start `aria2c --conf-path=/path/to/private/aria2.conf`. Set these environment
variables for Media Server, outside the frontend and exported application settings:

```text
Downloads__Aria2__Endpoint=http://127.0.0.1:6800/jsonrpc
Downloads__Aria2__Secret=<the same private RPC secret>
Downloads__Aria2__TrustEndpoint=true
Downloads__Aria2__RelayBaseUrl=http://127.0.0.1:5050
```

RPC requires an exact IP address, port and `/jsonrpc` path. HTTP is allowed only
on loopback; LAN RPC requires HTTPS with a normally trusted certificate including
the IP in its SAN. DNS names, URL credentials, queries and redirects are rejected.
Certificate verification is never disabled. Keep environment/service configuration
private: secrets are never returned through APIs, queue DTOs or logs.

`RelayBaseUrl` names the Media Server origin reachable from aria2. Use HTTPS for
LAN connections, or loopback HTTP on the same host. Reverse proxies must preserve
Authorization headers. aria2 must share `<server-data>/external-downloads`; for
a different mount path, set `Downloads__Aria2__RemoteRoot` to the corresponding
aria2-side path. The server always reads its own fixed local generated path,
never a client-reported path. Library roots must not include private server data.
The daemon is operator-trusted, not sandboxed against a malicious administrator
or a process with the server account's permissions.

## Approval and supported operations

Enable the existing Download Center and approve exact public source hosts.
Configured aria2 appears in the new-download and family-approval forms. Select it
and supply a trusted publisher SHA-256. Existing administrator approval,
library/content permissions, quotas and safe Movies/TV import still apply.

| Operation                    | Semantics                                                                                          |
| ---------------------------- | -------------------------------------------------------------------------------------------------- |
| Add/status                   | One deterministic owned daemon GID per server record; bounded polling                              |
| Pause                        | Revoke relay access and pause/remove the owned daemon task                                         |
| Resume/restart               | Re-fetch the approved source from zero and verify SHA-256; not bandwidth-saving daemon byte resume |
| Cancel                       | Stop source traffic and remove only owned staged data/tasks                                        |
| Priority                     | Server queue order -10 through 10, without preempting the active transfer                          |
| Unavailable/restarted client | Sanitized failure; retain the record and resume after recovery                                     |

Both GID and expected generated output directory/name are checked before
mutations. Foreign tasks remain untouched. Lost submissions/restarts reconcile
the deterministic ID rather than adding duplicates. Unsupported operations/client
names fail explicitly. The admin capability endpoint reports configuration and
semantics, never secrets or paths; configured does not mean currently reachable.

## Source and storage safety

aria2 receives a short-lived, header-authorized relay URL rather than the original
source credentials. Media Server fetches the source through its existing public
HTTPS host allowlist, public-IP/DNS pinning, redirect validation, TLS and byte/time
limits. The relay rechecks permissions, module status, desired state and space,
and is revoked on stop/failure. Local RPC trust never exempts source URLs from
SSRF restrictions. Temporary external copies reserve an extra full size budget;
retained leftovers count against the module limit. The existing hash-verified
staging and safe importer remain responsible for final library delivery.

## Validation

Configuration/authentication/input tests run without aria2. Linux backend CI
installs the actual daemon and runs:

```sh
RUN_ARIA2_INTEGRATION=1 dotnet test MediaServer.sln --filter FullyQualifiedName~Aria2IntegrationTests
python3 scripts/test-external-downloads.py
```

The first uses real aria2, Kestrel, SQLite, worker and relay with controlled source
bytes. It covers pause/resume, priority, cancel, foreign-task ownership, daemon
restart and built-in independence. The second fetches this project's pinned
Apache license through production public HTTPS and verifies the hash/attachment.
Windows runs configuration/authentication tests; physical LAN/TLS deployments
still require operator validation. Protocol reference: the official
[aria2 manual](https://aria2.github.io/manual/en/html/aria2c.html#rpc-interface).
