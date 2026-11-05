#!/usr/bin/env python3
"""Opt-in real Commons CC0 metadata/approval/guarded download smoke test."""
import hashlib
import importlib.util
import os
from pathlib import Path
import ssl
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('remote_fixture', ROOT / 'scripts/test-remote-proxy.py')
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


def run():
    with tempfile.TemporaryDirectory(prefix='media-commons-') as temporary:
        root = Path(temporary)
        (root / 'media').mkdir()
        origin = 'http://127.0.0.1:' + str(fixture.port())
        env = dict(os.environ, MediaServer__DataDirectory=str(root / 'data'), MediaServer__AllowedRoots__0=str(root / 'media'),
            ASPNETCORE_URLS=origin, Downloads__Commons__Enabled='true',
            Downloads__Commons__ReviewedFiles__0__File='CC0 - Public Domain Dedication video bumper.webm',
            Downloads__Commons__ReviewedFiles__0__Evidence='https://commons.wikimedia.org/wiki/File:CC0_-_Public_Domain_Dedication_video_bumper.webm')
        client = fixture.Client(origin, ssl.create_default_context())
        with (root / 'server.log').open('w') as log:
            process = subprocess.Popen(['dotnet', str(ROOT / 'backend/src/Api/bin/Debug/net10.0/Api.dll')],
                cwd=ROOT / 'backend/src/Api', env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                for _ in range(120):
                    if process.poll() is not None:
                        raise RuntimeError('Commons test server exited')
                    try:
                        if client.request('/health')[0] == 200:
                            break
                    except OSError:
                        pass
                    time.sleep(.2)
                client.data('/api/setup', 'POST', {'name': 'admin', 'password': 'synthetic-catalog-password',
                    'token': (root / 'data/setup-token').read_text().strip()}, 201)
                client.data('/api/auth/login', 'POST', {'name': 'admin', 'password': 'synthetic-catalog-password'})
                client.data('/api/downloads/settings', 'PUT', {'enabled': True, 'hosts': ['upload.wikimedia.org']})
                results = client.data('/api/downloads/catalog?query=CC0')
                if len(results) != 1:
                    raise RuntimeError('Reviewed CC0 fixture is unavailable or rights metadata changed')
                item = results[0]
                assert item['provider'] == 'wikimedia-commons-cc0'
                assert item['licenseUrl'] == 'https://creativecommons.org/publicdomain/zero/1.0/'
                assert client.data('/api/downloads') == []
                row = client.data('/api/downloads', 'POST', {'name': 'CC0 approved integration fixture', 'url': item['url'],
                    'budgetBytes': item['bytes'] + 1024 * 1024, 'sha256': None, 'catalogToken': item['approvalToken']})
                for _ in range(600):
                    row = next(x for x in client.data('/api/downloads') if x['id'] == row['id'])
                    if row['state'] in ('completed', 'failed'):
                        break
                    time.sleep(.2)
                assert row['state'] == 'completed', row.get('error')
                status, body, _ = client.request('/api/downloads/' + row['id'] + '/file')
                assert status == 200 and len(body) == item['bytes']
                assert hashlib.sha1(body).hexdigest() == item['sha1']
                assert hashlib.sha256(body).hexdigest() == row['contentHash']
                print('Real Commons CC0: live provenance, separate administrator approval, guarded HTTPS, size and SHA-1 identity, staged SHA-256/attachment passed.')
            finally:
                process.terminate()
                process.wait(timeout=10)


if __name__ == '__main__':
    run()
