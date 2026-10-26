#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
TASK_TEMP="$(mktemp -d)"
API_PID=""
cleanup() { if [[ -n "$API_PID" ]]; then kill "$API_PID" 2>/dev/null || true; wait "$API_PID" 2>/dev/null || true; fi; rm -rf "$TASK_TEMP"; }
trap cleanup EXIT
mkdir -p "$TASK_TEMP/media"
ffmpeg -v error -f lavfi -i testsrc=size=320x180:rate=24 -f lavfi -i sine=frequency=440 -t 8 -c:v libvpx-vp9 -c:a libopus -y "$TASK_TEMP/media/sample.webm"
export E2E_DATA="$TASK_TEMP/data" E2E_MEDIA="$TASK_TEMP/media"
MediaServer__DataDirectory="$E2E_DATA" MediaServer__AllowedRoots__0="$E2E_MEDIA" dotnet run --no-build --project backend/src/Api > "$TASK_TEMP/server.log" 2>&1 &
API_PID=$!
READY=0
for i in $(seq 1 100); do if curl -fsS http://127.0.0.1:5050/health > /dev/null 2>&1; then READY=1; break; fi; if ! kill -0 "$API_PID" 2>/dev/null; then cat "$TASK_TEMP/server.log"; exit 1; fi; sleep .2; done
if [[ "$READY" != 1 ]]; then cat "$TASK_TEMP/server.log"; exit 1; fi
(cd frontend && npm run test:e2e)
