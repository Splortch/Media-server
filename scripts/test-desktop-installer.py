#!/usr/bin/env python3
"""Install/mount the real bundle and check native startup (not a codec/device test)."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

root = Path(__file__).resolve().parents[1]
mac = sys.platform == 'darwin'
package, = (root / 'artifacts/desktop').glob('*.dmg' if mac else '*.deb')
with tempfile.TemporaryDirectory(prefix='media-desktop-installed-') as temp:
    directory = Path(temp)
    if mac:
        mount = directory / 'mounted'
        subprocess.run(['hdiutil', 'attach', '-nobrowse', '-readonly', '-mountpoint', str(mount), str(package)], check=True)
        try:
            app, = mount.glob('*.app')
            installed = directory / app.name
            subprocess.run(['ditto', str(app), str(installed)], check=True)
        finally:
            subprocess.run(['hdiutil', 'detach', str(mount)], check=True)
        executable, = (installed / 'Contents/MacOS').iterdir()
        command = [str(executable)]
    else:
        package_name = subprocess.check_output(['dpkg-deb', '-f', str(package), 'Package'], text=True).strip()
        subprocess.run(['sudo', 'dpkg', '-i', str(package)], check=True)
        files = subprocess.check_output(['dpkg-deb', '-c', str(package)], text=True)
        if '/usr/bin/media-server-windows-client' not in files:
            raise RuntimeError('Installed native executable missing')
        command = ['xvfb-run', '-a', 'dbus-run-session', '--', '/usr/bin/media-server-windows-client']
    try:
        with (directory / 'startup.log').open('w') as log:
            process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                time.sleep(8)
                if process.poll() is not None:
                    raise RuntimeError('Installed application exited: ' + (directory / 'startup.log').read_text())
            finally:
                import signal
                os.killpg(process.pid, signal.SIGTERM) if process.poll() is None else None
                process.wait(timeout=15)
    finally:
        if not mac:
            subprocess.run(['sudo', 'dpkg', '--remove', package_name], check=True)
    print('Real installer contains native application; installed/copied application remains running. Visual playback and OS dialogs require device testing.')
