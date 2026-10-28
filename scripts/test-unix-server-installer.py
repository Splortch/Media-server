#!/usr/bin/env python3
"""Install, test real authenticated media IO, reinstall and verify data survives removal."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
mac = sys.platform == 'darwin'
installer, = (ROOT / 'artifacts/packages').glob('*.pkg' if mac else '*.deb')
app = Path('/Applications/MediaServer' if mac else '/opt/media-server')
install = ['sudo', 'installer', '-pkg', str(installer), '-target', '/'] if mac else ['sudo', 'dpkg', '-i', str(installer)]
with tempfile.TemporaryDirectory(prefix='media installed server ') as temp:
    folder = Path(temp)
    media = folder / 'media'
    media.mkdir()
    data = folder / 'data'
    subprocess.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc=size=160x90:rate=10',
                    '-t', '4', '-c:v', 'libvpx-vp9', str(media / 'sample.webm')], check=True)
    original = (media / 'sample.webm').read_bytes()
    try:
        for mode in ('initialize', 'verify'):
            subprocess.run(install, check=True)
            env = dict(os.environ, MediaServer__DataDirectory=str(data),
                       MediaServer__AllowedRoots__0=str(media), MediaServer__FfprobePath=shutil.which('ffprobe'),
                       ASPNETCORE_URLS='http://127.0.0.1:5050')
            launcher = app / ('Start-MediaServer.command' if mac else 'start-media-server.sh')
            with (folder / 'server.log').open('w') as log:
                process = subprocess.Popen(['/bin/bash', str(launcher)], env=env, stdout=log, stderr=subprocess.STDOUT)
                try:
                    for _ in range(120):
                        if process.poll() is not None:
                            raise RuntimeError((folder / 'server.log').read_text())
                        try:
                            with urllib.request.urlopen('http://127.0.0.1:5050/health', timeout=1) as response:
                                if json.load(response)['status'] == 'ok':
                                    break
                        except OSError:
                            time.sleep(.25)
                    else:
                        raise RuntimeError('Installed server startup timed out')
                    subprocess.run([sys.executable, str(ROOT / 'scripts/test-installed-server.py'), str(data), str(media), mode], check=True)
                finally:
                    process.terminate()
                    process.wait(timeout=15)
        if (media / 'sample.webm').read_bytes() != original:
            raise RuntimeError('Installed scanning changed original media')
    finally:
        if mac:
            subprocess.run(['sudo', 'rm', '-rf', str(app)], check=True)
            subprocess.run(['sudo', 'pkgutil', '--forget', 'org.splortch.mediaserver.server'], check=True)
        else:
            subprocess.run(['sudo', 'dpkg', '--remove', 'media-server'], check=True)
    if not (data / 'media-server.db').exists() and not any(data.glob('*.db')):
        raise RuntimeError('Removing the application removed server data')
    print('Installed launcher, web setup, auth, scan, streaming, resume, reinstall and data retention passed.')
