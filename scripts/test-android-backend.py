#!/usr/bin/env python3
"""Install the real APK in an emulator and exercise a fresh real server using synthetic media."""
import http.cookiejar
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.request
import urllib.error
root = Path(__file__).resolve().parents[1]
base = 'http://127.0.0.1:5050'
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
def request(path, body=None, method='GET'):
    headers = {}
    if method not in ('GET', 'HEAD'):
        headers['X-CSRF-TOKEN'] = request('/auth/csrf')['token']
        headers['Content-Type'] = 'application/json'
    with opener.open(urllib.request.Request(base + '/api/v1' + path, data=json.dumps(body).encode() if body is not None else None, headers=headers, method=method), timeout=15) as response:
        data = response.read()
        return json.loads(data) if data else None
with tempfile.TemporaryDirectory(prefix='android-real-server-') as temp:
    folder = Path(temp); media = folder / 'media'; media.mkdir(); data = folder / 'data'
    subprocess.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc=size=320x180:rate=24', '-f', 'lavfi', '-i', 'sine=frequency=440', '-t', '60', '-c:v', 'libvpx-vp9', '-c:a', 'libopus', '-y', str(media / 'android-sample.webm')], check=True)
    env = dict(os.environ, MediaServer__DataDirectory=str(data), MediaServer__AllowedRoots__0=str(media), ASPNETCORE_URLS=base)
    with open(folder / 'server.log', 'w+') as log:
        server = subprocess.Popen(['dotnet', str(root / 'backend/src/Api/bin/Debug/net10.0/Api.dll')], cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
        try:
            for _ in range(150):
                try:
                    urllib.request.urlopen(base + '/health', timeout=1).close(); break
                except OSError:
                    if server.poll() is not None: raise RuntimeError('Fixture backend failed to start')
                    time.sleep(.2)
            else: raise RuntimeError('Fixture backend startup timed out')
            credentials = {'name': 'android-fixture', 'password': 'android-fixture-password'}
            request('/setup', dict(credentials, token=(data / 'setup-token').read_text()), 'POST')
            request('/auth/login', dict(credentials, deviceName='host-fixture'), 'POST')
            library = request('/libraries', {'name': 'Android synthetic', 'path': str(media), 'kind': 'movies'}, 'POST')
            job = request(f'/libraries/{library["id"]}/scan', {}, 'POST')
            for _ in range(150):
                status = request(f'/scans/{job["id"]}')
                if status['state'] == 'completed': break
                if status['state'] in ('failed', 'rejected'): raise RuntimeError('Fixture scan failed')
                time.sleep(.2)
            else: raise RuntimeError('Fixture scan timed out')
            assert request('/media-page')['items'][0]['id'] == 1
            try:
                urllib.request.urlopen(base + '/api/v1/media/1/stream', timeout=10).close()
                raise AssertionError('Unauthenticated media stream was accessible')
            except urllib.error.HTTPError as error:
                assert error.code == 401
            request('/media/1/progress-sync', {'seconds': 3, 'expectedRevision': 0}, 'PUT')
            subprocess.run(['adb', 'reverse', 'tcp:5050', 'tcp:5050'], check=True)
            outputs = root / 'clients/android/app/build/outputs'
            for apk in ['apk/debug/app-debug.apk', 'apk/androidTest/debug/app-debug-androidTest.apk']:
                subprocess.run(['adb', 'install', '-r', str(outputs / apk)], check=True)
            result = subprocess.run(['adb', 'shell', 'am', 'instrument', '-w', 'org.splortch.mediaserver.android.debug.test/androidx.test.runner.AndroidJUnitRunner'], capture_output=True, text=True, timeout=240)
            print(result.stdout); print(result.stderr)
            if result.returncode or 'OK (1 test)' not in result.stdout or 'FAILURES' in result.stdout: raise RuntimeError('Android real-server instrumentation failed')
            assert request('/media/1/progress')['seconds'] >= 15
        finally:
            server.terminate(); server.wait(timeout=15)
