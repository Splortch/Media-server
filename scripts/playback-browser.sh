#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
TASK_TEMP="$(mktemp -d)"
API_PID=""
cleanup() {
  if [[ -n "$API_PID" ]]; then kill "$API_PID" 2>/dev/null || true; wait "$API_PID" 2>/dev/null || true; fi
  rm -rf "$TASK_TEMP"
}
trap cleanup EXIT
mkdir -p "$TASK_TEMP/media" frontend/benchmark-results
ffmpeg -v error -filter_threads 1 -f lavfi -i testsrc2=size=1280x720:rate=24 -f lavfi -i sine=frequency=440 -t 60 -c:v libx264 -preset ultrafast -threads 2 -c:a aac -movflags +faststart "$TASK_TEMP/media/bandwidth.mp4"
# Codec probes use equivalent short fixtures; the 60-second source is only for ABR.
ffmpeg -v error -filter_threads 1 -f lavfi -i testsrc2=size=640x360:rate=24 -f lavfi -i sine=frequency=440 -t 2 -c:v libx264 -preset ultrafast -profile:v baseline -level:v 3.0 -pix_fmt yuv420p -threads 2 -c:a aac -movflags +faststart "$TASK_TEMP/media/h264.mp4"
for codec in hevc av1; do
  encoder=libx265
  options=(-preset ultrafast -x265-params pools=none:frame-threads=1 -tag:v hvc1)
  if [[ "$codec" == av1 ]]; then encoder=libaom-av1; options=(-cpu-used 8 -row-mt 1); fi
  ffmpeg -v error -filter_threads 1 -f lavfi -i testsrc2=size=320x180:rate=24 -t 2 -vf 'format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=limited' -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -c:v "$encoder" -threads 2 "${options[@]}" -movflags +faststart "$TASK_TEMP/media/$codec.mp4"
done
printf '1\n00:00:00,000 --> 00:00:20,000\n<b>Swedish synthetic caption</b>\n' > "$TASK_TEMP/media/bandwidth.swe.srt"
printf 'WEBVTT\n\n00:00:00.000 --> 00:00:20.000\nEnglish synthetic caption\n' > "$TASK_TEMP/media/bandwidth.eng.vtt"
export BENCHMARK_DATA="$TASK_TEMP/data" BENCHMARK_MEDIA="$TASK_TEMP/media"
MediaServer__DataDirectory="$BENCHMARK_DATA" MediaServer__AllowedRoots__0="$BENCHMARK_MEDIA" ASPNETCORE_URLS=http://127.0.0.1:5050 dotnet backend/src/Api/bin/Debug/net10.0/Api.dll > "$TASK_TEMP/server.log" 2>&1 &
API_PID=$!
READY=0
for i in $(seq 1 100); do
  if curl -fsS http://127.0.0.1:5050/health > /dev/null 2>&1; then READY=1; break; fi
  if ! kill -0 "$API_PID" 2>/dev/null; then cat "$TASK_TEMP/server.log"; exit 1; fi
  sleep .2
done
if [[ "$READY" != 1 ]]; then cat "$TASK_TEMP/server.log"; exit 1; fi
(cd frontend && npx playwright test --config playwright.benchmark.config.ts "$@")
