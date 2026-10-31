import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('server_installer', Path(__file__).resolve().parents[1] / 'build-server-installer.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class InstallerManifestTests(unittest.TestCase):
    def test_archive_gates_run_before_installer_adds_a_second_download(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/release.yml').read_text()
        # Both archive gates require exactly one native archive, on both OSes.
        installer_step = workflow.index('      - name: Build and test the actual Windows server setup.exe')
        for gate in ('python scripts/smoke-release.py', 'python scripts/test-native-update.py'):
            self.assertLess(workflow.index(gate), installer_step)

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

    def test_desktop_shortcut_is_optional_and_uninstall_is_explicit(self):
        script = (Path(__file__).resolve().parents[2] / 'release-resources/installer/server.nsi').read_text()
        self.assertIn('Section /o "Desktop shortcut (Media Server Control)"', script)
        self.assertIn('Delete "$DESKTOP\\Media Server Control.lnk"', script)
        self.assertIn(' -Command Show', script)
        self.assertIn('${GetOptions} $0 "/DESKTOP" $1', script)

    def test_external_servers_are_observed_never_adopted(self):
        script = (Path(__file__).resolve().parents[2] / 'release-resources/windows/MediaServer.Tray.ps1').read_text()
        self.assertIn('function Test-SharingViolation', script)
        self.assertIn('if (-not $externalMode) { Start-OwnedServer }', script)
        self.assertIn('$stopButton.Enabled = -not $externalMode', script)
        self.assertIn('$restartButton.Enabled = -not $externalMode', script)
        self.assertNotIn('Get-Process Api', script)

    def test_update_requires_consent_and_waits_for_both_leases(self):
        script = (Path(__file__).resolve().parents[2] / 'release-resources/installer/server.nsi').read_text()
        self.assertIn('${GetOptions} $0 "/CLOSESERVER" $1', script)
        self.assertIn('IfSilent decline_running_update', script)
        self.assertIn('MB_YESNO|MB_ICONQUESTION', script)
        self.assertIn('Call ProbeUpdateLocks', script)
        self.assertIn('!insertmacro ProbeUpdateLease ".server.lock"', script)
        self.assertIn('!insertmacro ProbeUpdateLease "tray-host.lock"', script)
        self.assertIn('$CloseAttempts >= 100', script)
        self.assertIn('-Stop -NonInteractive', script)
        self.assertNotIn('taskkill', script.lower())
        init = script[script.index('Function .onInit'):script.index('Section "Server and browser')]
        self.assertLess(init.index('$0 > ${BUILD}'), init.index('Call PrepareRunningUpdate'))
        self.assertLess(init.index('Call PrepareRunningUpdate'), init.index('!insertmacro CheckLease'))
