#!/usr/bin/env python3
"""Exercise the production Rust transport against a fresh real ASP.NET server."""
import json, os, pathlib, subprocess, tempfile, time, urllib.request
root=pathlib.Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='media-client-test-') as folder:
    folder=str(pathlib.Path(folder).resolve())
    data=pathlib.Path(folder)/'data';media=pathlib.Path(folder)/'media';media.mkdir()
    subprocess.run(['ffmpeg','-v','error','-f','lavfi','-i','testsrc=size=320x180:rate=24','-f','lavfi','-i','sine=frequency=440','-t','8','-c:v','libvpx-vp9','-c:a','libopus','-y',str(media/'sample.webm')],check=True)
    env=dict(os.environ,MediaServer__DataDirectory=str(data),MediaServer__AllowedRoots__0=str(media),ASPNETCORE_URLS='http://127.0.0.1:5053')
    with open(pathlib.Path(folder)/'server.log','w+') as log:
        # Own the API process itself. `dotnet run` starts a child apphost on Windows;
        # terminating only its CLI wrapper leaves .server.lock held during cleanup.
        assembly=root/'backend/src/Api/bin/Debug/net10.0/Api.dll'
        if not assembly.is_file():raise RuntimeError('Build the backend before running the native integration test')
        process=subprocess.Popen(['dotnet',str(assembly)],cwd=root,env=env,stdout=log,stderr=subprocess.STDOUT)
        try:
            for _ in range(150):
                try:
                    with urllib.request.urlopen('http://127.0.0.1:5053/health',timeout=1) as response:assert json.load(response)['status']=='ok'
                    break
                except OSError:
                    if process.poll() is not None:raise RuntimeError('Server failed to start')
                    time.sleep(.2)
            else:raise RuntimeError('Server startup timed out')
            env.update(MEDIA_CLIENT_TEST_SERVER='http://127.0.0.1:5053',MEDIA_CLIENT_TEST_TOKEN=(data/'setup-token').read_text(),MEDIA_CLIENT_TEST_ROOT=str(media))
            subprocess.run(['cargo','test','--manifest-path','clients/windows/native/Cargo.toml','--test','server','--','--ignored'],cwd=root,env=env,check=True)
        finally:
            if process.poll() is None:
                process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
