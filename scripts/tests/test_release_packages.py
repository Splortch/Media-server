"""Release packaging policy tests; executables here are explicitly test fixtures."""
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('package_release', Path(__file__).resolve().parents[1] / 'package-release.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ReleasePackagesTests(unittest.TestCase):
    def test_archive_has_one_named_root_and_launcher(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / 'MediaServer-test'
            source.mkdir()
            (source / 'Start-MediaServer.cmd').write_text('test fixture')
            module.archive(source, root / 'out.zip')
            with zipfile.ZipFile(root / 'out.zip') as archive:
                self.assertEqual(archive.namelist(), ['MediaServer-test/Start-MediaServer.cmd'])

    def test_ambiguous_or_missing_installers_fail(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            with self.assertRaises(RuntimeError):
                module.one(folder, '*.msi')
            (folder / 'one.msi').write_text('test fixture')
            self.assertEqual(module.one(folder, '*.msi').name, 'one.msi')
            (folder / 'two.msi').write_text('test fixture')
            with self.assertRaises(RuntimeError):
                module.one(folder, '*.msi')

    def test_assembly_requires_every_platform_and_emits_integrity_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            artifacts = Path(temporary)
            downloads = artifacts / 'downloads'
            for rid, ext in [('win-x64', '.zip'), ('linux-x64', '.tar.gz')]:
                folder = downloads / f'release-server-{rid}'
                folder.mkdir(parents=True)
                (folder / f'MediaServer-v0.2.0-alpha.5-{rid}{ext}').write_text('test archive fixture')
            native = downloads / 'release-windows-client'
            native.mkdir()
            (native / 'client-setup.exe').write_text('test installer fixture')
            (native / 'client.msi').write_text('test installer fixture')
            container = downloads / 'release-container'
            container.mkdir()
            for name in ('CONTAINER-PACKAGES.txt', 'FFMPEG-LICENSE.txt', 'Container-Corresponding-Source.tar.gz'):
                (container / name).write_text('test source fixture')
            env = dict(SERVER_VERSION='0.2.0-alpha.5', CLIENT_VERSION='0.1.0-alpha.5', SOURCE_SHA='a' * 40, GITHUB_REPOSITORY='Splortch/Media-server', RELEASE_TAG='v0.2.0-alpha.5')
            with patch.object(module, 'ARTIFACTS', artifacts), patch.dict(os.environ, env):
                module.assemble()
            output = artifacts / 'release'
            self.assertEqual(len(list(output.iterdir())), 10)
            self.assertEqual(len((output / 'SHA256SUMS').read_text().splitlines()), 9)
            self.assertTrue((output / 'MediaServer-Windows-Client-v0.1.0-alpha.5-win-x64.msi').exists())
            with zipfile.ZipFile(output / 'MediaServer-Docker-v0.2.0-alpha.5.zip') as archive:
                text = archive.read('MediaServer-Docker-v0.2.0-alpha.5/compose.yaml').decode()
                self.assertIn('ghcr.io/splortch/media-server:v0.2.0-alpha.5', text)
                self.assertIn('127.0.0.1:5050:5050', text)
                self.assertIn('/media:ro', text)


if __name__ == '__main__':
    unittest.main()
