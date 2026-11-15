#!/usr/bin/env python3
"""Retain Android runtime dependency evidence and verify it inside APK/AAB files."""
import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import tempfile
import xml.etree.ElementTree as ET
from urllib.parse import quote
import zipfile

LIMIT = 2_000_000
NOTICE = re.compile(r'(?i)^(licen[cs]e|notice|copying|copyright)([._-]|$)')


def pom_metadata(path, group, name, version):
    if path.stat().st_size > LIMIT:
        raise ValueError('Unsafe or oversized Maven POM')
    data = path.read_bytes()
    if len(data) > LIMIT or re.search(br'<!\s*(?:DOCTYPE|ENTITY)\b', data, re.I):
        raise ValueError('Unsafe or oversized Maven POM')
    document = ET.fromstring(data)
    # Maven namespace handling without interpreting properties, fetching parents,
    # or executing any dependency code. Preserve the original POM as evidence.
    def child(element, tag):
        return next((e for e in element if e.tag.rsplit('}', 1)[-1] == tag), None)
    def text(element, tag):
        value = child(element, tag)
        return (value.text or '').strip() if value is not None else ''
    if document.tag.rsplit('}', 1)[-1] != 'project':
        raise ValueError('Expected Maven project metadata')
    for tag, wanted in [('groupId', group), ('artifactId', name), ('version', version)]:
        actual = text(document, tag)
        if tag == 'artifactId' and not actual:
            raise ValueError('Maven POM artifact identity missing')
        if actual and '${' not in actual and actual != wanted:
            raise ValueError('Maven POM does not match resolved dependency')
    licenses = child(document, 'licenses')
    declared = [] if licenses is None else [
        {'name': text(e, 'name'), 'url': text(e, 'url')}
        for e in licenses if e.tag.rsplit('}', 1)[-1] == 'license']
    return data, declared


def archive_notices(archive, prefix=''):
    # Never extract dependency-controlled filenames to the filesystem.
    names = set()
    for entry in sorted(archive.infolist(), key=lambda e: e.filename):
        path = PurePosixPath(entry.filename)
        if entry.is_dir() or not NOTICE.match(path.name):
            continue
        if path.is_absolute() or '..' in path.parts or '\\' in entry.filename or entry.filename in names:
            raise ValueError('Unsafe or duplicate dependency notice path')
        names.add(entry.filename)
        if entry.file_size > LIMIT or len(names) > 1000:
            raise ValueError('Oversized dependency notice evidence')
        yield prefix + entry.filename, archive.read(entry)
    # AAR licenses commonly reside in their embedded classes.jar.
    classes = [entry for entry in archive.infolist() if entry.filename == 'classes.jar']
    if len(classes) > 1:
        raise ValueError('Ambiguous AAR classes archive')
    if classes and not prefix:
        if classes[0].file_size > 100_000_000:
            raise ValueError('Oversized AAR classes archive')
        with zipfile.ZipFile(io.BytesIO(archive.read(classes[0]))) as nested:
            yield from archive_notices(nested, 'classes.jar!/')


