#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
export MediaServer__DataDirectory="${MediaServer__DataDirectory:-${XDG_DATA_HOME:-$HOME/.local/share}/media-server}"
mkdir -p -- "$MediaServer__DataDirectory"
chmod 700 -- "$MediaServer__DataDirectory"
config="$MediaServer__DataDirectory/media-root"
if [[ -z "${MediaServer__AllowedRoots__0:-}" ]]; then
  if [[ -f "$config" ]]; then
    IFS= read -r MediaServer__AllowedRoots__0 < "$config"
  else
    read -r -p 'Absolute media folder to allow: ' MediaServer__AllowedRoots__0
    [[ "$MediaServer__AllowedRoots__0" = /* && -d "$MediaServer__AllowedRoots__0" ]] || { echo 'Choose an existing absolute directory.' >&2; exit 1; }
    printf '%s\n' "$MediaServer__AllowedRoots__0" > "$config"
  fi
  export MediaServer__AllowedRoots__0
fi
[[ -d "$MediaServer__AllowedRoots__0" ]] || { echo 'The configured media folder is unavailable.' >&2; exit 1; }
export MediaServer__FfprobePath="${MediaServer__FfprobePath:-ffprobe}"
command -v "$MediaServer__FfprobePath" >/dev/null || { echo 'Install FFmpeg/FFprobe first (Ubuntu: sudo apt install ffmpeg), or use the Docker package.' >&2; exit 1; }
export ASPNETCORE_URLS='http://127.0.0.1:5050'
echo "Open http://127.0.0.1:5050. First-run token: $MediaServer__DataDirectory/setup-token"
echo 'Leave this terminal open. Server data is stored outside the release folder.'
exec ./Api
