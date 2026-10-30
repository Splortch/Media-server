#!/usr/bin/env python3
"""Unpack the actual deliverable and verify startup, UI, version and auth isolation."""
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

root = Path(__file__).resolve().parents[1]
archives = list((root / 'artifacts/packages').iterdir())
if len(archives) != 1:
    raise RuntimeError('Expected exactly one packaged server')
with tempfile.TemporaryDirectory(prefix='media release smoke ') as temporary:
    folder = Path(temporary).resolve()
    shutil.unpack_archive(archives[0], folder / 'unpacked')
    package, = (folder / 'unpacked').iterdir()
    inventory = json.loads((package / 'inventory/SBOM.spdx.json').read_text(encoding='utf-8'))
    evidence = json.loads((package / 'inventory/THIRD-PARTY-INVENTORY.json').read_text(encoding='utf-8'))
    if inventory['spdxVersion'] != 'SPDX-2.3' or not inventory['packages'] or evidence['runtime'] != os.environ['RID']:
        raise RuntimeError('Packaged dependency inventory is missing or mismatched')
    if inventory['name'] != 'MediaServer-' + evidence['runtime']:
        raise RuntimeError('SBOM runtime does not match package')
    for component in evidence['components']:
        for notice in component['notices']:
            path = (package / 'inventory' / notice).resolve()
            if not path.is_relative_to((package / 'inventory/license-evidence').resolve()) or not path.is_file():
                raise RuntimeError('Packaged license evidence is incomplete')
    executable = package / ('Api.exe' if os.name == 'nt' else 'Api')
    if os.name != 'nt' and (not os.access(executable, os.X_OK) or not os.access(package / ('Start-MediaServer.command' if sys.platform == 'darwin' else 'start-media-server.sh'), os.X_OK)):
        raise RuntimeError('Linux archive did not preserve executable permissions')
    runtime = package / ('coreclr.dll' if os.name == 'nt' else 'libcoreclr.dylib' if sys.platform == 'darwin' else 'libcoreclr.so')
    if not runtime.is_file() or not (package / 'System.Private.CoreLib.dll').is_file():
        raise RuntimeError('The archive is not self-contained')
    with socket.socket() as available:
        available.bind(('127.0.0.1', 0))
        port = available.getsockname()[1]
    url = f'http://127.0.0.1:{port}'
    env = dict(os.environ, ASPNETCORE_URLS=url, MediaServer__DataDirectory=str(folder / 'data'), DOTNET_ROOT=str(folder / 'no-global-runtime'), DOTNET_MULTILEVEL_LOOKUP='0')
    with (folder / 'server.log').open('w', encoding='utf-8') as log:
        process = subprocess.Popen([str(executable)], cwd=package, env=env, stdout=log, stderr=subprocess.STDOUT)
        try:
            for _ in range(120):
                if process.poll() is not None:
                    raise RuntimeError('Packaged executable exited during startup')
                try:
                    with urllib.request.urlopen(url + '/health', timeout=1) as response:
                        if json.load(response)['status'] == 'ok':
                            break
                except OSError:
                    time.sleep(.25)
            else:
                raise RuntimeError('Packaged server startup timed out')
            with urllib.request.urlopen(url, timeout=5) as response:
                if 'id="root"' not in response.read().decode():
                    raise RuntimeError('Packaged web UI is missing')
            with urllib.request.urlopen(url + '/api/v1/server-info', timeout=5) as response:
                if json.load(response)['serverVersion'] != os.environ['SERVER_VERSION']:
                    raise RuntimeError('Packaged version does not match the download version')
            try:
                urllib.request.urlopen(url + '/api/media', timeout=5)
                raise RuntimeError('Unauthenticated packaged media access was allowed')
            except urllib.error.HTTPError as error:
                if error.code != 401:
                    raise
            if not (folder / 'data/setup-token').is_file():
                raise RuntimeError('Packaged setup token was not initialized')
            print('Archive starts without the SDK, includes web UI, reports the stamped version, and protects media.')
        finally:
            process.terminate()
            process.wait(timeout=15)
