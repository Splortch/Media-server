# Optional secure remote access (#19)

Start with a private VPN whose devices and access rules you control. Keep the
backend bound to its default `127.0.0.1:5050`. Do not forward port 5050 from your
router. The server does not require a cloud account, open router port, remote
service, or public listener. Remote clients must still use trusted HTTPS; a VPN
is an additional boundary, not a reason to disable certificate verification.

## TLS reference: host-local Caddy

The tested reference configuration is [docker/remote/Caddyfile](../../docker/remote/Caddyfile).
Caddy runs on the same host and connects to the loopback-only backend. Despite
its directory name, this file is not a Docker Compose deployment: `127.0.0.1`
inside a different container is not the server. Do not publish the backend to fix
container networking. Plan and explicitly restrict a private container network
before using a containerized proxy.

1. Establish private VPN access for the intended household devices. Keep the
   server's normal account authentication and library permissions enabled. Review
   guest access: an opted-in guest can enter without a password from any device
   that can reach the service.
2. Choose a hostname, for example `media.example`, and provision a certificate and
   private key trusted by your clients for that exact hostname. Do not commit the
   key, disable verification, or reuse the ephemeral integration-test certificate.
3. Configure the **native backend environment**, then restart the server:

   ```text
   ASPNETCORE_URLS=http://127.0.0.1:5050
   AllowedHosts=localhost;127.0.0.1;[::1];media.example
   MediaServer__ExternalUrl=https://media.example
   MediaServer__SecureCookies=true
   MediaServer__TrustedProxies__0=127.0.0.1
   ```

   These are environment variables, not an automatically loaded `.env` file.
   Use your platform's service/environment configuration. ExternalUrl accepts
   only an HTTPS origin, with no credentials, path, query or fragment. Subpath
   hosting is not supported. The external hostname must be explicitly listed;
   wildcard AllowedHosts is rejected for remote configuration. Secure cookies
   require signing in through HTTPS, including from the same computer.
4. Configure Caddy's environment (private key readable only by the proxy account):

   ```text
   MEDIA_SERVER_HOST=media.example:443
   MEDIA_SERVER_UPSTREAM=127.0.0.1:5050
   MEDIA_SERVER_CERT=/private/path/fullchain.pem
   MEDIA_SERVER_KEY=/private/path/private-key.pem
   ```

   Validate and run the file with `caddy validate --config docker/remote/Caddyfile
   --adapter caddyfile` and `caddy run --config docker/remote/Caddyfile --adapter
   caddyfile`. On production installations use the proxy's normal service
   management. Automatic certificate issuance and router exposure are not enabled
   by this reference file. Limit proxy access to your intended VPN/network.
5. Open `https://media.example`, sign in, then inspect **Settings → Remote access**.
   The current request should be HTTPS, secure-only cookies should be on, and the
   trusted proxy count should match your explicit configuration. These are
   configuration checks, not a security certification.
6. Test library browsing, seeking, subtitle/HLS playback and logout from a second
   trusted device before relying on remote playback. Use the normal browser/client
   connection screens; certificate errors require a trust/hostname fix.

## Trust boundary

Forwarded headers are processed only when explicit proxy IPs are configured;
with the default empty list, the middleware is not registered. Clearing framework
trust lists alone would trust every peer, so the implementation does not do that.
Only X-Forwarded-For and X-Forwarded-Proto are accepted, with a single-hop limit and
header symmetry. Forwarded host is ignored: the proxy preserves the real Host
header, and the existing AllowedHosts filter remains authoritative. Proxy IPs are
literal addresses, not wildcard ranges or hostnames. Restrict who can connect to
the upstream; a configured proxy address is a trusted identity.

The proxy forwards range headers, authenticated manifests/segments, cookies and
WebSocket upgrade requests. It does not cache private media or bypass
application authorization. TLS does not make hostile media safe to decode:
FFprobe/codec sandboxing and the two-maintainer review in #1 remain outstanding.

## Automated validation

`python3 scripts/test-remote-proxy.py` uses a real backend, Caddy, FFmpeg, synthetic
media and an ephemeral certificate trusted explicitly by the test client. It
checks secure authentication/CSRF cookies, scanning, HTTP 206 byte ranges,
authenticated HLS assets, SignalR WebSocket negotiation/upgrade and logout. It
never disables certificate verification. Ubuntu backend CI runs this test; Caddy
and OpenSSL are test tools, not new application dependencies. Unit tests reject
insecure configurations and verify that spoofed forwarded headers from an
untrusted peer do not change the request scheme, address or host.

This is automated local-host/proxy evidence. Physical remote-device, VPN/router,
public-network and administrator security review remain separate checks.
