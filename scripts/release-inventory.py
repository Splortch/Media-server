#!/usr/bin/env python3
"""Generate deterministic SPDX 2.3 metadata and auditable license evidence.

Lockfile/package-cache metadata is evidence, not a legal compliance verdict.
No dependency package code is imported or executed.
"""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import shutil
import xml.etree.ElementTree as ET
from urllib.parse import quote


def identifier(value):
    return 'SPDXRef-' + hashlib.sha256(value.encode()).hexdigest()[:24]


def checksum(integrity):
    if not integrity:
        return []
    algorithm, value = integrity.split('-', 1)
    names = {'sha256': 'SHA256', 'sha512': 'SHA512', 'sha1': 'SHA1'}
    if algorithm not in names:
        raise ValueError('Unsupported locked checksum algorithm')
    digest = base64.b64decode(value, validate=True)
    if len(digest) != {'sha256': 32, 'sha512': 64, 'sha1': 20}[algorithm]:
        raise ValueError('Invalid locked checksum length')
    return [{'algorithm': names[algorithm], 'checksumValue': digest.hex()}]


def component(name, version, ecosystem, license_text, integrity=None):
    if not name or not version or any(char in name + version for char in '\r\n\x00'):
        raise ValueError('Invalid dependency identity')
    purl = f'pkg:{ecosystem}/{quote(name, safe="/")}@{quote(version, safe="")}'
    return {
        'SPDXID': identifier(purl), 'name': name, 'versionInfo': version,
        'downloadLocation': 'NOASSERTION', 'filesAnalyzed': False,
        'licenseConcluded': 'NOASSERTION', 'licenseDeclared': 'NOASSERTION',
        'copyrightText': 'NOASSERTION',
        'externalRefs': [{'referenceCategory': 'PACKAGE-MANAGER', 'referenceType': 'purl', 'referenceLocator': purl}],
        **({'checksums': checksum(integrity)} if integrity else {}),
        'sourceInfo': 'Locked dependency metadata; archive checksum is not a checksum of the installed directory.',
        'comment': 'Declared license metadata (unreviewed): ' + (license_text or 'MISSING'),
    }


def bounded_file(path, root):
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(root.resolve()) or not resolved.is_file() or resolved.stat().st_size > 2_000_000:
        raise ValueError('Unsafe or oversized license evidence')
    return resolved


