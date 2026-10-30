import base64
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('release_inventory', Path(__file__).resolve().parents[1] / 'release-inventory.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ReleaseInventoryTests(unittest.TestCase):
    def fixture(self, root):
        cache = root / 'cache'
        package = cache / 'example/1.0.0'
        package.mkdir(parents=True)
        (package / 'example.nuspec').write_text('<package xmlns="urn:nuget"><metadata><license type="file">LICENSE</license></metadata></package>')
        (package / 'LICENSE').write_text('Explicit synthetic license fixture, not real package terms.')
        integrity = base64.b64encode(b'a' * 64).decode()
        assets = root / 'assets.json'
        assets.write_text(json.dumps({'packageFolders': {str(cache): {}}, 'libraries': {'Example/1.0.0': {'type': 'package', 'path': 'example/1.0.0', 'sha512': integrity}}}))
        nodes = root / 'node_modules'
        npm = nodes / 'example-web'
        npm.mkdir(parents=True)
        (npm / 'package.json').write_text(json.dumps({'name': 'example-web', 'version': '2.0.0', 'license': 'MIT'}))
        (npm / 'LICENSE').write_text('Synthetic browser license fixture')
        lock = root / 'package-lock.json'
        lock.write_text(json.dumps({'packages': {'': {}, 'node_modules/example-web': {'version': '2.0.0', 'integrity': 'sha512-' + integrity}, 'node_modules/dev-only': {'version': '3.0.0', 'dev': True}}}))
        return assets, lock, nodes

    def test_real_metadata_is_bounded_deterministic_and_not_a_legal_conclusion(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            args = self.fixture(root)
            first = module.inventory(*args, root / 'first', 'a' * 40, 'linux-x64')
            second = module.inventory(*args, root / 'second', 'a' * 40, 'linux-x64')
            self.assertEqual(first, second)
            self.assertEqual(first['spdxVersion'], 'SPDX-2.3')
            self.assertEqual(len(first['packages']), 2)
            self.assertTrue(all(x['licenseConcluded'] == 'NOASSERTION' for x in first['packages']))
            report = (root / 'first/THIRD-PARTY-INVENTORY.json').read_text()
            self.assertNotIn(str(root), report)
            self.assertNotIn('dev-only', report)
            self.assertEqual(len(list((root / 'first/license-evidence').iterdir())), 2)
            self.assertEqual(len(first['packages'][0]['checksums'][0]['checksumValue']), 128)
            self.assertTrue(all(value['notices'] for value in json.loads(report)['components']))

    def test_traversal_and_malformed_checksums_fail_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            args = self.fixture(root)
            data = json.loads(args[0].read_text())
            data['libraries']['Example/1.0.0']['path'] = '../secret'
            args[0].write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, 'Unsafe package'):
                module.inventory(*args, root / 'output', 'a' * 40, 'win-x64')
        for value in ('sha512-not-base64', 'md5-YQ==', 'sha512-YQ=='):
            with self.assertRaises(ValueError):
                module.checksum(value)

    def test_missing_restored_metadata_cannot_produce_a_fake_inventory(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            args = self.fixture(root)
            (root / 'cache/example/1.0.0/example.nuspec').unlink()
            with self.assertRaisesRegex(ValueError, 'metadata'):
                module.inventory(*args, root / 'output', 'a' * 40, 'linux-x64')

    def test_installed_browser_version_and_identity_must_match_lock(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            args = self.fixture(root)
            (args[2] / 'example-web/package.json').write_text('{"name":"example-web","version":"99"}')
            with self.assertRaisesRegex(ValueError, 'lockfile'):
                module.inventory(*args, root / 'output', 'a' * 40, 'linux-x64')

    def test_existing_output_is_not_silently_reused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            args = self.fixture(root)
            target = root / 'output'
            target.mkdir()
            (target / 'unowned.txt').write_text('fixture')
            with self.assertRaisesRegex(ValueError, 'empty'):
                module.inventory(*args, target, 'a' * 40, 'linux-x64')
            self.assertTrue((target / 'unowned.txt').is_file())

    def test_real_release_generates_inventory_before_packaging(self):
        text = (Path(__file__).resolve().parents[2] / '.github/workflows/release.yml').read_text()
        self.assertLess(text.index('Inventory locked server'), text.index('Package and smoke-test the actual archive'))
        self.assertIn('--output artifacts/server/inventory', text)


if __name__ == '__main__':
    unittest.main()
