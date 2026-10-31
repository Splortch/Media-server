import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

spec = importlib.util.spec_from_file_location('macos_packaging', Path(__file__).resolve().parents[1] / 'build-macos-client.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class MacPackagingTests(unittest.TestCase):
    def test_retries_disk_service_contention_then_requires_success(self):
        run = Mock(side_effect=[SimpleNamespace(returncode=1, stdout='hdiutil: create failed - Resource busy'), SimpleNamespace(returncode=0, stdout='Packaged')])
        wait = Mock()
        self.assertEqual(module.build(['fixture'], run, wait), 0)
        self.assertEqual(run.call_count, 2)
        wait.assert_called_once_with(5)

    def test_does_not_retry_compilation_or_other_packaging_errors(self):
        for error in ('Rust compilation failed', 'hdiutil: create failed - Permission denied'):
            run = Mock(return_value=SimpleNamespace(returncode=7, stdout=error))
            wait = Mock()
            self.assertEqual(module.build(['fixture'], run, wait), 7)
            self.assertEqual(run.call_count, 1)
            wait.assert_not_called()

    def test_persistent_contention_fails_after_three_attempts(self):
        run = Mock(return_value=SimpleNamespace(returncode=1, stdout='hdiutil: create failed - Resource busy'))
        wait = Mock()
        self.assertEqual(module.build(['fixture'], run, wait), 1)
        self.assertEqual(run.call_count, 3)
        self.assertEqual([call.args[0] for call in wait.call_args_list], [5, 10])
