# HLS conversion, hardware encoders and library removal

## Status

This alpha adds an operational, bounded HLS conversion path alongside unchanged Direct Play. It implements the software prerequisite in #4 and working portions of #5–#9. Do not close those roadmap issues solely because these APIs exist: the remaining device, network and visual validation below is required.

| Area | Implemented | Still requires validation or follow-up |
| --- | --- | --- |
| #5 adaptive playback | 360p/480p/720p ceiling ladder, hls.js automatic bandwidth selection, manual override, reuse within the same authenticated session | Real throttled-network switching/rebuffering benchmarks; progressive startup; source-resolution-specific ladders |
| #6 NVIDIA | Runtime synthetic encode probe, opt-in NVENC encoder, retry the complete conversion in software if hardware fails | Real NVIDIA device/driver tests and throughput benchmarks |
| #7 Intel/AMD | Runtime QSV, VAAPI and AMF adapters through FFmpeg; explicit fallback | Physical Linux/Windows GPU matrix, device selection beyond the default Linux render node |
| #8 formats/HDR | FFprobe dimensions, pixel format, bit depth, primaries, transfer, matrix and side metadata; explicit HDR warning; Hable HDR-to-BT.709 SDR conversion with stale HDR frame side data removed | Physical HDR display checks, visual/reference color fidelity, Dolby Vision, cross-browser decoder matrix and representative tone-map benchmarks |
| #9 tracks | Indexed audio selection through conversion, authorized embedded text-to-WebVTT and same-basename external `.vtt`, escaped React labels, caption markup stripping | Bitmap subtitle burn-in, multiple external language files, native client selection UI and screen-reader/device checks |
| Remove libraries | Administrator-only API and confirmation UI, transactional catalog cleanup, scan conflict protection, original files preserved | Existing already-open Direct Play handles can finish; subsequent media/segment requests fail |

The web player has the new controls. Desktop and Android retain their existing Direct Play behavior; their native authenticated transport must be extended before enabling HLS controls there. The server API is client-independent and additive to API v1.

## Try it

1. Install FFmpeg **and** FFprobe on the server and ensure both executables are on its PATH. Docker already includes them. Windows server setup currently bundles FFprobe; install FFmpeg separately for conversion, or set `MediaServer__FfmpegPath` to its absolute executable path. macOS: `brew install ffmpeg@7` (the maintained versioned formula includes `zscale` and `libaom`, which the regular lean formula omits); the server launcher also searches its keg-only bin directory. For terminal development, prepend `$(brew --prefix ffmpeg@7)/bin` to PATH or set `MediaServer__FfmpegPath` and `MediaServer__FfprobePath` to those executables. Ubuntu: `sudo apt install ffmpeg`.
2. Start the server normally. Existing databases migrate automatically; back up the database before updating.
3. Sign in as an administrator, add a library in Settings and scan it. Rescan existing media to populate color and track metadata. Conversion also reprobes its staged source to prevent stale HDR classification.
4. Open a video in the web client. Expand **Quality, audio and subtitles**. Direct Play is still the default.
5. Select an audio track, optional text subtitle, and encoder preference. Put an external caption alongside a video using the exact same basename, e.g. `Movie.mkv` and `Movie.vtt`.
6. Choose **Prepare HLS playback**. This version prepares the **whole video and all three renditions** before it is playable. Long movies may exceed the alpha time/disk quota; use Direct Play instead. No live/progressive transcoding is claimed.
7. When ready, play normally. Choose **Auto (network aware)** or a specific quality. All variants are H.264/AAC SDR, at most the source height; small sources are not upscaled. Captions use the selected converted WebVTT track. Direct Play returns to the original file.
8. Closing the player requests cache cancellation/removal. If that request cannot reach the server, the private cache expires after 30 minutes without access. Restart also clears stale conversion directories.

Limits: two conversion/cache jobs globally, 8 GiB source per job, six-hour source duration, 4 GiB output per job, two encoder threads and 30-minute conversion deadline. Files are sampled for output quota every 500 ms; reserve extra disk space for that interval. At most 24 GiB plus quota overshoot is required for two full staged+output jobs. Full-file staging costs time and disk but keeps FFmpeg away from attacker-replaceable library paths. There is no source modification, download authorization shortcut or public cache directory.

Run `python scripts/check-media-tools.py` to verify the selected FFmpeg build provides the conversion encoders and HDR filters used by the tests. Missing filters cause an explicit conversion error; they never trigger an un-tone-mapped SDR fallback.

## Hardware opt-in

Hardware selection is disabled by default. The operator may authorize encoders with indexed environment variables, for example:

```sh
export MediaServer__HardwareEncoders__0=h264_nvenc
# Other supported entries: h264_qsv, h264_vaapi, h264_amf
```

