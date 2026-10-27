#!/usr/bin/env python3
"""Package runnable builds; no source-only archives masquerading as installers."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / 'artifacts'


def version(name):
    value = os.environ[name]
    if not re.fullmatch(r'0\.[12]\.0-alpha\.[1-9][0-9]{0,4}', value):
        raise ValueError('Invalid alpha version')
    return value


def archive(folder, destination, linux=False):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if linux:
        with tarfile.open(destination, 'w:gz') as output:
            output.add(folder, arcname=folder.name)
    else:
        with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED) as output:
            for path in sorted(folder.rglob('*')):
                if path.is_file():
                    output.write(path, Path(folder.name) / path.relative_to(folder))


def browser_licenses(folder):
    target = folder / 'licenses'
    target.mkdir(exist_ok=True)
    for package in ('react', 'react-dom', 'scheduler'):
        shutil.copyfile(ROOT / 'frontend/node_modules' / package / 'LICENSE', target / f'{package}-LICENSE')


def package_server():
    ver = version('SERVER_VERSION')
    rid = os.environ['RID']
    if rid not in ('win-x64', 'linux-x64'):
        raise ValueError('Unsupported server runtime')
    linux = rid == 'linux-x64'
    executable = 'Api' if linux else 'Api.exe'
    source = ARTIFACTS / 'server'
    if not (source / executable).is_file():
        raise RuntimeError('Actual self-contained server executable is missing')
    folder = ARTIFACTS / 'staging' / f'MediaServer-v{ver}-{rid}'
    shutil.copytree(source, folder, dirs_exist_ok=True)
    shutil.copytree(ROOT / 'frontend/dist', folder / 'wwwroot', dirs_exist_ok=True)
    for path in ('LICENSE', 'NOTICE'):
        shutil.copyfile(ROOT / path, folder / path)
    shutil.copyfile(ROOT / 'docs/deployment/RELEASE-NOTES.md', folder / 'README.md')
    browser_licenses(folder)
    resources = ROOT / 'release-resources' / ('linux' if linux else 'windows')
    for path in resources.iterdir():
        shutil.copyfile(path, folder / path.name)
    if linux:
        (folder / executable).chmod(0o755)
        (folder / 'start-media-server.sh').chmod(0o755)
    extension = '.tar.gz' if linux else '.zip'
    archive(folder, ARTIFACTS / 'packages' / (folder.name + extension), linux)


def one(folder, pattern):
    matches = list(folder.rglob(pattern))
    if len(matches) != 1 or not matches[0].is_file():
        raise RuntimeError(f'Expected one {pattern} in {folder}; got {len(matches)}')
    return matches[0]


def assemble():
    server = version('SERVER_VERSION')
    client = version('CLIENT_VERSION')
    sha = os.environ['SOURCE_SHA']
    if not re.fullmatch('[0-9a-f]{40}', sha):
        raise ValueError('Invalid source commit')
    downloads = ARTIFACTS / 'downloads'
    output = ARTIFACTS / 'release'
    output.mkdir(parents=True, exist_ok=True)
    for rid, extension in [('win-x64', '.zip'), ('linux-x64', '.tar.gz')]:
        name = f'MediaServer-v{server}-{rid}{extension}'
        shutil.copyfile(downloads / f'release-server-{rid}' / name, output / name)
    native = downloads / 'release-windows-client'
    shutil.copyfile(one(native, '*-setup.exe'), output / f'MediaServer-Windows-Client-v{client}-win-x64-setup.exe')
    shutil.copyfile(one(native, '*.msi'), output / f'MediaServer-Windows-Client-v{client}-win-x64.msi')
    # The web UI is already bundled into the server and container. It is not a
    # standalone file:// application, so no misleading browser-only download.
    setup = ARTIFACTS / 'staging' / f'MediaServer-Docker-v{server}'
    setup.mkdir(parents=True, exist_ok=True)
    image = f'ghcr.io/{os.environ["GITHUB_REPOSITORY"].lower()}:{os.environ["RELEASE_TAG"]}'
    (setup / 'compose.yaml').write_text(f'''services:
  media-server:
    image: {image}
    ports: ["127.0.0.1:5050:5050"]
    volumes:
      - server-data:/data
      - ${{MEDIA_PATH:?Set MEDIA_PATH in .env to your media folder}}:/media:ro
    read_only: true
    tmpfs: [/tmp]
    cap_drop: [ALL]
    security_opt: ["no-new-privileges:true"]
    restart: unless-stopped
volumes:
  server-data:
''', encoding='utf-8')
    (setup / '.env.example').write_text('MEDIA_PATH=/absolute/path/to/media\n', encoding='utf-8')
    (setup / 'README.md').write_text('Copy .env.example to .env and set MEDIA_PATH. Run `docker compose up -d`, then `docker compose exec media-server cat /data/setup-token`. Open http://127.0.0.1:5050 and use /media when adding a library. The media mount is read-only. Private GHCR packages require docker login. Back up server-data before updates.\n', encoding='utf-8')
    archive(setup, output / (setup.name + '.zip'))
    for name in ('CONTAINER-PACKAGES.txt', 'FFMPEG-LICENSE.txt', 'Container-Corresponding-Source.tar.gz'):
        shutil.copyfile(downloads / 'release-container' / name, output / name)
    manifest = {'commit': sha, 'serverVersion': server, 'windowsClientVersion': client, 'image': image, 'channel': 'alpha', 'signed': False}
    (output / 'release.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    sums = []
    for path in sorted(output.iterdir()):
        if path.is_file() and path.name != 'SHA256SUMS':
            with path.open('rb') as stream:
                sums.append(f'{hashlib.file_digest(stream, "sha256").hexdigest()}  {path.name}')
    (output / 'SHA256SUMS').write_text('\n'.join(sums) + '\n', encoding='utf-8')
    if len(sums) != 9:
        raise RuntimeError(f'Expected 9 release assets before checksums; found {len(sums)}')


if __name__ == '__main__':
    {'server': package_server, 'assemble': assemble}[sys.argv[1]]()
