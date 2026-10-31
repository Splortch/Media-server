#!/usr/bin/env python3
"""Exercise the fixed-signature bridge from an actual published macOS server."""
import ctypes
from pathlib import Path
import os
import stat
import sys
import tempfile

if sys.platform != 'darwin':
    raise SystemExit('This check requires macOS')
bridge = ctypes.CDLL(str(Path(sys.argv[1]).resolve()), use_errno=True)
create = bridge.MediaServerOpenAt
create.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_int]
create.restype = ctypes.c_int
with tempfile.TemporaryDirectory() as directory:
    parent = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
    try:
        fd = create(parent, b'fixture.part', os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        if fd < 0:
            raise OSError(ctypes.get_errno(), 'Published download bridge could not create a file')
        try:
            assert stat.S_IMODE(os.fstat(fd).st_mode) == 0o600
            os.write(fd, b'published bridge fixture')
        finally:
            os.close(fd)
        assert (Path(directory) / 'fixture.part').read_bytes() == b'published bridge fixture'
    finally:
        os.close(parent)
print('Published macOS download bridge and private creation mode verified')
