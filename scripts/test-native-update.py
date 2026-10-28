#!/usr/bin/env python3
"""Test the actual self-contained release: stage/playback gate, backup and copy-only restore."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('native_update',ROOT/'scripts/update-server.py')
update=importlib.util.module_from_spec(spec)
spec.loader.exec_module(update)
archives=list((ROOT/'artifacts/packages').iterdir())
if len(archives)!=1:raise RuntimeError('Expected one actual native release archive')
with tempfile.TemporaryDirectory(prefix='native-update-') as temporary:
    root=Path(temporary)/'installation'
    root.mkdir()
    with archives[0].open('rb') as stream:digest=hashlib.file_digest(stream,'sha256').hexdigest()
    version=os.environ['SERVER_VERSION']
    update.stage(root,archives[0],digest,version,'ffprobe')
    binary=update.executable(root,version)
    data=Path(temporary)/'data'
    with update.running(binary,data):pass
    before=update.data_fingerprint(data)
    update.activate(root,version,data)
    state=update.read_state(root)
    if state['version']!=version or not state['backup']:raise RuntimeError('Activation checkpoint missing')
    restored=Path(temporary)/'restored'
    update.command(binary,restored,'--restore-check',Path(state['backup']))
    update.command(binary,restored,'--restore',Path(state['backup']))
    with update.running(binary,restored):pass
    if update.data_fingerprint(data)!=before:raise RuntimeError('Original data changed during staging/restore')
    print('PASS: actual native archive, version, synthetic probe/ranges/progress, migration copy, checkpoint and restore')
