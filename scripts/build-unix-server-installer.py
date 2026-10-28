#!/usr/bin/env python3
"""Build real .deb/.pkg installers from the already smoke-tested self-contained payload."""
import importlib.util
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def build(root=ROOT, run=subprocess.run):
    version = os.environ['SERVER_VERSION']
    rid = os.environ['RID']
    match = re.fullmatch(r'0\.2\.0-alpha\.([1-9][0-9]{0,4})', version)
    if not match or rid not in ('linux-x64', 'osx-x64', 'osx-arm64'):
        raise ValueError('Invalid installer version or runtime')
    source = root / 'artifacts/staging' / f'MediaServer-v{version}-{rid}'
    if not (source / 'Api').is_file() or not (source / 'wwwroot/index.html').is_file():
        raise RuntimeError('Smoke-tested server payload and web UI are required')
    output = root / 'artifacts/packages'
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='media-server-package-') as temp:
        payload = Path(temp) / 'payload'
        if rid == 'linux-x64':
            app = payload / 'opt/media-server'
            shutil.copytree(source, app)
            for name in ('Api', 'start-media-server.sh'):
                (app / name).chmod(0o755)
            control = payload / 'DEBIAN/control'
            control.parent.mkdir()
            control.write_text(f'''Package: media-server
Version: 0.2.0~alpha.{match[1]}
Architecture: amd64
Maintainer: Splortch <maintainers@users.noreply.github.com>
Section: video
Priority: optional
Depends: ffmpeg, libc6 (>= 2.35), libgcc-s1, libstdc++6, zlib1g, ca-certificates, libicu74 | libicu76 | libicu72, libssl3t64 | libssl3
Description: Self-hosted Media Server with web interface
 Runs as the signed-in user, listening on localhost by default.
''', encoding='utf-8')
            desktop = payload / 'usr/share/applications/media-server.desktop'
            desktop.parent.mkdir(parents=True)
            desktop.write_text('''[Desktop Entry]
Type=Application
Name=Media Server
Comment=Start your personal media server
Exec=/opt/media-server/start-media-server.sh
Terminal=true
Categories=AudioVideo;Video;
''', encoding='utf-8')
            destination = output / f'MediaServer-v{version}-{rid}.deb'
            run(['dpkg-deb', '--root-owner-group', '--build', str(payload), str(destination)], check=True)
        else:
            app = payload / 'Applications/MediaServer'
            shutil.copytree(source, app)
            for name in ('Api', 'Start-MediaServer.command'):
                (app / name).chmod(0o755)
            destination = output / f'MediaServer-v{version}-{rid}.pkg'
            # No root postinstall script or automatic service. The user launches the
            # terminal setup after installation; all mutable data stays in their home.
            run(['pkgbuild', '--root', str(payload), '--identifier', 'org.splortch.mediaserver.server',
                 '--version', f'0.2.{match[1]}', '--install-location', '/', '--ownership', 'recommended',
                 str(destination)], check=True)
    return destination


if __name__ == '__main__':
    print(build())