def generate(graph, output):
    commit, variant, version = graph['commit'], graph['variant'], graph['version']
    if not re.fullmatch(r'[a-f0-9]{40}', commit) or variant not in ('debug', 'release') or not re.fullmatch(r'0\.1\.0-alpha\.[1-9][0-9]{0,4}', version):
        raise ValueError('Invalid Android inventory identity')
    if output.exists() and (not output.is_dir() or any(output.iterdir())):
        raise ValueError('Inventory output must be empty; refusing stale evidence')
    if not 1 <= len(graph['components']) <= 2500:
        raise ValueError('Expected a bounded Android runtime dependency graph')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='android-inventory-', dir=output.parent) as temporary:
        staging = Path(temporary)
        evidence = staging / 'license-evidence'
        evidence.mkdir()
        packages, components, identities = [], [], set()
        for item in sorted(graph['components'], key=lambda x: (x['group'], x['name'], x['version'])):
            group, name, resolved_version = item['group'], item['name'], item['version']
            if any(not re.fullmatch(r'[A-Za-z0-9_.+-]+', v) for v in (group, name, resolved_version)):
                raise ValueError('Invalid resolved Maven identity')
            coordinate = f'{group}:{name}:{resolved_version}'
            if coordinate in identities:
                raise ValueError('Duplicate resolved Maven identity')
            identities.add(coordinate)
            identity = 'SPDXRef-' + hashlib.sha256(coordinate.encode()).hexdigest()[:24]
            pom, declared = pom_metadata(Path(item['pom']), group, name, resolved_version)
            pom_name = f'license-evidence/{identity}.pom'
            (staging / pom_name).write_bytes(pom)
            archives, notices = [], []
            if len(item['archives']) > 20:
                raise ValueError('Too many dependency archives')
            archive_names = set()
            for artifact in sorted(item['archives'], key=lambda a: a['name']):
                filename = artifact['name']
                if PurePosixPath(filename).name != filename or '\\' in filename or filename in archive_names:
                    raise ValueError('Ambiguous or unsafe dependency archive name')
                archive_names.add(filename)
                source = Path(artifact['file'])
                with source.open('rb') as stream:
                    digest = hashlib.file_digest(stream, 'sha256').hexdigest()
                archives.append({'name': filename, 'sha256': digest})
                with zipfile.ZipFile(source) as archive:
                    for original, data in archive_notices(archive):
                        destination = f'license-evidence/{identity}-{len(notices)}.txt'
                        (staging / destination).write_bytes(data)
                        notices.append({'archive': filename, 'entry': original, 'file': destination})
            packages.append({
                'SPDXID': identity, 'name': group + ':' + name, 'versionInfo': resolved_version,
                'downloadLocation': 'NOASSERTION', 'filesAnalyzed': False,
                'licenseDeclared': 'NOASSERTION', 'licenseConcluded': 'NOASSERTION',
                'copyrightText': 'NOASSERTION',
                'sourceInfo': 'Resolved application runtime component; artifact hashes are in THIRD-PARTY-INVENTORY.json, not lockfile attestations.',
                'externalRefs': [{'referenceCategory': 'PACKAGE-MANAGER', 'referenceType': 'purl',
                                  'referenceLocator': f'pkg:maven/{quote(group, safe="")}/{quote(name, safe="")}@{quote(resolved_version, safe="")}'}],
                'comment': 'Unreviewed direct POM license declarations: ' + json.dumps(declared, sort_keys=True),
            })
            components.append({'group': group, 'name': name, 'version': resolved_version,
                               'pom': pom_name, 'declaredLicenses': declared, 'archives': archives,
                               'notices': notices, 'missingDirectLicense': not bool(declared),
                               'missingNoticeFiles': not bool(notices), 'metadataOnly': not bool(archives)})
        document = {
            'spdxVersion': 'SPDX-2.3', 'dataLicense': 'CC0-1.0', 'SPDXID': 'SPDXRef-DOCUMENT',
            'name': 'MediaServer-Android-' + variant,
            'documentNamespace': f'https://spdx.org/spdxdocs/media-server-android-{commit}-{variant}-{version}',
            'creationInfo': {'creators': ['Tool: MediaServer-android-inventory-1'], 'created': '1970-01-01T00:00:00Z'},
            'packages': packages,
            'relationships': [{'spdxElementId': 'SPDXRef-DOCUMENT', 'relationshipType': 'DESCRIBES', 'relatedSpdxElement': p['SPDXID']} for p in packages],
        }
        report = {'sourceCommit': commit, 'variant': variant, 'version': version, 'components': components,
                  'limitations': ['License declarations and copied evidence require review; no legal conclusions are asserted.',
                                  'Gradle enforces committed app/core classpath version locks. Archive hashes describe resolved input files, not lockfile archive checksums or transformed DEX/native code.',
                                  'Runtime metadata includes platforms/BOMs and compile-time annotations; graph presence does not prove linked-code redistribution.',
                                  'Only direct POM licenses are interpreted. Parent inheritance and property expressions are not resolved; original POMs and missing declarations are retained for review.',
                                  'Available standard-named archive notices are copied, including AAR classes.jar. Missing notice files are explicitly flagged.',
                                  'First-party app/core code, instrumentation/JVM tests, compiler/plugins, OS/SDK/toolchain and external native binaries are not covered by this inventory.']}
        for filename, value in [('SBOM.spdx.json', document), ('THIRD-PARTY-INVENTORY.json', report)]:
            (staging / filename).write_text(json.dumps(value, indent=2, sort_keys=True) + '\n', encoding='utf-8')
        sums = [f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(staging).as_posix()}' for p in sorted(staging.rglob('*')) if p.is_file()]
        (staging / 'SHA256SUMS').write_text('\n'.join(sums) + '\n', encoding='utf-8')
        if output.exists():
            output.rmdir()
        staging.rename(output)
    return document


def verify_package(package, expected, variant, commit=None, version=None):
    report = json.loads((expected / 'THIRD-PARTY-INVENTORY.json').read_text(encoding='utf-8'))
    if report['variant'] != variant or (commit is not None and report['sourceCommit'] != commit) or (version is not None and report['version'] != version):
        raise ValueError('Android package inventory identity mismatch')
    prefix = 'base/assets/inventory/' if package.suffix == '.aab' else 'assets/inventory/'
    with zipfile.ZipFile(package) as archive:
        entries = [e for e in archive.infolist() if e.filename.startswith(prefix) and not e.is_dir()]
        if len({e.filename for e in entries}) != len(entries):
            raise ValueError('Duplicate packaged Android inventory entries')
        actual = {entry.filename for entry in entries}
        files = {prefix + p.relative_to(expected).as_posix(): p for p in expected.rglob('*') if p.is_file()}
        if not (expected / 'SHA256SUMS').is_file() or not (expected / 'SBOM.spdx.json').is_file() or actual != files.keys():
            raise ValueError('Packaged Android inventory is missing, stale or incomplete')
        for name, source in files.items():
            entry = archive.getinfo(name)
            if entry.file_size != source.stat().st_size or entry.file_size > LIMIT or archive.read(entry) != source.read_bytes():
                raise ValueError('Packaged Android inventory differs from generated evidence')
    print(f'{package.name}: {variant} inventory matches every generated evidence file.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--graph', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--verify-build', type=Path, help='Android app build directory')
    parser.add_argument('--commit')
    parser.add_argument('--version')
    args = parser.parse_args()
    if args.verify_build:
        for relative, variant in [('outputs/apk/debug/app-debug.apk', 'debug'),
                                  ('outputs/apk/release/app-release-unsigned.apk', 'release'),
                                  ('outputs/bundle/release/app-release.aab', 'release')]:
            expected = args.verify_build / 'reports/dependencyInventory' / variant
            verify_package(args.verify_build / relative, expected, variant, args.commit, args.version)
    else:
        if not args.graph or not args.output:
            parser.error('--graph and --output are required when generating evidence')
        result = generate(json.loads(args.graph.read_text(encoding='utf-8')), args.output)
        print(f'Inventoried {len(result["packages"])} resolved Android runtime components.')


if __name__ == '__main__':
    main()
