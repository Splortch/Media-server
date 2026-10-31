# Synthetic download fixture

`download.webm` is a one-second 160×90 VP9 WebM made from FFmpeg's `testsrc` generator, without external footage or audio. It is project-authored test material distributed under the repository's Apache-2.0 license.

Regenerate (the exact bytes may change with FFmpeg versions):

```sh
ffmpeg -v error -f lavfi -i testsrc=size=160x90:rate=12 -t 1 -c:v libvpx-vp9 -an -metadata title='Synthetic download integration fixture' -y tests/fixtures/download.webm
```

Browser integration downloads a pinned copy over production HTTPS and uses the local file's SHA-256 to verify it before real library ingestion. It does not use pirated or third-party sample films.
