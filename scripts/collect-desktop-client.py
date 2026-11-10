#!/usr/bin/env python3
"""Collect uniquely identified native installers under accurate release filenames."""
import os
from pathlib import Path
import re
import shutil

def collect(root, version, rid):
    if not re.fullmatch(r'0\.1\.0-alpha\.[1-9][0-9]{0,4}', version) or rid not in ('linux-x64', 'osx-x64', 'osx-arm64'):
        raise ValueError('Invalid desktop package identity')
    folder = root / 'clients/windows/src-tauri/target/release/bundle'
    extension = 'deb' if rid == 'linux-x64' else 'dmg'
    if extension == 'dmg':
        # A failed hdiutil ejection can leave rw.*.dmg beside the successful
        # retry's installer. Select Tauri's final version/architecture identity,
        # never an arbitrary image or an intermediate left by another attempt.
        arch = 'x64' if rid == 'osx-x64' else 'aarch64'
        source = folder / 'dmg' / f'Media Server Client_{version}_{arch}.dmg'
    else:
        sources = list(folder.rglob('*.deb'))
        if len(sources) != 1:
            raise ValueError('Expected exactly one desktop DEB installer')
        source = sources[0]
    if not source.is_file() or source.is_symlink():
        raise ValueError('Expected desktop installer is missing or is a symbolic link')
    output = root / 'artifacts/desktop'
    output.mkdir(parents=True, exist_ok=True)
    target = output / f'MediaServer-Desktop-Client-v{version}-{rid}.{extension}'
    shutil.copyfile(source, target)
    return target


if __name__ == '__main__':
    collect(Path(__file__).resolve().parents[1], os.environ['CLIENT_VERSION'], os.environ['RID'])
