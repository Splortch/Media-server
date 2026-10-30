#!/usr/bin/env python3
"""Real TLS/Caddy/backend integration. Ephemeral certificates are trusted explicitly."""
import base64
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


class Client:
    def __init__(self, origin, tls):
        self.origin = origin
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=tls), urllib.request.HTTPCookieProcessor(self.cookies))

    def request(self, path, method='GET', body=None, headers=None):
        fields = dict(headers or {})
        if method not in ('GET', 'HEAD') and path.startswith('/api'):
            fields['X-CSRF-TOKEN'] = json.loads(self.request('/api/auth/csrf')[1])['token']
        if body is not None:
            fields['Content-Type'] = 'application/json'
        request = urllib.request.Request(self.origin + path, data=json.dumps(body).encode() if body is not None else None, method=method, headers=fields)
        try:
            response = self.opener.open(request, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, response.read(), response.headers

    def data(self, path, method='GET', body=None, expected=200):
        status, raw, _ = self.request(path, method, body)
        if status != expected:
            raise RuntimeError(f'{method} {path.split("?")[0]} returned {status}, expected {expected}')
        return json.loads(raw) if raw else None


def websocket(client, tls, proxy_port, expected):
    token = ''
    if expected == 101:
        result = client.data('/hubs/status/negotiate?negotiateVersion=1', 'POST')
        token = '?id=' + urllib.parse.quote(result['connectionToken'], safe='')
    key = base64.b64encode(os.urandom(16)).decode()
    cookie = '; '.join(value.name + '=' + value.value for value in client.cookies)
    request = f'GET /hubs/status{token} HTTP/1.1\r\nHost: localhost:{proxy_port}\r\nOrigin: https://localhost:{proxy_port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nCookie: {cookie}\r\n\r\n'
    with socket.create_connection(('127.0.0.1', proxy_port), timeout=10) as raw:
        with tls.wrap_socket(raw, server_hostname='localhost') as connection:
            connection.sendall(request.encode())
            response = b''
            while b'\r\n\r\n' not in response and len(response) < 65536:
                chunk = connection.recv(4096)
                if not chunk:
                    break
                response += chunk
            if response.split(b'\r\n')[0].split()[1] != str(expected).encode():
                raise RuntimeError('WebSocket proxy authentication/upgrade failed')


def run():
    for binary in ('dotnet', 'caddy', 'openssl', 'ffmpeg', 'ffprobe'):
        if not shutil.which(binary):
            raise RuntimeError('Integration requires ' + binary)
    with tempfile.TemporaryDirectory(prefix='media-remote-') as directory:
        root = Path(directory); media = root / 'media'; media.mkdir()
        cert, key = root / 'tls.crt', root / 'tls.key'
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1', '-keyout', str(key), '-out', str(cert)], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=160x90:rate=12', '-f', 'lavfi', '-i', 'sine=frequency=440', '-t', '2', '-c:v', 'libvpx-vp9', '-c:a', 'libopus', str(media / 'sample.webm')], check=True)
        api_port, proxy_port = port(), port()
        env = dict(os.environ, MediaServer__DataDirectory=str(root / 'data'), MediaServer__AllowedRoots__0=str(media),
                   ASPNETCORE_URLS=f'http://127.0.0.1:{api_port}', MediaServer__ExternalUrl=f'https://localhost:{proxy_port}',
                   MediaServer__SecureCookies='true', MediaServer__TrustedProxies__0='127.0.0.1', AllowedHosts='localhost;127.0.0.1',
                   XDG_DATA_HOME=str(root / 'caddy-data'), XDG_CONFIG_HOME=str(root / 'caddy-config'), MEDIA_SERVER_HOST=f'localhost:{proxy_port}', MEDIA_SERVER_UPSTREAM=f'127.0.0.1:{api_port}', MEDIA_SERVER_CERT=str(cert), MEDIA_SERVER_KEY=str(key))
        tls = ssl.create_default_context(cafile=str(cert))
        client = Client(f'https://localhost:{proxy_port}', tls); anonymous = Client(client.origin, tls)
        processes = []
        with (root / 'backend.log').open('w') as backend_log, (root / 'proxy.log').open('w') as proxy_log:
            try:
                processes.append(subprocess.Popen(['dotnet', str(ROOT / 'backend/src/Api/bin/Debug/net10.0/Api.dll')], cwd=ROOT / 'backend/src/Api', env=env, stdout=backend_log, stderr=subprocess.STDOUT))
                processes.append(subprocess.Popen(['caddy', 'run', '--config', str(ROOT / 'docker/remote/Caddyfile'), '--adapter', 'caddyfile'], env=env, stdout=proxy_log, stderr=subprocess.STDOUT))
                ready = False
                for _ in range(120):
                    if any(value.poll() is not None for value in processes):
                        raise RuntimeError('Backend or proxy exited; inspect the fixture logs')
                    try:
                        ready = client.data('/health')['status'] == 'ok'
                        if ready:
                            break
                    except (OSError, RuntimeError):
                        time.sleep(.25)
                if not ready:
                    raise RuntimeError('TLS proxy startup timed out')
                client.data('/api/setup', 'POST', {'name': 'admin', 'password': 'synthetic-remote-password', 'token': (root / 'data/setup-token').read_text().strip()}, 201)
                client.data('/api/auth/login', 'POST', {'name': 'admin', 'password': 'synthetic-remote-password'})
                if not all(value.secure for value in client.cookies):
                    raise RuntimeError('TLS authentication or CSRF cookies are not Secure')
                readiness = client.data('/api/v1/remote-readiness')
                if not readiness['requestIsHttps'] or readiness['warnings'] or readiness['trustedProxyCount'] != 1:
                    raise RuntimeError('Trusted proxy HTTPS processing is not ready')
                library = client.data('/api/libraries', 'POST', {'name': 'TLS fixture', 'path': str(media)}, 201)
                scan = client.data(f'/api/libraries/{library["id"]}/scan', 'POST', expected=202)
                for _ in range(100):
                    state = client.data(f'/api/scans/{scan["id"]}')
                    if state['state'] not in ('queued', 'running'):
                        break
                    time.sleep(.2)
                items = client.data('/api/media'); item, = items
                status, body, headers = client.request(f'/api/media/{item["id"]}/stream', headers={'Range': 'bytes=0-31'})
                if status != 206 or len(body) != 32 or not headers.get('Content-Range', '').startswith('bytes 0-31/'):
                    raise RuntimeError('TLS proxy lost HTTP range semantics')
                conversion = client.data(f'/api/v1/media/{item["id"]}/playback-sessions', 'POST', {'encoder': 'libx264'}, 202)
                for _ in range(200):
                    conversion = client.data('/api/v1/playback-sessions/' + conversion['id'])
                    if conversion['state'] != 'preparing':
                        break
                    time.sleep(.2)
                if conversion['state'] != 'ready':
                    raise RuntimeError('TLS fixture HLS preparation failed')
                manifest = conversion['manifest']
                master = client.request(manifest)[1].decode()
                variant = next(line for line in master.splitlines() if line and not line.startswith('#'))
                variant_path = urllib.parse.urljoin(manifest, variant)
                playlist = client.request(variant_path)[1].decode()
                segment = next(line for line in playlist.splitlines() if line and not line.startswith('#'))
                segment_path = urllib.parse.urljoin(variant_path, segment)
                if client.request(segment_path)[0] != 200 or anonymous.request(segment_path)[0] != 401:
                    raise RuntimeError('TLS HLS asset authorization failed')
                websocket(client, tls, proxy_port, 101); websocket(anonymous, tls, proxy_port, 401)
                client.data('/api/auth/logout', 'POST', expected=204)
                if client.request(f'/api/media/{item["id"]}/stream')[0] != 401:
                    raise RuntimeError('Logout did not revoke proxied media access')
                print('Actual trusted TLS/Caddy: setup, Secure cookies, login, scan, ranges, HLS asset ACL, authenticated WebSocket upgrade and logout passed.')
            finally:
                for process in reversed(processes):
                    if process.poll() is None:
                        process.terminate()
                    process.wait(timeout=15)


if __name__ == '__main__':
    run()