Restart after configuration or driver changes. `GET /api/v1/transcoding/encoders` is admin-only and reports actual synthetic H.264 encode results, not just compiled encoder names. An available result is **not** proof that every resolution, profile, input or decoder works. All paths decode and filter in software currently; these adapters accelerate encoding only. Failed hardware conversion is retried once from scratch using libx264 before publishing any manifest. A software failure remains an explicit error.

| Adapter | Platform requirements | Device validation in this change |
| --- | --- | --- |
| libx264 | FFmpeg built with libx264 and AAC; `zscale`/`tonemap` for HDR | Synthetic software encode, HLS and format tests |
| h264_nvenc | NVIDIA GPU, compatible installed driver, FFmpeg NVENC build. Containers require NVIDIA Container Toolkit, GPU allocation (`--gpus all`) and `NVIDIA_DRIVER_CAPABILITIES=video,compute,utility`; do not run privileged | No physical GPU; unavailable-device/software fallback exercised |
| h264_qsv | Compatible Intel GPU/driver and FFmpeg QSV runtime. Linux commonly requires `/dev/dri` access; Windows requires the appropriate Intel driver/runtime | Not physically validated |
| h264_vaapi | Linux VAAPI driver and `/dev/dri/renderD128`, with service-user render-node permission; container map only this device and its required group | Not physically validated |
| h264_amf | Compatible AMD Windows driver and FFmpeg build with AMF. No Linux AMF guarantee | Not physically validated |

Consult [FFmpeg's hardware documentation](https://ffmpeg.org/ffmpeg-codecs.html) and the vendor's driver compatibility documentation for your installed version. Never solve missing device permissions with a privileged container or disable TLS. macOS uses software conversion; VideoToolbox is a separate future adapter.

## API and security

The generated contract lives in `shared/api/openapi-v1.json` and `shared/api/src/generated.ts`.

- `GET /api/v1/media/{id}/playback-options`: color metadata, tracks, external-caption presence, allowed encoders and conversion limits.
- `POST /api/v1/media/{id}/playback-sessions`: `{ audioTrack: number|null, subtitleTrack: number|null, encoder: string }`. Subtitle `-1` selects the same-basename external WebVTT file. Returns 202 with conversion state; 409 when capacity is occupied.
- `GET /api/v1/playback-sessions/{session}`: preparing/ready/failed state, actual encoder, encode seconds, manifest URL.
- `GET|HEAD /api/v1/playback-sessions/{session}/files/{file}`: private, no-store manifest/variant/segment/caption bytes. Supports byte ranges. Every request revalidates the login, user, session ownership and current media ACL. A guessed session ID is insufficient; another login of the same account cannot reuse it.
- `DELETE /api/v1/playback-sessions/{session}`: cancel, wait for process termination, remove generated cache. CSRF protection applies to mutations, just as to existing endpoints.
- `DELETE /api/libraries/{id}` and `/api/v1/libraries/{id}`: admin only. 204 removed, 404 absent, 409 scan active. Same scheduling lock prevents a scan from being inserted during deletion. Database transaction removes library, media, jobs/issues, permissions, history, progress, reports and library-bound smart collections. No filesystem delete receives a library path. Watcher reconciliation disposes removed-library watchers.

FFmpeg receives server-created paths and fixed argument lists, never client-supplied filters or executable names. Sources/captions are opened through the existing no-symlink path policy and staged from pinned handles; FFmpeg input protocols and container demuxers are restricted. Output filenames use a closed allowlist. Process output is drained, never returned to clients or logged with private paths. Cache URLs contain no tokens. Browser HLS uses authenticated same-origin requests and MSE blob URLs; CSP permits media blobs without relaxing scripts or external origins. Captions render through native WebVTT, not HTML; tags/style/region blocks are stripped. ASS positioning/styling is intentionally lost in text conversion.

Sessions are intentionally reused only within the same login and matching source fingerprint/selection. Cross-user reuse would need a separate content-addressed cache with independently checked leases, and is not part of this alpha.

## Validation and remaining work

Automated tests cover transactional removal/source preservation, active scan rejection, unauthorized access, unknown encoder/filter rejection, real synthetic FFmpeg conversion, all three complete HLS variants and transport segments, session-isolated cache reuse/removal, external subtitle sanitation, HDR profile policy, synthetic AV1/HEVC probing and SDR conversion. Frontend tests cover track labels, unavailable bitmap subtitle choices, encoder authorization and removal confirmation/error handling. The browser suite includes actual HLS frame rendering and quality selection against the real backend.

A passing build is not a physical GPU, HDR color-fidelity, screen-reader or native-device test. Keep #5–#9 open for those remaining criteria. Tone-map timing reported by conversion is wall-clock end-to-end time, not a representative hardware benchmark. Dolby Vision and automatic HDR-display passthrough negotiation are not implemented. Remote use still requires HTTPS and the existing deployment guidance.
