#!/usr/bin/env python3
"""Package real Android build outputs; do not rename unsigned files as installable releases."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import zipfile
root = Path(__file__).resolve().parents[1]
number = os.environ['ANDROID_BUILD_NUMBER']; sha = os.environ['SOURCE_SHA']
if not number.isdigit() or not 1 <= int(number) <= 65535 or not re.fullmatch('[0-9a-f]{40}', sha): raise SystemExit('Invalid Android build identity')
version = f'0.1.0-alpha.{number}'
source = root / 'clients/android/app/build/outputs'
dest = root / 'artifacts/android'; dest.mkdir(parents=True, exist_ok=True)
files = [('apk/debug/app-debug.apk', 'debug.apk', 'AndroidManifest.xml'), ('apk/release/app-release-unsigned.apk', 'release-unsigned.apk', 'AndroidManifest.xml'), ('bundle/release/app-release.aab', 'release-unsigned.aab', 'base/manifest/AndroidManifest.xml')]
for path, suffix, manifest in files:
    with zipfile.ZipFile(source / path) as archive:
        if manifest not in archive.namelist() or archive.getinfo(manifest).file_size == 0: raise SystemExit('Missing real Android manifest')
    shutil.copyfile(source / path, dest / f'MediaServer-Android-v{version}-{suffix}')
(dest / 'release.json').write_text(json.dumps({'platform': 'client-android', 'version': version, 'commit': sha, 'apiVersion': 1, 'releaseSigned': False, 'debugSigning': 'ephemeral-test-key'}, indent=2) + '\n')
(dest / 'README.txt').write_text('Media Server Android phone alpha\n\nInstall the debug APK for testing. It uses an ephemeral development signing key; uninstall a previous build if Android rejects an upgrade. The release APK is UNSIGNED and cannot be installed until signed. The AAB is an unsigned distribution bundle, not an installable APK. No production signing key is included. Android 8.0+; a separately running Media Server with API v1 and prefix search is required. Remote connections require HTTPS. See docs/clients/ANDROID.md at the source commit in release.json.\n')
expected = [dest / f'MediaServer-Android-v{version}-{suffix}' for _, suffix, _ in files] + [dest / 'release.json', dest / 'README.txt']
(dest / 'SHA256SUMS').write_text(''.join(f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n' for p in expected))
print(f'Packaged Android {version} from {sha}')