def inventory(assets, lockfile, node_modules, target, commit, runtime):
    if not re.fullmatch(r'[a-f0-9]{40}', commit) or runtime not in ('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64'):
        raise ValueError('Invalid source or runtime identity')
    if target.exists() and any(target.iterdir()):
        raise ValueError('Inventory output must be empty; refuse stale evidence')
    graph = json.loads(assets.read_text(encoding='utf-8'))
    npm = json.loads(lockfile.read_text(encoding='utf-8'))
    folders = [Path(value) for value in graph['packageFolders']]
    packages, evidence = [], []
    evidence_dir = target / 'license-evidence'
    evidence_dir.mkdir(parents=True, exist_ok=True)
    for key, item in sorted(graph['libraries'].items()):
        if item['type'] != 'package':
            continue
        name, version = key.rsplit('/', 1)
        relative = Path(item['path'])
        if relative.is_absolute() or '..' in relative.parts:
            raise ValueError('Unsafe package cache path')
        roots = [root / relative for root in folders if (root / relative).is_dir()]
        if len(roots) != 1:
            raise ValueError('Expected one restored NuGet package for ' + key)
        root = roots[0]
        nuspecs = list(root.glob('*.nuspec'))
        if len(nuspecs) != 1:
            raise ValueError('Missing or ambiguous NuGet license metadata: ' + key)
        document = ET.fromstring(bounded_file(nuspecs[0], root).read_bytes())
        # Nuspec documents vary by namespace; identify only direct metadata children.
        metadata = next((element for element in document if element.tag.rsplit('}', 1)[-1] == 'metadata'), None)
        if metadata is None:
            raise ValueError('NuGet metadata missing')
        declared, notices = '', []
        for element in metadata:
            tag = element.tag.rsplit('}', 1)[-1]
            if tag == 'license':
                declared = (element.text or '').strip()
                if element.attrib.get('type') == 'file':
                    notice = bounded_file(root / declared, root)
                    notices.append(notice)
            elif tag == 'licenseUrl' and not declared:
                declared = (element.text or '').strip()
        for candidate in root.iterdir():
            if candidate.is_file() and re.match(r'(?i)^(license|notice|thirdpartynotices)([._-]|$)', candidate.name):
                notices.append(bounded_file(candidate, root))
        entry = component(name, version, 'nuget', declared, 'sha512-' + item['sha512'] if item.get('sha512') else None)
        packages.append(entry)
        copied = []
        for index, notice in enumerate(sorted(set(notices))):
            destination = evidence_dir / (entry['SPDXID'] + '-' + str(index) + '.txt')
            shutil.copyfile(notice, destination)
            copied.append(destination.relative_to(target).as_posix())
        evidence.append({'ecosystem': 'nuget', 'name': name, 'version': version, 'declaredLicense': declared or None, 'scope': 'restore graph (includes build dependencies)', 'notices': copied})
    for path, item in sorted(npm['packages'].items()):
        if not path or item.get('dev') or item.get('link'):
            continue
        if not path.startswith('node_modules/') or '..' in Path(path).parts:
            raise ValueError('Unexpected npm package path')
        name = path.rsplit('node_modules/', 1)[-1]
        root = node_modules.parent / path
        manifest = json.loads(bounded_file(root / 'package.json', node_modules).read_text(encoding='utf-8'))
        if manifest['version'] != item['version'] or manifest['name'] != name:
            raise ValueError('Installed npm package does not match lockfile')
        declared = manifest.get('license')
        if not isinstance(declared, str):
            declared = json.dumps(declared, sort_keys=True) if declared else ''
        entry = component(name, item['version'], 'npm', declared, item.get('integrity'))
        packages.append(entry)
        copied = []
        for candidate in sorted(root.iterdir()):
            if candidate.is_file() and re.match(r'(?i)^(license|notice|copying)([._-]|$)', candidate.name):
                notice = bounded_file(candidate, node_modules)
                destination = evidence_dir / (entry['SPDXID'] + '-' + str(len(copied)) + '.txt')
                shutil.copyfile(notice, destination)
                copied.append(destination.relative_to(target).as_posix())
        evidence.append({'ecosystem': 'npm', 'name': name, 'version': item['version'], 'declaredLicense': declared or None, 'scope': 'production browser dependency', 'notices': copied})
    packages.sort(key=lambda value: (value['name'], value['versionInfo'], value['SPDXID']))
    if len({value['SPDXID'] for value in packages}) != len(packages):
        # Nested npm copies of the same exact package describe the same component.
        packages = list({value['SPDXID']: value for value in packages}.values())
    document = {
        'spdxVersion': 'SPDX-2.3', 'dataLicense': 'CC0-1.0', 'SPDXID': 'SPDXRef-DOCUMENT',
        'name': 'MediaServer-' + runtime,
        'documentNamespace': f'https://spdx.org/spdxdocs/media-server-{commit}-{runtime}',
        'creationInfo': {'creators': ['Tool: MediaServer-release-inventory-1'], 'created': '1970-01-01T00:00:00Z',
                         'comment': 'Deterministic metadata inventory; epoch timestamp is intentional, source commit identifies build. Not a license review or complete OS/runtime inventory.'},
        'packages': packages,
        'relationships': [{'spdxElementId': 'SPDXRef-DOCUMENT', 'relationshipType': 'DESCRIBES', 'relatedSpdxElement': value['SPDXID']} for value in packages],
    }
    target.mkdir(parents=True, exist_ok=True)
    (target / 'SBOM.spdx.json').write_text(json.dumps(document, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    report = {'sourceCommit': commit, 'runtime': runtime, 'components': evidence,
              'limitations': ['Metadata declarations are not legal conclusions.', 'NuGet inventory includes build/design dependencies; presence does not prove redistribution.', 'Microsoft runtime/OS packages and native client dependencies require separate inventories.', 'Native servers do not bundle FFmpeg; downloaded tools have separate licenses. Container FFmpeg/source evidence is distributed separately.']}
    (target / 'THIRD-PARTY-INVENTORY.json').write_text(json.dumps(report, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    return document


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--assets', type=Path, required=True)
    parser.add_argument('--lockfile', type=Path, required=True)
    parser.add_argument('--node-modules', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--runtime', required=True)
    args = parser.parse_args()
    result = inventory(args.assets, args.lockfile, args.node_modules, args.output, args.commit, args.runtime)
    print(f'Inventoried {len(result["packages"])} locked components; license conclusions require review.')
