"""Update failure policy tests; fake executables here are never run as release builds."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('update_server', Path(__file__).resolve().parents[1] / 'update-server.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class UpdateTests(unittest.TestCase):
    def test_archive_traversal_and_links_fail(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / 'evil.zip'
            with zipfile.ZipFile(archive, 'w') as output:
                output.writestr('../outside', 'fixture')
            with self.assertRaises(ValueError):
                module.unpack(archive, root / 'stage')
            self.assertFalse((root / 'outside').exists())
            archive = root / 'link.tar.gz'
            with tarfile.open(archive, 'w:gz') as output:
                entry = tarfile.TarInfo('link')
                entry.type = tarfile.SYMTYPE
                entry.linkname = '../outside'
                output.addfile(entry)
            with self.assertRaises(ValueError):
                module.unpack(archive, root / 'stage')

    def test_wrong_checksum_does_not_run_candidate(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / 'build.zip'
            archive.write_text('not a real build')
            with patch.object(module, 'verify_playback') as smoke:
                with self.assertRaises(ValueError):
                    module.stage(root, archive, '0' * 64, '0.2.0-alpha.1', 'ffprobe')
                smoke.assert_not_called()

    def test_interrupted_pointer_replace_keeps_previous_installation(self):
        with tempfile.TemporaryDirectory() as temporary:
            pointer = Path(temporary) / 'current.json'
            module.atomic_json(pointer, {'version': 'old'})
            with patch.object(module.os, 'replace', side_effect=OSError('simulated interruption')):
                with self.assertRaises(OSError):
                    module.atomic_json(pointer, {'version': 'new'})
            self.assertEqual(json.loads(pointer.read_text()), {'version': 'old'})
            self.assertEqual(list(pointer.parent.glob('*.tmp')), [])

    def prepare(self, root):
        data = root / 'data'
        data.mkdir()
        (data / 'media.db').write_bytes(b'unchanged fixture database')
        for version in ['old', 'new']:
            folder = root / 'releases' / version
            folder.mkdir(parents=True)
            (folder / ('Api.exe' if module.os.name == 'nt' else 'Api')).write_text('not executed')
            (folder / 'verified.json').write_text('{}')
        module.atomic_json(root / 'current.json', {'version': 'old', 'data': str(data), 'previous': None, 'backup': None})
        return data

    def test_failed_candidate_migration_preserves_pointer_and_original_database(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data = self.prepare(root)
            before = (root / 'current.json').read_bytes()
            @contextlib.contextmanager
            def failed(*args):
                raise RuntimeError('simulated failed migration')
                yield
            with patch.object(module, 'command'), patch.object(module, 'running', failed):
                with self.assertRaises(RuntimeError):
                    module.activate(root, 'new', data)
            self.assertEqual((root / 'current.json').read_bytes(), before)
            self.assertEqual((data / 'media.db').read_bytes(), b'unchanged fixture database')

    def test_rollback_uses_a_new_data_directory_and_keeps_failed_data(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data = self.prepare(root)
            module.atomic_json(root / 'current.json', {'version': 'new', 'data': str(data), 'previous': 'old', 'backup': 'fixture.zip'})
            def restore(binary, target, action, archive):
                self.assertEqual(action, '--restore')
                self.assertNotEqual(target, data)
                target.mkdir()
                (target / 'media.db').write_text('restored fixture')
            with patch.object(module, 'command', restore), patch.object(module, 'running', lambda *args: contextlib.nullcontext('test')):
                module.rollback(root)
            state = module.read_state(root)
            self.assertEqual(state['version'], 'old')
            self.assertNotEqual(Path(state['data']), data)
            self.assertEqual((data / 'media.db').read_bytes(), b'unchanged fixture database')


if __name__ == '__main__':
    unittest.main()
