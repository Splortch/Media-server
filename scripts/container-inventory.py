#!/usr/bin/env python3
"""Record installed package identities and FFmpeg build evidence from a Docker image."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tarfile
from urllib.parse import quote

PACKAGE_MANIFEST = 'corresponding-source/installed-packages.tsv'
DOCKER_DIR = Path(__file__).resolve().parents[1] / 'docker'


def ubuntu_packages(manifest):
    rows = []
    for line in manifest.splitlines():
        binary, version, source, source_version, arch = line.split('\t')
        name = binary.removesuffix(':' + arch)
        if not all(re.fullmatch(r'[a-z0-9][a-z0-9+.-]*', part) for part in (name, source, arch)):
            raise ValueError(f'Invalid installed package identity: {binary}')
        if not all(re.fullmatch(r'[0-9][a-zA-Z0-9.+:~\-]*', part) for part in (version, source_version)):
            raise ValueError(f'Invalid package version: {binary}')
        rows.append((name, version, source, source_version, arch))
    if len({(name, arch) for name, _, _, _, arch in rows}) != len(rows):
        raise ValueError('Duplicate installed package identity')
    if not any(name == 'ffmpeg' for name, *_ in rows):
        raise ValueError('The runtime package inventory is missing FFmpeg')
    return sorted(rows)


def runtime_packages(runtimes):
    versions = []
    for line in runtimes.splitlines():
        match = re.fullmatch(r'(Microsoft\.(?:NETCore|AspNetCore)\.App) (\d+\.\d+\.\d+) \[[^\]\r\n]+\]', line)
        if not match:
            raise ValueError('Unexpected .NET shared runtime metadata')
        versions.append(match.groups())
    if len(versions) != 2 or {name for name, _ in versions} != {'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'}:
        raise ValueError('Expected exactly one .NET runtime and one ASP.NET Core runtime')
    return sorted(versions)


def spdx_package(name, version, purl, source_info):
    return {
        'SPDXID': 'SPDXRef-' + hashlib.sha256(purl.encode()).hexdigest()[:24],
        'name': name, 'versionInfo': version, 'downloadLocation': 'NOASSERTION',
        'filesAnalyzed': False, 'licenseConcluded': 'NOASSERTION',
        'licenseDeclared': 'NOASSERTION', 'copyrightText': 'NOASSERTION',
        'externalRefs': [{'referenceCategory': 'PACKAGE-MANAGER', 'referenceType': 'purl', 'referenceLocator': purl}],
        'sourceInfo': source_info,
    }


def inventory(image_id, manifest, runtimes, ffmpeg_build):
    if not re.fullmatch(r'sha256:[a-f0-9]{64}', image_id):
        raise ValueError('Expected the immutable Docker image configuration digest')
    if not ffmpeg_build.startswith('ffmpeg version ') or not re.search(r'^configuration:\s+--', ffmpeg_build, re.M):
        raise ValueError('FFmpeg build configuration is missing')
    packages = [
        spdx_package(
            name, version, f'pkg:deb/ubuntu/{quote(name, safe="")}@{quote(version, safe="")}?arch={arch}',
            f'Installed Ubuntu binary ({arch}); source: {source}={source_version}.',
        )
        for name, version, source, source_version, arch in ubuntu_packages(manifest)
    ]
    packages.extend(
        spdx_package(name, version, f'pkg:generic/{name}@{version}', '.NET shared runtime installed in the base image.')
        for name, version in runtime_packages(runtimes)
    )
    return {
        'spdxVersion': 'SPDX-2.3', 'dataLicense': 'CC0-1.0', 'SPDXID': 'SPDXRef-DOCUMENT',
        'name': 'MediaServer container system packages',
        'documentNamespace': f'https://spdx.org/spdxdocs/media-server-docker-{image_id.split(":")[1]}',
        'creationInfo': {
            'creators': ['Tool: MediaServer-container-inventory-1'],
            'created': '1970-01-01T00:00:00Z',
            'comment': 'Deterministic inventory of dpkg packages and .NET shared runtimes. Epoch timestamp is intentional.',
        },
        'comment': f'Docker config digest {image_id}. FFmpeg build and corresponding-source evidence are distributed separately.',
        'packages': packages,
        'relationships': [
            {'spdxElementId': 'SPDXRef-DOCUMENT', 'relationshipType': 'DESCRIBES', 'relatedSpdxElement': item['SPDXID']}
            for item in packages
        ],
    }


def verify_source_bundle(bundle, installed):
    with tarfile.open(bundle, 'r|gz') as archive:
        entry = archive.next()
        if entry is None or entry.name != PACKAGE_MANIFEST or not entry.isfile() or entry.size > 500_000:
            raise ValueError('Corresponding-source archive is missing its leading package manifest')
        source_manifest = archive.extractfile(entry).read().decode('utf-8')
    if ubuntu_packages(source_manifest) != ubuntu_packages(installed):
        raise ValueError('Corresponding-source packages do not match the shipped runtime image')


def docker(*args, stdin=None):
    return subprocess.run(['docker', *args], input=stdin, text=True, stdout=subprocess.PIPE,
                          stderr=subprocess.STDOUT, check=True).stdout


def collect(image, output, source_bundle):
    image_id = docker('image', 'inspect', '--format', '{{.Id}}', image).strip()

    def run(command, *args, stdin=None):
        return docker('run', '--rm', '-i', '--entrypoint', command, image_id, *args, stdin=stdin)

    manifest = run('sh', '-s', stdin=(DOCKER_DIR / 'installed-packages.sh').read_text(encoding='utf-8'))
    runtimes = run('dotnet', '--list-runtimes')
    ffmpeg_build = run('ffmpeg', '-version')
    ffmpeg_license = run('ffmpeg', '-L')
    document = inventory(image_id, manifest, runtimes, ffmpeg_build)
    if source_bundle:
        verify_source_bundle(source_bundle, manifest)
    output.mkdir(parents=True, exist_ok=True)
    for name, data in {
        'CONTAINER-PACKAGES.txt': manifest,
        'FFMPEG-LICENSE.txt': ffmpeg_license,
        'FFMPEG-BUILD.txt': ffmpeg_build,
        'CONTAINER-SBOM.spdx.json': json.dumps(document, indent=2, sort_keys=True) + '\n',
    }.items():
        (output / name).write_text(data, encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('image')
    parser.add_argument('output', type=Path)
    parser.add_argument('--source-bundle', type=Path)
    args = parser.parse_args()
    try:
        collect(args.image, args.output, args.source_bundle)
    except subprocess.CalledProcessError as error:
        raise SystemExit(error.stdout) from error
