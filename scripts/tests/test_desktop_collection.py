import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    'desktop_collection', Path(__file__).resolve().parents[1] / 'collect-desktop-client.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DesktopCollectionTests(unittest.TestCase):
    version = '0.1.0-alpha.42'

    def write(self, root, name, content=b'installer'):
        file = root / 'clients/windows/src-tauri/target/release/bundle' / name
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_bytes(content)
        return file

    def test_collects_final_mac_installer_after_retry_with_intermediates_and_old_builds(self):
        for rid, arch in (('osx-x64', 'x64'), ('osx-arm64', 'aarch64')):
            with self.subTest(rid=rid), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                final = f'Media Server Client_{self.version}_{arch}.dmg'
                self.write(root, 'dmg/rw.12345.' + final, b'partial retry image')
                self.write(root, 'dmg/Media Server Client_0.1.0-alpha.41_' + arch + '.dmg', b'old')
                self.write(root, 'other/' + final, b'unrelated output')
                self.write(root, 'dmg/' + final, b'correct final installer')
                result = module.collect(root, self.version, rid)
                self.assertEqual(result.name, f'MediaServer-Desktop-Client-v{self.version}-{rid}.dmg')
                self.assertEqual(result.read_bytes(), b'correct final installer')
                self.assertTrue((root / 'clients/windows/src-tauri/target/release/bundle/dmg' / ('rw.12345.' + final)).exists())

    def test_refuses_temporary_old_or_wrong_architecture_images_without_expected_installer(self):
        for name in ('rw.12345.Media Server Client_0.1.0-alpha.42_x64.dmg',
                     'Media Server Client_0.1.0-alpha.41_x64.dmg',
                     'Media Server Client_0.1.0-alpha.42_aarch64.dmg'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                self.write(root, 'dmg/' + name)
                with self.assertRaisesRegex(ValueError, 'missing'):
                    module.collect(root, self.version, 'osx-x64')
                self.assertFalse((root / 'artifacts/desktop').exists())

    def test_linux_still_requires_one_deb_and_preserves_release_naming(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.write(root, 'deb/client.deb', b'Linux installer')
            result = module.collect(root, self.version, 'linux-x64')
            self.assertEqual(result.name, f'MediaServer-Desktop-Client-v{self.version}-linux-x64.deb')
            self.assertEqual(result.read_bytes(), b'Linux installer')
            self.write(root, 'deb/another.deb')
            with self.assertRaisesRegex(ValueError, 'exactly one'):
                module.collect(root, self.version, 'linux-x64')

    def test_rejects_invalid_release_identities_before_creating_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for version, rid in (('../escape', 'osx-x64'), (self.version, 'osx-unknown'),
                                 ('0.1.0-alpha.0', 'linux-x64')):
                with self.subTest(version=version, rid=rid), self.assertRaises(ValueError):
                    module.collect(root, version, rid)
            self.assertFalse((root / 'artifacts/desktop').exists())
