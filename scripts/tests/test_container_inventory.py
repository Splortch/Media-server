"""Container inventory regression tests with synthetic installed package evidence."""

import importlib.util
from io import BytesIO
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    'container_inventory', Path(__file__).resolve().parents[1] / 'container-inventory.py',
)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

IMAGE = 'sha256:' + 'a' * 64
FFMPEG = 'ffmpeg\t7:6.1.1-3ubuntu5\tffmpeg\t7:6.1.1-3ubuntu5\tamd64\n'
LIBAV = 'libavcodec60:amd64\t7:6.1.1-3ubuntu5\tffmpeg\t7:6.1.1-3ubuntu5\tamd64\n'
RUNTIMES = (
    'Microsoft.NETCore.App 10.0.15 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n'
    'Microsoft.AspNetCore.App 10.0.15 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]\n'
)
BUILD = 'ffmpeg version 6.1.1\nbuilt with gcc\nconfiguration: --enable-gpl\n'


class ContainerInventoryTests(unittest.TestCase):
    def test_inventories_binary_source_and_runtime_versions_deterministically(self):
        result = module.inventory(IMAGE, FFMPEG + LIBAV, RUNTIMES, BUILD)
        self.assertEqual(result, module.inventory(IMAGE, LIBAV + FFMPEG, RUNTIMES, BUILD))
        self.assertEqual(result['spdxVersion'], 'SPDX-2.3')
        self.assertIn(IMAGE, result['comment'])
        packages = {entry['name']: entry for entry in result['packages']}
        self.assertEqual(set(packages), {'ffmpeg', 'libavcodec60', 'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'})
        self.assertEqual(packages['ffmpeg']['versionInfo'], '7:6.1.1-3ubuntu5')
        self.assertEqual(
            packages['libavcodec60']['externalRefs'][0]['referenceLocator'],
            'pkg:deb/ubuntu/libavcodec60@7%3A6.1.1-3ubuntu5?arch=amd64',
        )
        self.assertIn('source: ffmpeg=7:6.1.1-3ubuntu5', packages['libavcodec60']['sourceInfo'])
        self.assertEqual(packages['Microsoft.NETCore.App']['versionInfo'], '10.0.15')
        self.assertEqual(len(result['relationships']), len(packages))

    def test_missing_ffmpeg_build_and_runtime_metadata_are_rejected(self):
        for args in [
            ('media-server:latest', FFMPEG, RUNTIMES, BUILD),
            (IMAGE, LIBAV, RUNTIMES, BUILD),
            (IMAGE, FFMPEG + FFMPEG, RUNTIMES, BUILD),
            (IMAGE, FFMPEG, '', BUILD),
            (IMAGE, FFMPEG, RUNTIMES, 'ffmpeg version 6.1.1'),
        ]:
            with self.subTest(args=args), self.assertRaises(ValueError):
                module.inventory(*args)

    def test_corresponding_source_must_match_the_image_package_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            bundle = Path(temporary) / 'sources.tar.gz'
            with tarfile.open(bundle, 'w:gz') as archive:
                manifest = (FFMPEG + LIBAV).encode()
                entry = tarfile.TarInfo(module.PACKAGE_MANIFEST)
                entry.size = len(manifest)
                archive.addfile(entry, BytesIO(manifest))
            module.verify_source_bundle(bundle, LIBAV + FFMPEG)
            with self.assertRaisesRegex(ValueError, 'do not match'):
                module.verify_source_bundle(bundle, FFMPEG)

    def test_extracts_evidence_from_the_same_immutable_image(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            with patch.object(module, 'docker', side_effect=[IMAGE + '\n', FFMPEG, RUNTIMES, BUILD, 'license evidence']) as docker:
                module.collect('media-server:ci', output, None)
            self.assertEqual(docker.call_count, 5)
            for command in docker.call_args_list[1:]:
                self.assertIn(IMAGE, command.args)
                self.assertNotIn('media-server:ci', command.args)
            self.assertEqual((output / 'FFMPEG-BUILD.txt').read_text(), BUILD)
            self.assertEqual((output / 'FFMPEG-LICENSE.txt').read_text(), 'license evidence')
            self.assertIn(IMAGE, json.loads((output / 'CONTAINER-SBOM.spdx.json').read_text())['comment'])


if __name__ == '__main__':
    unittest.main()
