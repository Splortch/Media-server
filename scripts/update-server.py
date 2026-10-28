#!/usr/bin/env python3
"""Optional, operator-invoked native update manager. Never edits original media.

stage verifies a release SHA-256 and tests startup, UI, probing, ranges and progress on synthetic media.
activate backs up offline data and validates migrations on a restored copy before an atomic pointer switch.
rollback restores a verified backup to a NEW directory; it never overwrites the failed database.
"""
import argparse
from contextlib import contextmanager
import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tarfile
import tempfile
import time
import urllib.request
import uuid
import zipfile


@contextmanager
def lease(data):
    data.mkdir(parents=True, exist_ok=True)
    with (data / '.server.lock').open('a+b') as handle:
        if os.name != 'nt':
            import fcntl
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        else:
            import msvcrt
            if handle.seek(0, 2) == 0:
                handle.write(b'0')
                handle.flush()
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        # Windows open also fails while the .NET server holds its no-sharing handle.
        yield


def atomic_json(path, value):
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        with temporary.open('x', encoding='utf-8') as stream:
            json.dump(value, stream, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def read_state(root):
    path = root / 'current.json'
    return json.loads(path.read_text()) if path.exists() else None


def executable(root, version):
    if not version or any(c not in '0123456789abcdefghijklmnopqrstuvwxyz.-' for c in version):
        raise ValueError('Invalid installed version')
    folder = root / 'releases' / version
    matches = list(folder.rglob('Api.exe' if os.name == 'nt' else 'Api'))
    if len(matches) != 1 or matches[0].is_symlink():
        raise ValueError('Expected one runnable native server for this OS')
    return matches[0]


def unpack(archive, folder):
    def safe(name):
        if not name or name.startswith('/') or '\\' in name or ':' in name or '..' in Path(name).parts:
            raise ValueError('Unsafe archive pathname')
        return folder / name
    if zipfile.is_zipfile(archive):
        with zipfile.ZipFile(archive) as source:
            entries = source.infolist()
            if len(entries) > 50000 or sum(e.file_size for e in entries) > 4 * 1024**3:
                raise ValueError('Release archive exceeds limits')
            for entry in entries:
                target = safe(entry.filename)
                if ((entry.external_attr >> 16) & 0xf000) == 0xa000:
                    raise ValueError('Archive links are not accepted')
                if entry.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with source.open(entry) as input_file, target.open('xb') as output:
                        shutil.copyfileobj(input_file, output)
    else:
        with tarfile.open(archive) as source:
            entries = source.getmembers()
            if len(entries) > 50000 or sum(e.size for e in entries) > 4 * 1024**3:
                raise ValueError('Release archive exceeds limits')
            for entry in entries:
                target = safe(entry.name)
                if entry.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                elif entry.isfile():
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with source.extractfile(entry) as input_file, target.open('xb') as output:
                        shutil.copyfileobj(input_file, output)
                    target.chmod(0o755 if entry.mode & 0o111 else 0o644)
                else:
                    raise ValueError('Archive links/devices are not accepted')


@contextmanager
def running(binary, data, roots=(), ffprobe='ffprobe'):
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    env = {**os.environ, 'MediaServer__DataDirectory': str(data), 'MediaServer__FfprobePath': ffprobe}
    for index, media in enumerate(roots):
        env[f'MediaServer__AllowedRoots__{index}'] = str(media)
    with tempfile.TemporaryFile() as log:
        process = subprocess.Popen([str(binary), '--urls', base], cwd=binary.parent, env=env, stdout=log, stderr=log)
        try:
            for _ in range(300):
                try:
                    with urllib.request.urlopen(base + '/health', timeout=1) as response:
                        if response.status == 200:
                            break
                except OSError:
                    if process.poll() is not None:
                        raise RuntimeError('Candidate exited before becoming healthy')
                    time.sleep(.1)
            else:
                raise RuntimeError('Candidate health check timed out')
            yield base
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)


def command(binary, data, action, archive):
    env = {**os.environ, 'MediaServer__DataDirectory': str(data)}
    result = subprocess.run([str(binary), action, str(archive)], cwd=binary.parent, env=env,
                            capture_output=True, timeout=120)
    if result.returncode:
        raise RuntimeError(f'{action} failed; no installation pointer was changed')


