import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('server_installer', Path(__file__).resolve().parents[1] / 'build-server-installer.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class InstallerManifestTests(unittest.TestCase):
    def test_uninstall_is_bounded_to_known_application_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            payload = Path(temporary)
            (payload / 'wwwroot').mkdir()
            (payload / 'Api.exe').write_bytes(b'explicit test fixture')
            (payload / 'wwwroot/index.html').write_text('fixture')
            manifest = module.uninstall_manifest(payload)
            self.assertIn('Delete "$INSTDIR\\app\\Api.exe"', manifest)
            self.assertIn('Delete "$INSTDIR\\app\\wwwroot\\index.html"', manifest)
            self.assertNotIn('/r', manifest)
            self.assertNotIn('$LOCALAPPDATA', manifest)
            self.assertNotIn('*', manifest)
            self.assertLess(manifest.index('Delete'), manifest.index('RMDir'))

    def test_nsis_expansion_in_filenames_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            payload = Path(temporary)
            (payload / '$escaped.dll').write_bytes(b'fixture')
            with self.assertRaises(ValueError):
                module.uninstall_manifest(payload)
