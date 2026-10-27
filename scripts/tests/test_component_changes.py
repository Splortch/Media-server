"""Regression checks for the cross-platform CI change detector."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'component-changes.py'
spec = importlib.util.spec_from_file_location('component_changes', SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ComponentChangesTests(unittest.TestCase):
    def test_emoji_pr_event_with_windows_ansi_default(self):
        # U+270F includes 0x8f in UTF-8, which crashed the Windows cp1252 reader.
        # Subprocess forces legacy Windows text decoding even on UTF-8 Linux hosts.
        with tempfile.TemporaryDirectory() as folder:
            event = Path(folder) / 'event.json'
            output = Path(folder) / 'output.txt'
            event.write_text(json.dumps({'body': 'Update notes ✏️', 'before': '0' * 40}, ensure_ascii=False), encoding='utf-8')
            env = dict(os.environ, GITHUB_EVENT_PATH=str(event), GITHUB_OUTPUT=str(output), GITHUB_SHA='head', PYTHONUTF8='0')
            code = """import builtins, io, runpy, sys
original_open = builtins.open
def ansi_open(file, mode='r', buffering=-1, encoding=None, errors=None, newline=None, closefd=True, opener=None):
    if 'b' not in mode and encoding is None:
        encoding = 'cp1252'
    return original_open(file, mode, buffering, encoding, errors, newline, closefd, opener)
builtins.open = io.open = ansi_open
sys.argv = [sys.argv[1], 'windows']
runpy.run_path(sys.argv[0], run_name='__main__')
"""
            subprocess.run([sys.executable, '-c', code, str(SCRIPT)], env=env, check=True, capture_output=True)
            self.assertEqual(output.read_text(encoding='utf-8'), 'changed=true\n')

    def result(self, paths, code=0):
        return subprocess.CompletedProcess([], code, '\0'.join(paths), '')

    def test_utf8_git_output_and_affected_component(self):
        def run(args, **options):
            self.assertEqual(options['encoding'], 'utf-8')
            self.assertEqual(args[-2:], ['base', 'head'])
            return self.result(['frontend/src/电影.tsx'])
        event = {'pull_request': {'base': {'sha': 'base'}, 'head': {'sha': 'head'}}}
        self.assertTrue(module.component_changed('web', event, 'merge', run))
        self.assertTrue(module.component_changed('windows', event, 'merge', run))
        self.assertFalse(module.component_changed('backend', event, 'merge', run))

    def test_recovery_regression_change_runs_backend(self):
        self.assertTrue(module.component_changed('backend', {'before': 'base'}, 'head', lambda *a, **k: self.result(['scripts/test-credential-recovery.py'])))

    def test_missing_history_runs_checks(self):
        self.assertTrue(module.component_changed('backend', {'before': 'base'}, 'head', lambda *a, **k: self.result([], 128)))

    def test_shared_policy_change_runs_all_components(self):
        for kind in module.PREFIXES:
            self.assertTrue(module.component_changed(kind, {'before': 'base'}, 'head', lambda *a, **k: self.result(['.gitattributes'])))

    def test_docs_only_can_skip_component_builds(self):
        for kind in module.PREFIXES:
            self.assertFalse(module.component_changed(kind, {'before': 'base'}, 'head', lambda *a, **k: self.result(['docs/README.md'])))

    def test_no_comparison_runs_checks(self):
        self.assertTrue(module.component_changed('backend', {}, 'head'))


if __name__ == '__main__':
    unittest.main()