def verify_playback(binary, ffprobe, expected_version=None):
    ffmpeg = shutil.which('ffmpeg')
    if not ffmpeg or not (shutil.which(ffprobe) or Path(ffprobe).is_file()):
        raise RuntimeError('FFmpeg and FFprobe are required for the synthetic playback gate')
    with tempfile.TemporaryDirectory(prefix='media-update-test-') as temporary:
        work = Path(temporary)
        media = work / 'media'
        media.mkdir()
        subprocess.run([ffmpeg, '-v', 'error', '-f', 'lavfi', '-i', 'testsrc=size=160x90:rate=10',
                        '-t', '3', '-c:v', 'libvpx-vp9', '-y', str(media / 'sample.webm')], check=True, timeout=60)
        with running(binary, work / 'data', [media], ffprobe) as base:
            client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
            def get(path):
                with client.open(base + path, timeout=10) as response:
                    return json.load(response)
            def write(path, body, method='POST'):
                token = get('/api/auth/csrf')['token']
                request = urllib.request.Request(base + path, method=method, data=json.dumps(body).encode(),
                          headers={'Content-Type': 'application/json', 'X-CSRF-TOKEN': token})
                with client.open(request, timeout=30) as response:
                    content = response.read()
                    return json.loads(content) if content else None
            with client.open(base, timeout=10) as response:
                if b'id="root"' not in response.read():
                    raise RuntimeError('Candidate web interface is missing')
            info=get('/api/v1/server-info')
            if 'maintenance-v1' not in info['features']:
                raise RuntimeError('This build predates the managed backup/restore protocol')
            if expected_version and info['serverVersion'] != expected_version:
                raise RuntimeError('Candidate version does not match the requested version')
            password = uuid.uuid4().hex
            write('/api/setup', {'name': 'update-test', 'password': password,
                                'token': (work / 'data/setup-token').read_text()})
            write('/api/auth/login', {'name': 'update-test', 'password': password})
            library = write('/api/libraries', {'name': 'Synthetic test', 'path': str(media)})
            job = write(f'/api/libraries/{library["id"]}/scan', {})
            for _ in range(200):
                state = get(f'/api/scans/{job["id"]}')
                if state['state'] == 'completed':
                    break
                if state['state'] in ('failed', 'interrupted', 'rejected'):
                    raise RuntimeError('Candidate could not scan the synthetic video')
                time.sleep(.1)
            else:
                raise RuntimeError('Candidate scan timed out')
            items = get('/api/media')
            if len(items) != 1:
                raise RuntimeError('Synthetic video was not identified')
            item = items[0]['id']
            request = urllib.request.Request(base + f'/api/media/{item}/stream', headers={'Range': 'bytes=0-15'})
            with client.open(request) as response:
                if response.status != 206 or len(response.read()) != 16:
                    raise RuntimeError('Candidate range playback failed')
            write(f'/api/v1/media/{item}/progress-sync', {'seconds': 1, 'expectedRevision': 0}, 'PUT')
            if get(f'/api/v1/media/{item}/progress')['seconds'] != 1:
                raise RuntimeError('Candidate progress persistence failed')


def stage(root, archive, expected, version, ffprobe):
    with archive.open('rb') as stream:
        if hashlib.file_digest(stream, 'sha256').hexdigest().lower() != expected.lower():
            raise ValueError('Release SHA-256 mismatch')
    releases = root / 'releases'
    releases.mkdir(parents=True, exist_ok=True)
    # Validate the version with the same rule as executable().
    if not version or any(c not in '0123456789abcdefghijklmnopqrstuvwxyz.-' for c in version):
        raise ValueError('Invalid version')
    destination = releases / version
    if destination.exists():
        raise ValueError('That version is already staged; installed versions are immutable')
    work = Path(tempfile.mkdtemp(prefix='.staging-', dir=releases))
    try:
        unpack(archive, work)
        binary_name = 'Api.exe' if os.name == 'nt' else 'Api'
        matches = list(work.rglob(binary_name))
        if len(matches) != 1:
            raise ValueError('Expected one native server executable')
        verify_playback(matches[0], ffprobe, version)
        atomic_json(work / 'verified.json', {'sha256': expected, 'version': version})
        work.rename(destination)
    finally:
        if work.exists():
            shutil.rmtree(work)


