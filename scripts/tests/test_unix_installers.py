"""Installer payload/layout checks. Executables in these tests are explicit fixtures."""
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('unix_installer', Path(__file__).resolve().parents[1] / 'build-unix-server-installer.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class UnixInstallerTests(unittest.TestCase):
    def build_fixture(self, rid):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / 'artifacts/staging' / f'MediaServer-v0.2.0-alpha.42-{rid}'
            source.mkdir(parents=True)
            (source / 'Api').write_text('explicit executable fixture')
            (source / 'wwwroot').mkdir()
            (source / 'wwwroot/index.html').write_text('explicit frontend fixture')
            launcher = 'start-media-server.sh' if rid == 'linux-x64' else 'Start-MediaServer.command'
            (source / launcher).write_text('explicit launcher fixture')
            observed = []

            def run(args, check):
                self.assertTrue(check)
                payload = Path(args[args.index('--root') + 1]) if '--root' in args else Path(args[-2])
                observed.append(args)
                app = payload / ('opt/media-server' if rid == 'linux-x64' else 'Applications/MediaServer')
                self.assertTrue((app / 'Api').stat().st_mode & 0o111)
                self.assertTrue((app / launcher).stat().st_mode & 0o111)
                self.assertTrue((app / 'wwwroot/index.html').is_file())
                # No per-user DB, auto-start root service or privileged postinstall.
                self.assertFalse(any(payload.rglob('*.db')))
                self.assertFalse((payload / 'DEBIAN/postinst').exists())
                if rid == 'linux-x64':
                    control = (payload / 'DEBIAN/control').read_text()
                    self.assertIn('Version: 0.2.0~alpha.42', control)
                    self.assertIn('Depends: ffmpeg', control)
                    self.assertIn('Terminal=true', (payload / 'usr/share/applications/media-server.desktop').read_text())
                else:
                    self.assertIn('org.splortch.mediaserver.server', args)
                    self.assertIn('0.2.42', args)
                Path(args[-1]).write_text('explicit package fixture')
            with patch.dict(os.environ, SERVER_VERSION='0.2.0-alpha.42', RID=rid):
                result = module.build(root, run)
            self.assertTrue(result.is_file())
            self.assertEqual(len(observed), 1)
            self.assertTrue(result.name.endswith('.deb' if rid == 'linux-x64' else '.pkg'))

    def test_linux_layout(self):
        self.build_fixture('linux-x64')

    def test_both_mac_architectures(self):
        for rid in ('osx-x64', 'osx-arm64'):
            self.build_fixture(rid)

    def test_rejects_invalid_identities_and_missing_binary(self):
        with tempfile.TemporaryDirectory() as temporary:
            for version, rid, error in [('0.2.0-alpha.1;rm', 'linux-x64', ValueError), ('0.2.0-alpha.1', 'other', ValueError), ('0.2.0-alpha.1', 'osx-x64', RuntimeError)]:
                with patch.dict(os.environ, SERVER_VERSION=version, RID=rid), self.assertRaises(error):
                    module.build(Path(temporary))


if __name__ == '__main__':
    unittest.main()
