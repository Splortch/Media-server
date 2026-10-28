#!/usr/bin/env python3
"""Compile the real staged server into a per-user NSIS setup executable."""
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def uninstall_manifest(payload):
    # Values come from our build tree, never from an installed/user directory.
    def quote(path):
        value = str(path).replace('\\', '/').replace('/', '\\')
        if any(c in value for c in ('"', '$', '\n', '\r')):
            raise ValueError('Unsafe packaged filename')
        return value
    files = sorted(p for p in payload.rglob('*') if p.is_file())
    if any(p.is_symlink() for p in payload.rglob('*')):
        raise ValueError('Installer payload cannot contain symbolic links')
    lines = [f'Delete "$INSTDIR\\app\\{quote(p.relative_to(payload))}"' for p in files]
    directories = sorted((p for p in payload.rglob('*') if p.is_dir()), key=lambda p: len(p.parts), reverse=True)
    lines.extend(f'RMDir "$INSTDIR\\app\\{quote(p.relative_to(payload))}"' for p in directories)
    return '\n'.join(lines + ['RMDir "$INSTDIR\\app"', ''])


def build():
    version = os.environ['SERVER_VERSION']
    if not re.fullmatch(r'0\.2\.0-alpha\.[1-9][0-9]{0,4}', version) or int(version.rsplit('.', 1)[1]) > 65535:
        raise ValueError('Invalid server version')
    payload = ROOT / 'artifacts/staging' / f'MediaServer-v{version}-win-x64'
    for name in ('Api.exe', 'coreclr.dll', 'System.Private.CoreLib.dll', 'wwwroot/index.html', 'MediaServer.Tray.ps1', 'LICENSE'):
        if not (payload / name).is_file():
            raise RuntimeError(f'Missing real runnable payload: {name}')
    compiler = os.environ.get('MAKENSIS') or shutil.which('makensis')
    if not compiler and os.name == 'nt':
        candidate = Path(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)')) / 'NSIS/makensis.exe'
        if candidate.is_file():
            compiler = str(candidate)
    if not compiler:
        raise RuntimeError('NSIS 3 is required; install it or set MAKENSIS to its executable.')
    manifest = ROOT / 'artifacts/server-uninstall.nsh'
    manifest.write_text(uninstall_manifest(payload), encoding='utf-8')
    output = ROOT / 'artifacts/packages' / f'MediaServer-v{version}-win-x64-setup.exe'
    switch = '/' if os.name == 'nt' else '-'
    subprocess.run([compiler, switch + 'V3', switch + 'DVERSION=' + version,
                    switch + 'DBUILD=' + version.rsplit('.', 1)[1],
                    switch + 'DPAYLOAD=' + str(payload), switch + 'DOUTPUT=' + str(output),
                    switch + 'DUNINSTALL_MANIFEST=' + str(manifest),
                    str(ROOT / 'release-resources/installer/server.nsi')], check=True)
    if not output.is_file() or output.stat().st_size < 100_000:
        raise RuntimeError('NSIS did not produce a populated setup executable')
    print(f'Built {output.name}')


if __name__ == '__main__':
    build()
