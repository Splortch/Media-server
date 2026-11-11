import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(os.name == 'posix' and shutil.which('xargs'), 'requires Unix source-bundle tools')
class SourceDownloadTests(unittest.TestCase):
    script = Path(__file__).resolve().parents[2] / 'docker/download-sources.sh'

    def run_downloads(self, packages, fail=''):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        (root / 'manifest').write_text(''.join(package + '\n' for package in packages))
        binary = root / 'bin'
        binary.mkdir()
        apt = binary / 'apt-get'
        apt.write_text(f'#!{sys.executable}\n' + '''
import fcntl
import json
import os
from pathlib import Path
import sys
import time

root = Path(os.environ['SOURCE_TEST_ROOT'])
def record(change):
    with (root / 'state').open('a+') as file:
        fcntl.flock(file, fcntl.LOCK_EX)
        file.seek(0)
        state = json.loads(file.read() or '{"active": 0, "maximum": 0, "calls": []}')
        state['active'] += change
        state['maximum'] = max(state['maximum'], state['active'])
        if change > 0:
            state['calls'].append({'args': sys.argv[1:], 'cwd': str(Path.cwd())})
        file.seek(0)
        file.truncate()
        json.dump(state, file)

record(1)
try:
    time.sleep(0.1)
    if sys.argv[-1] == os.environ['SOURCE_TEST_FAIL']:
        sys.exit(42)
    # Different packages can legitimately use the same archive filename.
    Path('shared.orig.tar.gz').write_text(sys.argv[-1])
    Path('source.dsc').write_text(sys.argv[-1])
finally:
    record(-1)
''')
        apt.chmod(0o755)
        result = subprocess.run(
            ['sh', str(self.script), str(root / 'manifest'), str(root / 'output with spaces')],
            env={**os.environ, 'PATH': str(binary) + os.pathsep + os.environ['PATH'],
                 'SOURCE_TEST_ROOT': str(root), 'SOURCE_TEST_FAIL': fail},
            capture_output=True, text=True, timeout=30)
        state = json.loads((root / 'state').read_text()) if (root / 'state').exists() else None
        return root, result, state

    def test_downloads_every_exact_version_with_bounded_parallelism_and_isolated_archives(self):
        packages = [f'library-{i}=2:1.0+dfsg-0ubuntu1' for i in range(12)]
        root, result, state = self.run_downloads(packages)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertGreater(state['maximum'], 1)
        self.assertLessEqual(state['maximum'], 4)
        self.assertEqual(state['active'], 0)
        self.assertCountEqual([call['args'][-1] for call in state['calls']], packages)
        for call in state['calls']:
            self.assertEqual(call['args'][:-1], ['source', '--download-only', '--yes'])
        for package in packages:
            directory = root / 'output with spaces' / package
            self.assertEqual((directory / 'source.dsc').read_text(), package)
            self.assertEqual((directory / 'shared.orig.tar.gz').read_text(), package)

    def test_any_failed_download_fails_the_bundle_and_waits_for_workers(self):
        packages = [f'library-{i}=1.0' for i in range(8)]
        _, result, state = self.run_downloads(packages, fail=packages[1])
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(state['active'], 0)
        self.assertCountEqual([call['args'][-1] for call in state['calls']], packages)

    def test_empty_manifest_does_not_request_an_unpinned_package(self):
        _, result, state = self.run_downloads([])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIsNone(state)
