#!/usr/bin/env python3
"""Publish RID graphs must retain the reviewed framework package versions."""
import json
from pathlib import Path
root = Path(__file__).resolve().parents[1]
for rid in ('win-x64', 'linux-x64'):
    for project in (root / 'backend/src').glob('*/*.csproj'):
        baseline = json.loads((project.parent / 'packages.lock.json').read_text(encoding='utf-8'))['dependencies']
        publish = root / 'backend/locks' / rid / project.stem / 'packages.lock.json'
        locked = json.loads(publish.read_text(encoding='utf-8'))['dependencies']
        for framework, packages in baseline.items():
            if '/' in framework:
                continue
            for name, package in packages.items():
                candidate = locked[framework][name]
                if candidate.get('resolved') != package.get('resolved') or candidate.get('contentHash') != package.get('contentHash'):
                    raise RuntimeError(f'{rid}/{project.stem}: reviewed package {name} drifted')
        if not any(key.endswith('/' + rid) for key in locked):
            raise RuntimeError(f'{rid}/{project.stem}: missing runtime graph')
print('Both platform publish graphs retain the reviewed dependency versions and hashes.')