def data_fingerprint(data):
    digest = hashlib.sha256()
    files = [data / name for name in ('media.db', 'launcher.json') if (data / name).exists()]
    wal = data / 'media.db-wal'
    # SQLite may create an empty WAL header during read-only maintenance.
    if wal.exists() and wal.stat().st_size > 32:
        files.append(wal)
    files += sorted((data / 'keys').glob('*.xml'))
    for file in files:
        digest.update(str(file.relative_to(data)).encode())
        with file.open('rb') as stream:
            digest.update(hashlib.file_digest(stream, 'sha256').digest())
    return digest.hexdigest()


def activate(root, version, data):
    binary = executable(root, version)
    if not (root / 'releases' / version / 'verified.json').exists():
        raise ValueError('Stage and validate this build first')
    current = read_state(root)
    if current and Path(current['data']).resolve() != data.resolve():
        raise ValueError('Use the installation’s current data directory')
    if root==data or root.is_relative_to(data):
        raise ValueError('Keep the installation root outside the data directory')
    if current and current['version']==version:raise ValueError('This version is already active')
    previous = current['version'] if current else None
    with lease(data):before=data_fingerprint(data)
    backup = None
    if (data / 'media.db').exists():
        backups = root / 'backups'
        backups.mkdir(parents=True, exist_ok=True)
        backup = backups / (uuid.uuid4().hex + '.zip')
        old = executable(root, previous) if previous else binary
        command(old, data, '--backup', backup)
        with lease(data):
            if data_fingerprint(data)!=before:raise RuntimeError('Data changed while preparing the checkpoint; retry with the server stopped')
        # Migration is exercised on a restored copy, never the production database.
        with tempfile.TemporaryDirectory(prefix='media-migration-test-') as temporary:
            clone = Path(temporary) / 'data'
            command(binary, clone, '--restore', backup)
            with running(binary, clone):
                pass
    with lease(data):
        if data_fingerprint(data)!=before:raise RuntimeError('Data changed during migration validation; pointer unchanged')
        atomic_json(root / 'current.json', {'version': version, 'previous': previous, 'backup': str(backup) if backup else None,
                                            'data': str(data.resolve())})


def rollback(root):
    state = read_state(root)
    if not state or not state.get('previous') or not state.get('backup'):
        raise ValueError('No rollback checkpoint is available')
    previous = executable(root, state['previous'])
    old_data = Path(state['data'])
    restored = old_data.with_name(old_data.name + '-rollback-' + uuid.uuid4().hex)
    with lease(old_data):
        command(previous, restored, '--restore', Path(state['backup']))
        with running(previous, restored):
            pass
        atomic_json(root / 'current.json', {'version': state['previous'], 'previous': None, 'backup': None, 'data': str(restored)})
    # The old/failed data directory is deliberately retained.


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    sub = parser.add_subparsers(dest='action', required=True)
    staging = sub.add_parser('stage')
    staging.add_argument('archive', type=Path)
    staging.add_argument('--sha256', required=True)
    staging.add_argument('--version', required=True)
    staging.add_argument('--ffprobe', default='ffprobe')
    switching = sub.add_parser('activate')
    switching.add_argument('version')
    switching.add_argument('--data', type=Path, required=True)
    sub.add_parser('rollback')
    sub.add_parser('run')
    args = parser.parse_args()
    root = args.root.resolve()
    root.mkdir(mode=0o700, parents=True, exist_ok=True)
    with lease(root):
        if args.action == 'stage':
            stage(root, args.archive.resolve(), args.sha256, args.version, args.ffprobe)
        elif args.action == 'activate':
            activate(root, args.version, args.data.resolve())
        elif args.action == 'rollback':
            rollback(root)
        else:
            state = read_state(root)
            if not state:
                raise ValueError('Activate a staged version first')
            binary = executable(root, state['version'])
            subprocess.run([str(binary)], cwd=binary.parent, env={**os.environ, 'MediaServer__DataDirectory': state['data']}, check=True)
    print('Update operation completed. Original media and previous versions were retained.')


if __name__ == '__main__':
    main()
