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

    def runtime_fixture(self, root, runtime='linux-x64'):
        published = root / 'published'
        published.mkdir()
        frameworks = []
        libraries = {}
        for name in ('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'):
            version = '10.0.14'
            package_id = name + '.Runtime.' + runtime
            package = root / 'cache' / package_id.lower() / version
            package.mkdir(parents=True)
            (package / 'runtime.nuspec').write_text(
                f'<package><metadata><id>{package_id}</id><version>{version}</version>'
                '<license type="expression">MIT</license></metadata></package>')
            (package / 'LICENSE.txt').write_text('Synthetic Microsoft license evidence')
            (package / 'THIRD-PARTY-NOTICES.TXT').write_text('Synthetic upstream notices')
            frameworks.append({'name': name, 'version': version})
            libraries['runtimepack.' + package_id + '/' + version] = {'type': 'runtimepack'}
        (published / 'Api.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'includedFrameworks': frameworks}}))
        (published / 'Api.deps.json').write_text(json.dumps({'runtimeTarget': {'name': '.NETCoreApp,Version=v10.0/' + runtime}, 'libraries': libraries}))
        return published

    def test_published_runtime_packs_include_exact_license_and_upstream_notices(self):
        for runtime in ('linux-x64', 'win-x64', 'osx-x64', 'osx-arm64'):
            with self.subTest(runtime=runtime), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                args = self.fixture(root)
                published = self.runtime_fixture(root, runtime)
                first = module.inventory(*args, root / 'first', 'a' * 40, runtime, published)
                second = module.inventory(*args, root / 'second', 'a' * 40, runtime, published)
                self.assertEqual(first, second)
                self.assertEqual(len(first['packages']), 4)
                report = json.loads((root / 'first/THIRD-PARTY-INVENTORY.json').read_text())
                self.assertTrue(report['runtimePackEvidence'])
                packs = [item for item in report['components'] if item['scope'] == 'published self-contained runtime pack']
                self.assertEqual(len(packs), 2)
                for pack in packs:
                    self.assertEqual(len(pack['notices']), 2)
                    texts = [(root / 'first' / path).read_text() for path in pack['notices']]
                    self.assertIn('Synthetic Microsoft license evidence', texts)
                    self.assertIn('Synthetic upstream notices', texts)
                self.assertNotIn(str(root), json.dumps(report))
                for item in first['packages']:
                    if '.Runtime.' in item['name']:
                        self.assertNotIn('checksums', item)
                        self.assertEqual(item['licenseConcluded'], 'NOASSERTION')

    def test_runtime_evidence_must_match_published_platform_frameworks_and_cache(self):
        mutations = ('platform', 'framework', 'deps_version', 'extra_pack', 'nuspec_identity', 'license', 'notices')
        for mutation in mutations:
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                args = self.fixture(root)
                published = self.runtime_fixture(root)
                config_path, deps_path = published / 'Api.runtimeconfig.json', published / 'Api.deps.json'
                config, deps = json.loads(config_path.read_text()), json.loads(deps_path.read_text())
                pack = root / 'cache/microsoft.netcore.app.runtime.linux-x64/10.0.14'
                if mutation == 'platform':
                    deps['runtimeTarget']['name'] = '.NETCoreApp,Version=v10.0/win-x64'
                elif mutation == 'framework':
                    config['runtimeOptions']['includedFrameworks'].pop()
                elif mutation == 'deps_version':
                    config['runtimeOptions']['includedFrameworks'][0]['version'] = '10.0.15'
                elif mutation == 'extra_pack':
                    deps['libraries']['runtimepack.Extra/1.0.0'] = {'type': 'runtimepack'}
                elif mutation == 'nuspec_identity':
                    (pack / 'runtime.nuspec').write_text('<package><metadata><id>Wrong</id><version>10.0.14</version></metadata></package>')
                elif mutation == 'license':
                    (pack / 'LICENSE.txt').unlink()
                elif mutation == 'notices':
                    (pack / 'THIRD-PARTY-NOTICES.TXT').unlink()
                config_path.write_text(json.dumps(config))
                deps_path.write_text(json.dumps(deps))
                with self.assertRaises(ValueError):
                    module.inventory(*args, root / 'output', 'a' * 40, 'linux-x64', published)

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
        self.assertIn('--published-dir artifacts/server', text)


if __name__ == '__main__':
    unittest.main()
