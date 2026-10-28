"""Verify required conversion features without printing private media paths."""
import os
import re
import subprocess


def main():
    executable = os.environ.get('MediaServer__FfmpegPath', 'ffmpeg')
    required = {
        '-encoders': ('libx264', 'libx265', 'libaom-av1', 'aac', 'webvtt'),
        '-filters': ('zscale', 'tonemap', 'sidedata'),
    }
    missing = []
    for option, names in required.items():
        result = subprocess.run(
            [executable, '-hide_banner', option],
            capture_output=True, text=True, timeout=20, check=True,
        )
        found = set(re.findall(r'^\s*\S+\s+(\S+)\s', result.stdout, re.MULTILINE))
        for name in names:
            if name not in found:
                missing.append(name)
    if missing:
        raise SystemExit('FFmpeg build is missing required features: ' + ', '.join(missing)
                         + '. See docs/architecture/PLAYBACK-PIPELINE.md.')
    print('FFmpeg conversion encoders and HDR filters are available.')


if __name__ == '__main__':
    main()
