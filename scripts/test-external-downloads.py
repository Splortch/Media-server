#!/usr/bin/env python3
"""Actual aria2 RPC + Kestrel + public HTTPS + SQLite/staging integration."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import tempfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('remote_fixture', ROOT / 'scripts/test-remote-proxy.py')
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


def run():
    for name in ('aria2c', 'dotnet'):
        if not shutil.which(name):
            raise RuntimeError('Actual external-client integration requires ' + name)
    with tempfile.TemporaryDirectory(prefix='media-external-') as temporary:
        root = Path(temporary)
        media = root / 'media'
        media.mkdir()
        api_port, rpc_port = fixture.port(), fixture.port()
        origin = f'http://127.0.0.1:{api_port}'
        endpoint = f'http://127.0.0.1:{rpc_port}/jsonrpc'
        secret = os.urandom(24).hex()
        env = dict(os.environ, MediaServer__DataDirectory=str(root / 'data'),
                   MediaServer__AllowedRoots__0=str(media), ASPNETCORE_URLS=origin,
                   Downloads__Aria2__Endpoint=endpoint, Downloads__Aria2__Secret=secret,
                   Downloads__Aria2__TrustEndpoint='true', Downloads__Aria2__RelayBaseUrl=origin)
        admin = fixture.Client(origin, ssl.create_default_context())
        anonymous = fixture.Client(origin, ssl.create_default_context())
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

        def rpc(method, *arguments):
            request = urllib.request.Request(endpoint, json.dumps({'jsonrpc': '2.0', 'id': 'fixture',
                'method': 'aria2.' + method, 'params': ['token:' + secret, *arguments]}).encode(),
                {'Content-Type': 'application/json'})
            with opener.open(request, timeout=10) as response:
                return json.load(response)

        def job(identifier):
            return next(row for row in admin.data('/api/downloads') if row['id'] == identifier)

        def until(predicate, seconds=60):
            deadline = time.monotonic() + seconds
            while time.monotonic() < deadline:
                value = predicate()
                if value:
                    return value
                time.sleep(.15)
            raise RuntimeError('External-client integration condition timed out')

        processes = []
        with (root / 'server.log').open('w') as server_log, (root / 'aria2.log').open('w') as aria_log:
            try:
                processes.append(subprocess.Popen(['aria2c', '--enable-rpc', '--rpc-secret=' + secret,
                    '--rpc-listen-port=' + str(rpc_port), '--rpc-listen-all=false', '--enable-color=false',
                    '--console-log-level=error', '--max-download-limit=2K', '--all-proxy=', '--http-proxy=',
                    '--https-proxy=', '--no-netrc=true'], stdout=aria_log, stderr=subprocess.STDOUT))
                processes.append(subprocess.Popen(['dotnet', str(ROOT / 'backend/src/Api/bin/Debug/net10.0/Api.dll')],
                    cwd=ROOT / 'backend/src/Api', env=env, stdout=server_log, stderr=subprocess.STDOUT))

                def ready():
                    if any(p.poll() is not None for p in processes):
                        raise RuntimeError('Integration process exited')
                    try:
                        return admin.request('/health')[0] == 200
                    except OSError:
                        return False

                until(ready)
                assert rpc("getVersion").get("result", {}).get("version")
                admin.data('/api/setup', 'POST', {'name': 'admin', 'password': 'external-integration-password',
                    'token': (root / 'data/setup-token').read_text().strip()}, 201)
                admin.data('/api/auth/login', 'POST', {'name': 'admin', 'password': 'external-integration-password'})
                assert anonymous.request('/api/downloads/clients')[0] == 401
                assert anonymous.request('/api/download-relay/' + 'a' * 32)[0] == 404
                capabilities = admin.data('/api/downloads/clients')
                assert capabilities[0]['id'] == 'aria2' and capabilities[0]['configured']
                assert secret not in json.dumps(capabilities)
                admin.data('/api/downloads/settings', 'PUT', {'enabled': True, 'hosts': ['raw.githubusercontent.com']})
                # A known immutable revision and project-authored Apache-licensed fixture.
                source = 'https://raw.githubusercontent.com/Splortch/Media-server/f3181a0d704aad07f4a197652cd01a7f4922f7ee/LICENSE'
                checksum = hashlib.sha256((ROOT / 'LICENSE').read_bytes()).hexdigest()
                input_data = {'name': 'Actual aria2 fixture', 'url': source, 'budgetBytes': 1024 * 1024,
                              'sha256': checksum, 'priority': 0, 'transferClient': 'aria2'}
                created = admin.data('/api/downloads', 'POST', input_data)
                identifier = created['id']
                until(lambda: job(identifier)['state'] == 'running')
                state = job(identifier)
                admin.data('/api/downloads/' + identifier, 'PUT', {'action': 'pause', 'expectedRevision': state['revision']})
                until(lambda: job(identifier)['state'] == 'paused')
                state = job(identifier)
                admin.data('/api/downloads/' + identifier, 'PUT', {'action': 'priority', 'priority': 8, 'expectedRevision': state['revision']})
                state = job(identifier)
                admin.data('/api/downloads/' + identifier, 'PUT', {'action': 'resume', 'expectedRevision': state['revision']})
                until(lambda: job(identifier)['state'] in ('completed', 'failed'))
                state = job(identifier)
                assert state['state'] == 'completed', state.get('error')
                assert state['contentHash'] == checksum
                status, body, _ = admin.request('/api/downloads/' + identifier + '/file')
                assert status == 200 and hashlib.sha256(body).hexdigest() == checksum
                assert not list((root / 'data/external-downloads').glob('*/payload'))
                # Unavailable clients fail safely and ordinary built-in transfers still work.
                processes[0].terminate()
                processes[0].wait(timeout=10)
                unavailable = admin.data('/api/downloads', 'POST', input_data)
                until(lambda: job(unavailable['id'])['state'] == 'failed')
                built_in = admin.data('/api/downloads', 'POST', dict(input_data, transferClient='builtin'))
                until(lambda: job(built_in['id'])['state'] in ('completed', 'failed'))
                assert job(built_in['id'])['state'] == 'completed'
                assert secret not in (root / 'server.log').read_text()
                print('Actual aria2: authenticated transfer, pause/re-fetch resume, local priority, SHA-256, confined staging, unavailable-client isolation and built-in fallback passed.')
            except Exception:
                # Sanitized error codes only; never print client options, URL headers or secrets.
                shutil.copyfile(root / 'server.log', '/tmp/media-external-server.log')
                shutil.copyfile(root / 'aria2.log', '/tmp/media-external-aria.log')
                print('External integration failed; inspect private fixture logs locally.')
                print('Queue:', [(row['state'], row.get('error')) for row in admin.data('/api/downloads')])
                raise
            finally:
                for process in reversed(processes):
                    if process.poll() is None:
                        process.terminate()
                        process.wait(timeout=10)


if __name__ == '__main__':
    run()
