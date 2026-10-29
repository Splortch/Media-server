# Playback validation

Use synthetic media to exercise the real conversion pipeline without importing personal media. FFmpeg and FFprobe must be on PATH (or set `MediaServer__FfmpegPath` and `MediaServer__FfprobePath`). Install the SDK pinned by `global.json`.

```sh
dotnet restore backend/tools/PlaybackBenchmark --locked-mode
dotnet run --project backend/tools/PlaybackBenchmark -- --scenario sdr --output artifacts/sdr.json
dotnet run --project backend/tools/PlaybackBenchmark -- --scenario hevc-hdr --output artifacts/hevc-hdr.json
dotnet run --project backend/tools/PlaybackBenchmark -- --scenario av1-hdr --output artifacts/av1-hdr.json
```

The tool generates six seconds of 720p video with synthesized audio in its own temporary directory. `--seconds` accepts 2–60 and `--height` accepts 360, 720 or 1080. It prepares all three HLS renditions through production `PlaybackSessions`, probes the results for H.264/AAC and SDR output, and checks authorized cache reuse and independent session release. It never opens or alters a configured media library.

JSON records the platform, source, requested and actual encoder, runtime capability checks, conversion time, output metadata, segment bytes and reuse time. `nominalAggregateFramesPerSecond` includes source staging, probing and all rendition conversions; it is not a raw encoder benchmark. Complete-VOD preparation time is not playback startup time for progressive streaming. These synthetic fixtures check metadata and pipeline behavior; they do not establish visual HDR fidelity or real-film performance.

## Hardware configuration

Hardware encoding is opt-in. Enable the desired encoder in `MediaServer__HardwareEncoders__0` and preserve software fallback with a second entry for `libx264`. Bind devices on the server, never through a client request:

| Variable | Values | Purpose |
| --- | --- | --- |
| `MediaServer__NvencGpu` | -1 (automatic), or 0–31 | NVENC adapter index |
| `MediaServer__VaapiDevice` | `/dev/dri/renderD128` (default), or another render node | VAAPI device |
| `MediaServer__QsvDevice` | Unset (automatic), Windows adapter 0–31, or Linux render-node path | QSV child device |

Both the runtime capability probe and actual conversion use the same binding. Invalid settings fail startup. A valid configuration does not prove the driver, device permissions or source codec is supported: runtime synthetic probing and per-source software fallback still apply.

For Linux VAAPI/QSV, map only the selected render node into the container and grant the server account its render-group access. Do not use a privileged container. NVIDIA containers require the NVIDIA Container Toolkit and compatible host drivers; device index refers to devices visible inside the container. Windows requires compatible vendor drivers and an FFmpeg build containing the selected encoder. AMF currently uses FFmpeg's default adapter selection. Hardware decoding is not implemented by these bindings.

Run an explicit hardware check:

```sh
dotnet run --project backend/tools/PlaybackBenchmark -- --encoder h264_nvenc --require-hardware --scenario hevc-hdr --output artifacts/nvenc.json
```

Exit 0 means the requested encoder produced valid output. Exit 2 means hardware was unavailable or playback fell back to another encoder. Exit 1 indicates validation failure. Without `--require-hardware`, the normal production software fallback is allowed and reported.

The Playback validation workflow runs software checks on hosted Linux runners. Hardware validation is manual, restricted to `main`, and uses an operator-provisioned self-hosted runner labelled `media-validation`. Provision .NET, FFmpeg/FFprobe, the GPU driver and optional device environment settings on that runner. Untrusted pull requests never execute on it. No signing credentials or repository write token are required.

## Evidence and remaining validation

| Coverage | Current evidence |
| --- | --- |
| Software SDR, HEVC HDR and AV1 HDR conversion | Automated synthetic pipeline checks; JSON artifact from each workflow run |
| Unsupported hardware and software fallback | Automated capability/pipeline tests |
| Native HLS authorization, ranges, logout and malicious manifests | Rust real-backend integration tests |
| Android HLS rendering and quality changes | Emulator instrumentation against the real backend; not physical-device evidence |
| Physical NVENC, QSV, VAAPI and AMF devices | Not tested in this development environment; run the explicit hardware workflow |
| Throttled-network ABR/rebuffer measurements | Still pending |
| HDR visual fidelity and physical display behavior | Still pending |
| Bitmap subtitle burn-in and multiple external text files | Still pending |

Do not interpret a successful software workflow as proof of GPU support, device codec compatibility, HDR display output or a successful visual-quality review.
