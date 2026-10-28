#!/usr/bin/env python3
"""Real-server recovery and instance-lock regression. Credentials are isolated test fixtures."""
from contextlib import closing
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
COMMAND = [shutil.which('dotnet') or 'dotnet', str(ROOT / 'backend/src/Api/bin/Debug/net10.0/Api.dll')]


def check():
    with tempfile.TemporaryDirectory(prefix='media-recovery-') as temporary:
        data = Path(temporary)
        env = {**os.environ, 'MediaServer__DataDirectory': temporary}
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        base = f'http://127.0.0.1:{port}'

        def stop(process, log):
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
                raise
            finally:
                log.close()

        def start():
            log = (data / 'test.log').open('a')
            process = subprocess.Popen(COMMAND + ['--urls', base], env=env, cwd=ROOT, stdout=log, stderr=log)
            for _ in range(100):
                try:
                    with urllib.request.urlopen(base + '/health', timeout=1):
                        return process, log
                except OSError:
                    if process.poll() is not None:
                        break
                    time.sleep(.1)
            stop(process, log)
            raise RuntimeError('Fixture server did not start')

        def client():
            return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

        def post(session, endpoint, body):
            with session.open(base + '/api/auth/csrf') as response:
                token = json.load(response)['token']
            request = urllib.request.Request(base + endpoint, data=json.dumps(body).encode(), headers={
                'Content-Type': 'application/json', 'X-CSRF-TOKEN': token,
            })
            try:
                with session.open(request) as response:
                    return response.status
            except urllib.error.HTTPError as error:
                error.close()
                return error.code

        def login(password):
            return post(client(), '/api/auth/login', {'name': 'fixture', 'password': password})

        process, log = start()
        try:
            assert post(client(), '/api/setup', {
                'name': 'fixture', 'password': 'old-fixture-password', 'token': (data / 'setup-token').read_text(),
            }) == 201
            assert login('old-fixture-password') == 200
            blocked = subprocess.run(COMMAND + ['--reset-admin-password'], env=env, cwd=ROOT,
                                     input='should-not-be-applied\n', capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=15)
            assert blocked.returncode == 1 and 'Cannot exclusively lock' in blocked.stderr
            assert login('old-fixture-password') == 200
        finally:
            stop(process, log)

        reset = subprocess.run(COMMAND + ['--reset-admin-password'], env=env, cwd=ROOT,
                               input='new-fixture-password\n', capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)
        assert reset.returncode == 0, 'Offline recovery failed'
        assert 'new-fixture-password' not in reset.stdout + reset.stderr
        with closing(sqlite3.connect(data / 'media.db')) as db:
            assert db.execute('SELECT COUNT(*) FROM Sessions').fetchone()[0] == 0
        process, log = start()
        try:
            assert login('old-fixture-password') == 401
            assert login('new-fixture-password') == 200
            assert not (data / 'setup-token').exists()
        finally:
            stop(process, log)
        print('PASS: live recovery rejected, offline recovery, no password echo, revocation, login and closed setup')


if __name__ == '__main__':
    check()
