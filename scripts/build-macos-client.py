#!/usr/bin/env python3
"""Retry only macOS disk-image contention; preserve real build/package failures."""
import subprocess
import sys
import time


def build(command, run=subprocess.run, wait=time.sleep):
    for attempt in range(3):
        result = run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        print(result.stdout, end='', flush=True)
        if result.returncode == 0:
            return 0
        if 'hdiutil: create failed - Resource busy' not in result.stdout or attempt == 2:
            return result.returncode
        delay = 5 * (attempt + 1)
        print(f'Disk-image service is busy; retrying packaging in {delay}s (attempt {attempt + 2}/3).', flush=True)
        wait(delay)
    raise AssertionError('Unreachable')


if __name__ == '__main__':
    if sys.platform != 'darwin':
        raise SystemExit('This packaging helper requires macOS.')
    if len(sys.argv) != 2 or sys.argv[1] != 'app,dmg':
        raise SystemExit('Usage: build-macos-client.py app,dmg (from clients/windows)')
    raise SystemExit(build(['npm', 'run', 'tauri', '--', 'build', '--bundles', sys.argv[1],
                           '--config', '{"bundle":{"icon":["generated-icons/icon.icns"]}}',
                           '--', '--locked']))
