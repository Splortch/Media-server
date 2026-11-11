#!/usr/bin/env python3
"""Inventory the locked desktop Rust graph and retain available license evidence.

Cargo metadata is read without compiling or executing dependency build scripts.
This is a review inventory, not a determination of redistribution obligations.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import tomllib
from urllib.parse import quote

TARGETS = {'linux-x64': 'x86_64-unknown-linux-gnu', 'win-x64': 'x86_64-pc-windows-msvc',
           'osx-x64': 'x86_64-apple-darwin', 'osx-arm64': 'aarch64-apple-darwin'}
NOTICE = re.compile(r'(?i)^(licen[cs]e|notice|copying|copyright)([._-]|$)')


def bounded(path, root):
    path = path.resolve(strict=True)
    if not path.is_relative_to(root.resolve()) or not path.is_file() or path.stat().st_size > 2_000_000:
        raise ValueError('Unsafe or oversized desktop license evidence')
    return path


def selected_packages(metadata):
    packages = {package['id']: package for package in metadata['packages']}
    nodes = {node['id']: node for node in metadata['resolve']['nodes']}
    if len(packages) != len(metadata['packages']) or len(nodes) != len(metadata['resolve']['nodes']):
        raise ValueError('Ambiguous Cargo metadata identities')
    root = metadata['resolve']['root']
    if root not in packages or packages[root]['name'] != 'media-server-windows-client':
        raise ValueError('Expected the desktop application Cargo root')
    pending, visited = [root], set()
    while pending:
        identity = pending.pop()
        if identity in visited:
            continue
        if identity not in packages or identity not in nodes:
            raise ValueError('Incomplete Cargo dependency graph')
        visited.add(identity)
        for dependency in nodes[identity]['deps']:
            kinds = dependency['dep_kinds']
            if not kinds or any(kind['kind'] not in (None, 'build', 'dev') for kind in kinds):
                raise ValueError('Unknown Cargo dependency kind')
            if any(kind['kind'] != 'dev' for kind in kinds):
                pending.append(dependency['pkg'])
    return [packages[key] for key in sorted(visited)]


def generate(metadata, lock, repository, output, commit, runtime):
    if runtime not in TARGETS or not re.fullmatch(r'[a-f0-9]{40}', commit):
        raise ValueError('Invalid desktop build identity')
    if output.exists() and (not output.is_dir() or any(output.iterdir())):
        raise ValueError('Desktop inventory output must be empty; refusing stale evidence')
    locked = {(p['name'], p['version'], p.get('source')): p for p in lock['package']}
    if len(locked) != len(lock['package']):
        raise ValueError('Ambiguous Cargo lock identities')
    packages, components = [], []
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='desktop-inventory-', dir=output.parent) as temporary:
        staging = Path(temporary)
        evidence = staging / 'license-evidence'
        evidence.mkdir()
        for package in selected_packages(metadata):
            name, version, source = package['name'], package['version'], package['source']
            if not re.fullmatch(r'[A-Za-z0-9_-]+', name) or not re.fullmatch(r'[A-Za-z0-9.+-]+', version):
                raise ValueError('Invalid Cargo package identity')
            entry = locked.get((name, version, source))
            if entry is None:
                raise ValueError('Cargo metadata does not match the exact lockfile source/version')
            root = Path(package['manifest_path']).parent.resolve(strict=True)
            if source is None:
                if name not in ('media-server-windows-client', 'media-client-core') or not root.is_relative_to(repository.resolve() / 'clients/windows'):
                    raise ValueError('Unreviewed local desktop dependency')
                continue  # First-party code is covered by the bundled project LICENSE/NOTICE.
            boundary = root
            checksum = entry.get('checksum')
            if source == 'registry+https://github.com/rust-lang/crates.io-index':
                if not isinstance(checksum, str) or not re.fullmatch(r'[a-f0-9]{64}', checksum):
                    raise ValueError('Registry dependency needs a locked SHA256 archive checksum')
            elif source.startswith('git+') and re.search(r'#[a-f0-9]{40}$', source):
                boundary = next((parent for parent in (root, *root.parents) if (parent / '.git').exists()), None)
                if boundary is None:
                    raise ValueError('Git dependency requires a bounded checkout for workspace notices')
                if checksum is not None:
                    raise ValueError('Git dependencies do not have registry archive checksums')
            else:
                raise ValueError('Unsupported or unpinned Cargo source')
            notices = {candidate for candidate in root.rglob('*') if candidate.is_file() and NOTICE.match(candidate.name)}
            notices.update(candidate for candidate in boundary.iterdir() if candidate.is_file() and NOTICE.match(candidate.name))
            if package.get('license_file'):
                notices.add(root / package['license_file'])
            if len(notices) > 1000:
                raise ValueError('Too many desktop license evidence files')
            # Include the source in identity: a git fork is not the registry crate.
            identity = 'SPDXRef-' + hashlib.sha256(f'{name}@{version}:{source}'.encode()).hexdigest()[:24]
            copied = []
            for notice in sorted(notices, key=lambda p: p.as_posix()):
                notice = bounded(notice, boundary)
                destination = evidence / f'{identity}-{len(copied)}.txt'
                shutil.copyfile(notice, destination)
                copied.append(destination.relative_to(staging).as_posix())
            declared = package.get('license')
            if not declared and not copied:
                raise ValueError('Missing desktop dependency license metadata and evidence')
            purl = f'pkg:cargo/{name}@{quote(version, safe="")}'
            if source.startswith('git+'):
                purl += '?vcs_url=' + quote(source.removeprefix('git+'), safe='')
            packages.append({
                'SPDXID': identity, 'name': name, 'versionInfo': version,
                'downloadLocation': 'NOASSERTION', 'filesAnalyzed': False,
                'licenseConcluded': 'NOASSERTION', 'licenseDeclared': 'NOASSERTION',
                'copyrightText': 'NOASSERTION', 'sourceInfo': source,
                'comment': 'Unreviewed declared license: ' + (declared or 'MISSING'),
                'externalRefs': [{'referenceCategory': 'PACKAGE-MANAGER', 'referenceType': 'purl', 'referenceLocator': purl}],
                **({'checksums': [{'algorithm': 'SHA256', 'checksumValue': checksum}]} if checksum else {}),
            })
            components.append({'name': name, 'version': version, 'source': source,
                               'declaredLicense': declared, 'notices': copied,
                               'missingNoticeFiles': not bool(copied)})
        if not packages:
            raise ValueError('Expected third-party desktop dependencies')
        packages.sort(key=lambda p: p['SPDXID'])
        components.sort(key=lambda p: (p['name'], p['version'], p['source']))
        document = {
            'spdxVersion': 'SPDX-2.3', 'dataLicense': 'CC0-1.0', 'SPDXID': 'SPDXRef-DOCUMENT',
            'name': 'MediaServer-Desktop-' + runtime,
            'documentNamespace': f'https://spdx.org/spdxdocs/media-server-desktop-{commit}-{runtime}',
            'creationInfo': {'creators': ['Tool: MediaServer-desktop-inventory-1'], 'created': '1970-01-01T00:00:00Z'},
            'packages': packages,
            'relationships': [{'spdxElementId': 'SPDXRef-DOCUMENT', 'relationshipType': 'DESCRIBES', 'relatedSpdxElement': p['SPDXID']} for p in packages],
        }
        report = {'sourceCommit': commit, 'runtime': runtime, 'targetTriple': TARGETS[runtime],
                  'components': components,
                  'limitations': ['Declared licenses are unreviewed metadata, not legal conclusions.',
                                  'Target-filtered Cargo graph includes normal and build dependencies; inclusion does not prove code is redistributed.',
                                  'Cargo dev-only dependencies and first-party code are excluded.',
                                  'Archive checksums come from Cargo.lock, not from installed files. Git sources retain commit identities without invented archive checksums.',
                                  'Available license/notice files are copied; missing files are explicitly flagged for review.',
                                  'Browser notices are bundled separately; this inventory covers Rust/Tauri dependencies only.',
                                  'OS, WebView, native SDK, toolchain and Android dependencies require separate inventories.']}
        for name, value in [('SBOM.spdx.json', document), ('THIRD-PARTY-INVENTORY.json', report)]:
            (staging / name).write_text(json.dumps(value, indent=2, sort_keys=True) + '\n', encoding='utf-8')
        sums = [f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(staging).as_posix()}'
                for p in sorted(staging.rglob('*')) if p.is_file()]
        (staging / 'SHA256SUMS').write_text('\n'.join(sums) + '\n', encoding='utf-8')
        # Nothing is made available for packaging until the complete graph validates.
        if output.exists():
            output.rmdir()
        staging.rename(output)
    return document


def verify_installed(expected, installation):
    candidates = list(installation.glob('**/inventory/SBOM.spdx.json'))
    if len(candidates) != 1:
        raise ValueError('Expected exactly one installed desktop inventory')
    installed = candidates[0].parent
    for source in expected.rglob('*'):
        if source.is_file():
            target = bounded(installed / source.relative_to(expected), installation)
            if hashlib.sha256(target.read_bytes()).digest() != hashlib.sha256(source.read_bytes()).digest():
                raise ValueError('Installed desktop inventory differs from generated evidence')
    if not (expected / 'SHA256SUMS').is_file() or not (expected / 'THIRD-PARTY-INVENTORY.json').is_file():
        raise ValueError('Generated desktop evidence is incomplete')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--runtime', choices=TARGETS)
    parser.add_argument('--commit')
    parser.add_argument('--verify-installed', type=Path)
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[1]
    manifest = repository / 'clients/windows/src-tauri/Cargo.toml'
    output = manifest.parent / 'target/distribution-inventory'
    if args.verify_installed:
        verify_installed(output, args.verify_installed)
        print('Installed desktop inventory matches all generated evidence bytes.')
        return
    if not args.runtime or not args.commit:
        parser.error('--runtime and --commit are required when generating an inventory')
    lock = tomllib.loads(manifest.with_name('Cargo.lock').read_text(encoding='utf-8'))
    metadata = json.loads(subprocess.check_output(
        ['cargo', 'metadata', '--locked', '--format-version', '1', '--filter-platform', TARGETS[args.runtime], '--manifest-path', str(manifest)],
        text=True, encoding='utf-8'))
    result = generate(metadata, lock, repository, output, args.commit, args.runtime)
    print(f'Inventoried {len(result["packages"])} locked Rust/Tauri dependencies for {args.runtime}.')


if __name__ == '__main__':
    main()
