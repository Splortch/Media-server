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


def runtime_inventory(published, folders, target, runtime):
    """Require evidence for the runtime packs actually named by the published app."""
    config = json.loads(bounded_file(published / 'Api.runtimeconfig.json', published).read_text(encoding='utf-8'))
    deps = json.loads(bounded_file(published / 'Api.deps.json', published).read_text(encoding='utf-8'))
    if not deps['runtimeTarget']['name'].endswith('/' + runtime):
        raise ValueError('Published runtime does not match inventory target')
    frameworks = config['runtimeOptions'].get('includedFrameworks', [])
    expected = {'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'}
    if len(frameworks) != 2 or {item['name'] for item in frameworks} != expected:
        raise ValueError('Expected both self-contained server frameworks')
    packages, evidence = [], []
    for framework in sorted(frameworks, key=lambda item: item['name']):
        version = framework['version']
        if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?', version):
            raise ValueError('Invalid published framework version')
        name = framework['name'] + '.Runtime.' + runtime
        key = 'runtimepack.' + name + '/' + version
        if deps['libraries'].get(key, {}).get('type') != 'runtimepack':
            raise ValueError('Published framework has no matching runtime pack')
        roots = [folder / name.lower() / version.lower() for folder in folders
                 if (folder / name.lower() / version.lower()).is_dir()]
        if len(roots) != 1:
            raise ValueError('Expected one restored runtime pack for ' + name)
        root = roots[0]
        nuspecs = list(root.glob('*.nuspec'))
        if len(nuspecs) != 1:
            raise ValueError('Missing or ambiguous runtime pack metadata')
        document = ET.fromstring(bounded_file(nuspecs[0], root).read_bytes())
        metadata = next((element for element in document if element.tag.rsplit('}', 1)[-1] == 'metadata'), None)
        if metadata is None:
            raise ValueError('Runtime pack metadata missing')
        values = {element.tag.rsplit('}', 1)[-1]: (element.text or '').strip() for element in metadata}
        if values.get('id', '').lower() != name.lower() or values.get('version') != version:
            raise ValueError('Restored runtime pack identity does not match published app')
        # Runtime packs carry Microsoft's terms and upstream third-party notices.
        # Do not accept a package identity alone as license evidence.
        notices = sorted(candidate for candidate in root.rglob('*')
                         if candidate.is_file() and re.fullmatch(
                             r'(?i)(license|third[-_]?party[-_]?notices)(\.[a-z]+)?', candidate.name))
        if not any(re.match(r'(?i)^license', p.name) for p in notices) or not any(
                re.match(r'(?i)^third', p.name) for p in notices):
            raise ValueError('Runtime pack license and third-party notices are required')
        entry = component(name, version, 'nuget', values.get('license'))
        entry['sourceInfo'] = 'Self-contained runtime pack identified by published runtimeconfig/deps and restored nuspec; no lockfile archive checksum is asserted.'
        packages.append(entry)
        copied = []
        for index, notice in enumerate(notices):
            source = bounded_file(notice, root)
            destination = target / 'license-evidence' / (entry['SPDXID'] + '-' + str(index) + '.txt')
            shutil.copyfile(source, destination)
            copied.append(destination.relative_to(target).as_posix())
        evidence.append({'ecosystem': 'nuget', 'name': name, 'version': version,
                         'declaredLicense': values.get('license') or None,
                         'scope': 'published self-contained runtime pack', 'notices': copied})
    actual = {key for key, value in deps['libraries'].items() if value.get('type') == 'runtimepack'}
    wanted = {'runtimepack.' + item['name'] + '/' + item['version'] for item in evidence}
    if actual != wanted:
        raise ValueError('Unexpected published runtime packs require inventory support')
    return packages, evidence


def inventory(assets, lockfile, node_modules, target, commit, runtime, published=None):
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
    if published is not None:
        runtime_packages, runtime_evidence = runtime_inventory(published, folders, target, runtime)
        packages.extend(runtime_packages)
        evidence.extend(runtime_evidence)
    packages.sort(key=lambda value: (value['name'], value['versionInfo'], value['SPDXID']))
    if len({value['SPDXID'] for value in packages}) != len(packages):
        # Nested npm copies of the same exact package describe the same component.
        packages = list({value['SPDXID']: value for value in packages}.values())
    document = {
        'spdxVersion': 'SPDX-2.3', 'dataLicense': 'CC0-1.0', 'SPDXID': 'SPDXRef-DOCUMENT',
        'name': 'MediaServer-' + runtime,
        'documentNamespace': f'https://spdx.org/spdxdocs/media-server-{commit}-{runtime}',
        'creationInfo': {'creators': ['Tool: MediaServer-release-inventory-1'], 'created': '1970-01-01T00:00:00Z',
                         'comment': 'Deterministic metadata inventory; epoch timestamp is intentional, source commit identifies build. Not a license review or complete OS/native SDK inventory.'},
        'packages': packages,
        'relationships': [{'spdxElementId': 'SPDXRef-DOCUMENT', 'relationshipType': 'DESCRIBES', 'relatedSpdxElement': value['SPDXID']} for value in packages],
    }
    target.mkdir(parents=True, exist_ok=True)
    (target / 'SBOM.spdx.json').write_text(json.dumps(document, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    report = {'sourceCommit': commit, 'runtime': runtime, 'components': evidence,
              'runtimePackEvidence': published is not None,
              'limitations': ['Metadata declarations are not legal conclusions.', 'NuGet inventory includes build/design dependencies; presence does not prove redistribution.', 'Runtime pack evidence is identified by the published app, not a locked archive hash.' if published is not None else 'Microsoft runtime packs were not inventoried.', 'OS packages and native client dependencies require separate inventories.', 'Native servers do not bundle FFmpeg; downloaded tools have separate licenses. Container FFmpeg/source evidence is distributed separately.']}
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
    parser.add_argument('--published-dir', type=Path, required=True)
    args = parser.parse_args()
    result = inventory(args.assets, args.lockfile, args.node_modules, args.output, args.commit, args.runtime, args.published_dir)
    print(f'Inventoried {len(result["packages"])} locked components; license conclusions require review.')
