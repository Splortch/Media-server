#!/usr/bin/env python3
"""Collect uniquely identified native installers under accurate release filenames."""
import os
from pathlib import Path
import re
import shutil

root = Path(__file__).resolve().parents[1]
version = os.environ['CLIENT_VERSION']
rid = os.environ['RID']
if not re.fullmatch(r'0\.1\.0-alpha\.[1-9][0-9]{0,4}', version) or rid not in ('linux-x64', 'osx-x64', 'osx-arm64'):
    raise ValueError('Invalid desktop package identity')
folder = root / 'clients/windows/src-tauri/target/release/bundle'
extension = 'deb' if rid == 'linux-x64' else 'dmg'
source, = folder.rglob('*.' + extension)
output = root / 'artifacts/desktop'
output.mkdir(parents=True, exist_ok=True)
shutil.copyfile(source, output / f'MediaServer-Desktop-Client-v{version}-{rid}.{extension}')
