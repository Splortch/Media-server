import copy
from contextlib import nullcontext
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('android_inventory', Path(__file__).resolve().parents[1] / 'android-inventory.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class AndroidInventoryTests(unittest.TestCase):
    def fixture(self, root, variant='release'):
        pom = root / 'example.pom'
        pom.write_text('''<project xmlns="http://maven.apache.org/POM/4.0.0"><groupId>org.example</groupId>
<artifactId>library</artifactId><version>1.2.3</version><licenses><license><name>MIT</name>
<url>https://example.invalid/license</url></license></licenses></project>''')
        nested = io.BytesIO()
        with zipfile.ZipFile(nested, 'w') as archive:
            archive.writestr('META-INF/LICENSE', b'Synthetic nested license')
        artifact = root / 'library-1.2.3.aar'
        with zipfile.ZipFile(artifact, 'w') as archive:
            archive.writestr('NOTICE.txt', b'Synthetic AAR notice')
            archive.writestr('classes.jar', nested.getvalue())
        return {'commit': 'a' * 40, 'version': '0.1.0-alpha.3', 'variant': variant,
                'components': [{'group': 'org.example', 'name': 'library', 'version': '1.2.3',
                                'pom': str(pom), 'archives': [{'name': artifact.name, 'file': str(artifact)}]}]}

    def package(self, path, expected):
        prefix = 'base/assets/inventory/' if path.suffix == '.aab' else 'assets/inventory/'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('AndroidManifest.xml', b'Synthetic manifest')
            for file in expected.rglob('*'):
                if file.is_file():
                    archive.write(file, prefix + file.relative_to(expected).as_posix())

    def test_exact_runtime_components_archive_hashes_and_nested_notices_are_repeatable(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            graph = self.fixture(root)
            first = module.generate(graph, root / 'one')
            second = module.generate(graph, root / 'two')
            self.assertEqual(first, second)
            self.assertEqual(len(first['packages']), 1)
            self.assertEqual(first['packages'][0]['licenseConcluded'], 'NOASSERTION')
            self.assertNotIn('checksums', first['packages'][0])
            report = json.loads((root / 'one/THIRD-PARTY-INVENTORY.json').read_text())
            self.assertNotIn(str(root), json.dumps(report))
            component = report['components'][0]
            self.assertEqual(component['declaredLicenses'][0]['name'], 'MIT')
            self.assertEqual(component['archives'][0]['sha256'], hashlib.sha256((root / 'library-1.2.3.aar').read_bytes()).hexdigest())
            self.assertEqual(len(component['notices']), 2)
            self.assertIn('classes.jar!/META-INF/LICENSE', [p['entry'] for p in component['notices']])
            for file in (root / 'one').rglob('*'):
                if file.is_file():
                    self.assertEqual(file.read_bytes(), (root / 'two' / file.relative_to(root / 'one')).read_bytes())
            for line in (root / 'one/SHA256SUMS').read_text().splitlines():
                digest, relative = line.split('  ', 1)
                self.assertEqual(digest, hashlib.sha256((root / 'one' / relative).read_bytes()).hexdigest())

    def test_each_variant_is_verified_in_apk_and_aab_including_notice_bytes(self):
        for variant, extension in [('debug', 'apk'), ('release', 'apk'), ('release', 'aab')]:
            with self.subTest(variant=variant, extension=extension), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                module.generate(self.fixture(root, variant), root / 'evidence')
                package = root / ('package.' + extension)
                self.package(package, root / 'evidence')
                module.verify_package(package, root / 'evidence', variant, 'a' * 40)
                with self.assertRaisesRegex(ValueError, 'identity'):
                    module.verify_package(package, root / 'evidence', variant, 'b' * 40)
                with self.assertRaisesRegex(ValueError, 'identity'):
                    module.verify_package(package, root / 'evidence', variant, version='0.1.0-alpha.4')
                notice = next((root / 'evidence/license-evidence').glob('*.txt'))
                notice.write_text('changed notice')
                with self.assertRaisesRegex(ValueError, 'differs'):
                    module.verify_package(package, root / 'evidence', variant)

    def test_missing_extra_or_duplicate_packaged_evidence_is_rejected(self):
        for mutation in ('missing', 'extra', 'duplicate'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                module.generate(self.fixture(root), root / 'evidence')
                package = root / 'package.apk'
                if mutation == 'missing':
                    with zipfile.ZipFile(package, 'w') as archive:
                        archive.writestr('AndroidManifest.xml', b'fixture')
                else:
                    self.package(package, root / 'evidence')
                    with zipfile.ZipFile(package, 'a') as archive:
                        name = 'SBOM.spdx.json' if mutation == 'duplicate' else 'stale.txt'
                        with self.assertWarns(UserWarning) if mutation == 'duplicate' else nullcontext():
                            archive.writestr('assets/inventory/' + name, b'stale')
                with self.assertRaises(ValueError):
                    module.verify_package(package, root / 'evidence', 'release')

    def test_malformed_graph_pom_and_notice_cannot_publish_partial_evidence(self):
        for mutation in ('identity', 'version', 'duplicate', 'pom_identity', 'xml_entity', 'large_pom', 'notice_traversal', 'large_notice'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                graph = self.fixture(root)
                if mutation == 'identity':
                    graph['commit'] = '../escape'
                elif mutation == 'version':
                    graph['components'][0]['version'] = '1.2.3-SNAPSHOT/escape'
                elif mutation == 'duplicate':
                    graph['components'].append(copy.deepcopy(graph['components'][0]))
                elif mutation == 'pom_identity':
                    (root / 'example.pom').write_text('<project><artifactId>other</artifactId></project>')
                elif mutation == 'xml_entity':
                    (root / 'example.pom').write_text('<!DOCTYPE project [<!ENTITY private SYSTEM "file:///private">]><project>&private;</project>')
                elif mutation == 'large_pom':
                    (root / 'example.pom').write_bytes(b'x' * (module.LIMIT + 1))
                else:
                    with zipfile.ZipFile(root / 'library-1.2.3.aar', 'w') as archive:
                        archive.writestr('../NOTICE' if mutation == 'notice_traversal' else 'NOTICE', b'x' * (module.LIMIT + 1) if mutation == 'large_notice' else b'fixture')
                with self.assertRaises(ValueError):
                    module.generate(graph, root / 'output')
                self.assertFalse((root / 'output').exists())

    def test_metadata_only_missing_direct_licenses_and_stale_outputs_are_visible(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            graph = self.fixture(root)
            graph['components'][0]['archives'] = []
            (root / 'example.pom').write_text('<project><artifactId>library</artifactId><parent><licenses>not inherited</licenses></parent></project>')
            module.generate(graph, root / 'output')
            report = json.loads((root / 'output/THIRD-PARTY-INVENTORY.json').read_text())
            component = report['components'][0]
            self.assertTrue(component['metadataOnly'])
            self.assertTrue(component['missingDirectLicense'])
            self.assertTrue(component['missingNoticeFiles'])
            with self.assertRaisesRegex(ValueError, 'stale'):
                module.generate(graph, root / 'output')
