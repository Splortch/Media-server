#!/usr/bin/env python3
"""Guard the actual code fix and lockfile source; never suppress an advisory."""
import json
from pathlib import Path
import subprocess
import tomllib

ROOT = Path(__file__).resolve().parents[1]
REVISION = 'ea720152f28e293ef4362ee844ee5cc499f32d2a'
REPOSITORY = 'https://github.com/gtk-rs/gtk-rs-core'
PACKAGES = {'glib', 'glib-sys', 'gobject-sys', 'gio-sys', 'glib-macros'}
APP = ROOT / 'clients/windows/src-tauri'
TEST = ROOT / 'tests/security/glib-variant-iter'


def verify_manifest(folder):
    manifest = tomllib.loads((folder / 'Cargo.toml').read_text(encoding='utf-8'))
    patches = manifest['patch']['crates-io']
    for name in PACKAGES:
        assert patches[name] == {'git': REPOSITORY, 'rev': REVISION}, f'Unexpected patch for {name}'
    lock = tomllib.loads((folder / 'Cargo.lock').read_text(encoding='utf-8'))
    for name in PACKAGES:
        matches = [p for p in lock['package'] if p['name'] == name]
        assert len(matches) == 1, f'Duplicate/missing {name} dependency'
        assert matches[0]['source'] == f'git+{REPOSITORY}?rev={REVISION}#{REVISION}', f'Unpatched {name} source'


def main():
    for folder in (APP, TEST):
        verify_manifest(folder)
    metadata = json.loads(subprocess.check_output(
        ['cargo', 'metadata', '--locked', '--format-version', '1', '--manifest-path', str(TEST / 'Cargo.toml')],
        text=True, encoding='utf-8',
    ))
    package = next(p for p in metadata['packages'] if p['name'] == 'glib')
    source = Path(package['manifest_path']).parent / 'src/variant_iter.rs'
    code = source.read_text(encoding='utf-8')
    # Match the upstream-reviewed FFI mutation correction, not a version-number workaround.
    function = code.split("fn impl_get(&self, i: usize) -> &'a str {", 1)[1].split('let p = std::ffi::CStr::from_ptr(p);', 1)[0]
    assert 'let mut p: *mut libc::c_char = std::ptr::null_mut();' in function
    assert '&mut p,' in function
    assert '\n                &p,' not in function
    print('Application and optimized regression resolve the reviewed GLib pointer fix.')


if __name__ == '__main__':
    main()
