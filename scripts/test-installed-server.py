#!/usr/bin/env python3
"""Real HTTP checks against the installed server controlled by its tray host."""
import http.cookiejar
import json
from pathlib import Path
import sys
import time
import urllib.error
import urllib.request

data, media, mode = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
base = 'http://127.0.0.1:5050'
jar = http.cookiejar.CookieJar()
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))


def request(path, method='GET', body=None, headers=None):
    values = {'Content-Type': 'application/json', **(headers or {})}
    if method != 'GET':
        with client.open(base + '/api/auth/csrf', timeout=5) as response:
            values['X-CSRF-TOKEN'] = json.load(response)['token']
    req = urllib.request.Request(base + path, data=json.dumps(body).encode() if body is not None else None, headers=values, method=method)
    return client.open(req, timeout=10)


try:
    with request('/api/media'):
        raise RuntimeError('Unauthenticated installed media was accessible')
except urllib.error.HTTPError as error:
    if error.code != 401:
        raise
with request('/') as response:
    if b'id="root"' not in response.read():
        raise RuntimeError('Installed browser UI missing')
if mode == 'initialize':
    with request('/api/setup', 'POST', {'name': 'installer-fixture', 'password': 'installer-fixture-password', 'token': (data / 'setup-token').read_text().strip()}):
        pass
with request('/api/auth/login', 'POST', {'name': 'installer-fixture', 'password': 'installer-fixture-password'}):
    pass
if mode == 'initialize':
    with request('/api/libraries', 'POST', {'name': 'Fixture', 'path': str(media)}) as response:
        library = json.load(response)['id']
    with request(f'/api/libraries/{library}/scan', 'POST') as response:
        job = json.load(response)['id']
    for _ in range(120):
        with request(f'/api/scans/{job}') as response:
            state = json.load(response)
        if state['state'] == 'completed':
            if state['failed'] or state['discovered'] != 1:
                raise RuntimeError('Installed scan did not identify the generated fixture')
            break
        if state['state'] == 'failed':
            raise RuntimeError('Installed scan failed')
        time.sleep(.25)
    else:
        raise RuntimeError('Installed scan timed out')
with request('/api/media') as response:
    items = json.load(response)
if len(items) != 1:
    raise RuntimeError('Installed library was lost or fixture was not indexed')
item = items[0]['id']
with request(f'/api/media/{item}/stream', headers={'Range': 'bytes=0-15'}) as response:
    if response.status != 206 or len(response.read()) != 16:
        raise RuntimeError('Installed authenticated ranges failed')
if mode == 'initialize':
    with request(f'/api/v1/media/{item}/progress-sync', 'PUT', {'seconds': 1, 'expectedRevision': 0}):
        pass
with request(f'/api/v1/media/{item}/progress') as response:
    if json.load(response)['seconds'] != 1:
        raise RuntimeError('Installed playback progress was not retained')
print('Installed server: UI, auth, FFprobe scanning, range streaming and persisted resume passed (' + mode + ').')
