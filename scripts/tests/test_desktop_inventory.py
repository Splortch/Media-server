import copy
import hashlib
import importlib.util
import json
import shutil
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('desktop_inventory', Path(__file__).resolve().parents[1] / 'desktop-inventory.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DesktopInventoryTests(unittest.TestCase):
    def fixture(self, root):
        registry = 'registry+https://github.com/rust-lang/crates.io-index'
        git = 'git+https://example.invalid/reviewed?rev=' + 'b' * 40 + '#' + 'b' * 40
        packages, nodes, locked = [], [], []
        for name, source, location in [
            ('media-server-windows-client', None, 'clients/windows/src-tauri'),
            ('media-client-core', None, 'clients/windows/native'),
            ('example', registry, 'cache/example'),
            ('example', git, 'checkout/nested/example'),
            ('build-helper', registry, 'cache/build-helper'),
            ('dev-only', registry, 'cache/dev-only'),
            ('other-platform', registry, 'cache/other-platform'),
        ]:
            directory = root / location
            directory.mkdir(parents=True)
            (directory / 'Cargo.toml').write_text('[package]\nname="' + name + '"\nversion="1.0.0"\n')
            (directory / 'LICENSE').write_text('Synthetic license for ' + name)
            identity = str(len(packages))
            packages.append({'id': identity, 'name': name, 'version': '1.0.0', 'source': source,
                             'manifest_path': str(directory / 'Cargo.toml'), 'license': 'MIT', 'license_file': None})
            nodes.append({'id': identity, 'deps': []})
            locked.append({'name': name, 'version': '1.0.0', **({'source': source} if source else {}),
                           **({'checksum': 'c' * 64} if source == registry else {})})
        def edge(index, kind=None):
            return {'pkg': str(index), 'dep_kinds': [{'kind': kind, 'target': None}]}
        nodes[0]['deps'] = [edge(1), edge(2), edge(3), edge(4, 'build'), edge(5, 'dev')]
        nodes[2]['deps'] = [edge(4)]  # Shared/cyclic edges must not duplicate or loop.
        nodes[4]['deps'] = [edge(2)]
        (root / 'checkout/.git').mkdir()
        (root / 'checkout/WORKSPACE-LICENSE').write_text('Synthetic workspace license')
        packages[3]['license_file'] = '../../WORKSPACE-LICENSE'
        nested = root / 'cache/example/third_party/vendor'
        nested.mkdir(parents=True)
        (nested / 'NOTICE.txt').write_text('Synthetic nested notice')
        return {'packages': packages, 'resolve': {'root': '0', 'nodes': nodes}}, {'package': locked}

    def generate(self, root, metadata, lock, name='output', runtime='linux-x64'):
        return module.generate(metadata, lock, root, root / name, 'a' * 40, runtime)

    def test_target_graph_versions_forks_notices_and_checksums_are_deterministic(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            metadata, lock = self.fixture(root)
            first = self.generate(root, metadata, lock, 'first')
            second = self.generate(root, metadata, lock, 'second')
            self.assertEqual(first, second)
            self.assertEqual(len(first['packages']), 3)
            self.assertCountEqual([p['name'] for p in first['packages']], ['example', 'example', 'build-helper'])
            self.assertEqual(len({p['SPDXID'] for p in first['packages']}), 3)
            git = next(p for p in first['packages'] if p['sourceInfo'].startswith('git+'))
            self.assertNotIn('checksums', git)
            self.assertIn('vcs_url=', git['externalRefs'][0]['referenceLocator'])
            registry = next(p for p in first['packages'] if p['name'] == 'example' and 'checksums' in p)
            self.assertEqual(registry['checksums'][0]['checksumValue'], 'c' * 64)
            self.assertTrue(all(p['licenseConcluded'] == 'NOASSERTION' for p in first['packages']))
            for file in (root / 'first').rglob('*'):
                if file.is_file():
                    relative = file.relative_to(root / 'first')
                    self.assertEqual(file.read_bytes(), (root / 'second' / relative).read_bytes())
            report = (root / 'first/THIRD-PARTY-INVENTORY.json').read_text()
            self.assertNotIn(str(root), report)
            texts = [p.read_text() for p in (root / 'first/license-evidence').iterdir()]
            self.assertIn('Synthetic nested notice', texts)
            self.assertIn('Synthetic workspace license', texts)
            for line in (root / 'first/SHA256SUMS').read_text().splitlines():
                digest, name = line.split('  ', 1)
                self.assertEqual(digest, hashlib.sha256((root / 'first' / name).read_bytes()).hexdigest())

    def test_bad_graph_lock_or_notice_cannot_publish_a_partial_inventory(self):
        mutations = ('version', 'source', 'checksum', 'missing_node', 'missing_package',
                     'duplicate_id', 'unknown_kind', 'unknown_local', 'unpinned_git',
                     'escaping_notice', 'oversized_notice', 'missing_license')
        for mutation in mutations:
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                metadata, lock = self.fixture(root)
                if mutation == 'version':
                    lock['package'][2]['version'] = '9.0.0'
                elif mutation == 'source':
                    lock['package'][2]['source'] = 'registry+https://wrong.invalid'
                elif mutation == 'checksum':
                    lock['package'][2]['checksum'] = 'not-a-sha256'
                elif mutation == 'missing_node':
                    metadata['resolve']['nodes'] = metadata['resolve']['nodes'][1:]
                elif mutation == 'missing_package':
                    metadata['packages'].pop(2)
                elif mutation == 'duplicate_id':
                    metadata['packages'].append(copy.deepcopy(metadata['packages'][2]))
                elif mutation == 'unknown_kind':
                    metadata['resolve']['nodes'][0]['deps'][0]['dep_kinds'][0]['kind'] = 'unknown'
                elif mutation == 'unknown_local':
                    metadata['packages'][1]['name'] = lock['package'][1]['name'] = 'unexpected-local'
                elif mutation == 'unpinned_git':
                    metadata['packages'][3]['source'] = lock['package'][3]['source'] = 'git+https://wrong.invalid#main'
                elif mutation == 'escaping_notice':
                    (root / 'secret').write_text('private')
                    metadata['packages'][2]['license_file'] = '../../secret'
                elif mutation == 'oversized_notice':
                    (root / 'cache/example/LICENSE').write_bytes(b'x' * 2_000_001)
                elif mutation == 'missing_license':
                    metadata['packages'][4]['license'] = None
                    (root / 'cache/build-helper/LICENSE').unlink()
                with self.assertRaises(ValueError):
                    self.generate(root, metadata, lock)
                self.assertFalse((root / 'output').exists())

    def test_missing_notice_is_visible_and_stale_inventory_is_never_reused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            metadata, lock = self.fixture(root)
            (root / 'cache/build-helper/LICENSE').unlink()
            self.generate(root, metadata, lock)
            report = json.loads((root / 'output/THIRD-PARTY-INVENTORY.json').read_text())
            entry = next(p for p in report['components'] if p['name'] == 'build-helper')
            self.assertTrue(entry['missingNoticeFiles'])
            with self.assertRaisesRegex(ValueError, 'stale'):
                self.generate(root, metadata, lock)

    def test_platform_identity_and_empty_existing_output(self):
        for runtime, triple in module.TARGETS.items():
            with self.subTest(runtime=runtime), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                metadata, lock = self.fixture(root)
                (root / 'output').mkdir()
                self.generate(root, metadata, lock, runtime=runtime)
                report = json.loads((root / 'output/THIRD-PARTY-INVENTORY.json').read_text())
                self.assertEqual(report['runtime'], runtime)
                self.assertEqual(report['targetTriple'], triple)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            metadata, lock = self.fixture(root)
            with self.assertRaisesRegex(ValueError, 'identity'):
                module.generate(metadata, lock, root, root / 'output', '../escape', 'linux-x64')

    def test_installer_verification_rejects_missing_changed_or_duplicate_evidence(self):
        for mutation in ('none', 'missing_notice', 'changed_report', 'duplicate', 'missing_expected'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                metadata, lock = self.fixture(root)
                self.generate(root, metadata, lock)
                installed = root / 'installed'
                bundle = installed / 'Application/Resources/inventory'
                shutil.copytree(root / 'output', bundle)
                if mutation == 'missing_notice':
                    next((bundle / 'license-evidence').iterdir()).unlink()
                elif mutation == 'changed_report':
                    (bundle / 'THIRD-PARTY-INVENTORY.json').write_text('{}')
                elif mutation == 'duplicate':
                    shutil.copytree(root / 'output', installed / 'Other/inventory')
                elif mutation == 'missing_expected':
                    (root / 'output/SHA256SUMS').unlink()
                if mutation == 'none':
                    module.verify_installed(root / 'output', installed)
                else:
                    with self.assertRaises((ValueError, FileNotFoundError)):
                        module.verify_installed(root / 'output', installed)
