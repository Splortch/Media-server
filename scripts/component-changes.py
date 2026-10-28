#!/usr/bin/env python3
"""Keep required checks present, but skip expensive steps for unrelated changes."""
import json
import os
from pathlib import Path
import subprocess
import sys

COMMON = (
    'Directory.Build.props', 'global.json', 'dotnet-tools.json',
    '.editorconfig', '.gitattributes', 'scripts/component-changes.py',
)
PREFIXES = {
    'backend': ('backend/', 'scripts/audit.py', 'scripts/test-credential-recovery.py', 'scripts/update-server.py', 'scripts/test-native-update.py', '.github/workflows/backend-server.yml'),
    'web': ('frontend/', 'shared/api/', '.github/workflows/web-client.yml'),
    'windows': (
        'clients/windows/', 'shared/api/', 'backend/', 'frontend/',
        'scripts/test-native-client.py', 'scripts/setup-ffmpeg.ps1',
        'scripts/check-glib-patch.py', 'tests/security/',
        '.github/workflows/windows-client.yml',
    ),
}


def component_changed(kind, event, head, run=subprocess.run):
    base = event.get('pull_request', {}).get('base', {}).get('sha') or event.get('before')
    head = event.get('pull_request', {}).get('head', {}).get('sha') or head
    if not base or set(base) == {'0'}:
        return True
    # Git paths are UTF-8 data too. Never depend on the Windows ANSI code page.
    result = run(
        ['git', 'diff', '--name-only', '-z', base, head],
        capture_output=True, text=True, encoding='utf-8', errors='replace',
    )
    if result.returncode != 0:
        return True  # Missing history must run checks, never silently skip them.
    return any(
        path.startswith(PREFIXES[kind]) or path in COMMON
        for path in result.stdout.split('\0')
    )


def main():
    kind = sys.argv[1]
    # GitHub writes UTF-8 JSON even when a Windows runner defaults to cp1252.
    event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text(encoding='utf-8'))
    changed = component_changed(kind, event, os.environ['GITHUB_SHA'])
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        output.write('changed=' + str(changed).lower() + '\n')
    print(kind + ': ' + ('validate changed dependencies' if changed else 'no affected component changes'))


if __name__ == '__main__':
    main()

