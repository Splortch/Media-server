# Playback acceptance matrix

Implementation: [PR #83](https://github.com/Splortch/Media-server/pull/83). Repeatable browser validation: [PR #84](https://github.com/Splortch/Media-server/pull/84). Issues [#5](https://github.com/Splortch/Media-server/issues/5)–[#9](https://github.com/Splortch/Media-server/issues/9) track acceptance, including tests that need physical equipment.

## Verified automated evidence

| Area | Evidence | What it does not prove |
| --- | --- | --- |
| Adaptive quality (#5) | Real server; Chromium throughput shaping, quality switch, rebuffer counters; cache/authorization concurrency tests | Every real network or device |
| NVENC (#6) | Runtime synthetic encode, selected GPU binding, unavailable-device fallback tests; hardware workflow rejects fallback | Physical NVIDIA GPU throughput/driver compatibility |
| Intel/AMD (#7) | Shared runtime probes; QSV/VAAPI device binding; AMF default-device path; strict configuration tests | Physical Intel/AMD driver/device compatibility |
| HDR conversion (#8) | PQ/HLG neutral-ramp numerical regression; SDR/HEVC-HDR/AV1-HDR production benchmarks; output metadata checks | Reference-color fidelity, gamut accuracy or HDR display output |
| Captions/audio (#9) | Authorized delivery, language labels, real cue loading, cache edits, sanitization; keyboard selection on Chromium/Firefox; selection semantics, focus and axe checks on each browser; conversion benchmark checks timing and source preservation | Spoken output and interaction with a physical screen reader |

The neutral-ramp test checks black preservation, neutral channels and monotonic luminance. Its thresholds deliberately tolerate rounding between FFmpeg builds. It is not a perceptual color reference and must not be described as display certification. Conversions explicitly produce SDR; Dolby Vision and bitmap subtitle burn-in remain unsupported. ASS styling is not preserved when converted to text WebVTT.

## Browser matrix

Observed in [run 40887908949](https://github.com/Splortch/Media-server/actions/runs/40887908949), commit `20fe1cb`:

| Platform/browser | H.264 | 10-bit HEVC | 10-bit AV1 |
| --- | --- | --- | --- |
| Hosted Linux Chromium | Decoded | Explicitly unsupported | Decoded |
| Hosted Linux Firefox | Decoded | Decoded | Decoded |
| Hosted macOS WebKit | Decoded | Decoded | Explicitly unsupported |

Each codec probe runs twice in independent contexts and requires presented/decoded frames plus advancing time. Both platform jobs also passed fresh reruns. Inspect the JSON artifacts for exact browser versions. The earlier Linux WebKit result in #8 is historical, not Safari evidence. Linux WebKitGTK subsequently stalled and is no longer the Safari compatibility gate.

## Physical hardware matrix

| Encoder | Actual device tested in this development environment | Status |
| --- | --- | --- |
| NVIDIA NVENC | None | Pending |
| Intel QSV | None | Pending |
| Linux VAAPI (Intel/AMD) | None | Pending |
| Windows AMD AMF | None | Pending |

No hosted software build substitutes for a device test. To fill a row:

1. Provision a trusted runner with the `media-validation` label, Python 3, pinned .NET SDK, full FFmpeg/FFprobe and vendor driver. Configure device access as described in [PLAYBACK-VALIDATION.md](PLAYBACK-VALIDATION.md).
2. From main, manually dispatch **Playback validation**, enable hardware, and choose the encoder. Pull requests cannot invoke the hardware runner.
3. Run all three scenarios. The benchmark must report the requested encoder as actual; `--require-hardware` rejects unavailable hardware or software fallback.
4. Retain the JSON and generated `hardware-matrix.md` artifacts. Record GPU model, driver version, OS, FFmpeg version, device setting, commit, workflow URL and test date in an issue update. Never include private media paths or credentials.
5. Repeat on the applicable Windows/Linux paths. An unavailable device is an untested/failed row, never a pass.

For local evidence, run the commands in PLAYBACK-VALIDATION.md, then:

```sh
python3 scripts/playback-evidence.py artifacts/hardware-*.json --require-hardware --output artifacts/hardware-matrix.md
```

Exit 0 requires successful runtime capability detection and actual hardware encoding in every supplied report. Timing measures complete-VOD preparation, not hardware decode speed or progressive playback startup.

## Screen-reader and display acceptance

Use synthetic fixtures and a test account; no personal media is required. Record platform, client/browser version, assistive technology/version and commit alongside the results.

1. Test NVDA with Windows/web or desktop, VoiceOver with macOS WebKit, and TalkBack with Android on actual supported devices.
2. Without a mouse, reach and expand **Quality, audio and subtitles**. Confirm Audio track, Subtitles and Encoder preference have spoken labels, values and instructions.
3. Select audio and a text subtitle language, activate **Prepare HLS playback**, and confirm the conversion status/error is announced. Play, pause and seek; verify the chosen audio and visible caption text/timing.
4. Change languages, return to captions Off, then switch Direct Play/HLS. Confirm focus remains usable, labels remain unique and unavailable bitmap options are explained. Verify expired-session errors can be read and dismissed through normal navigation.
5. For HDR, compare a legally distributable reference clip on a calibrated HDR display against explicit SDR conversion. Check dark detail, highlights, skin/neutral colors, gamut clipping, caption timing and audio alignment. Record display mode and calibration/reference source. A synthetic neutral ramp or an HDR metadata flag alone is insufficient.

Attach results and remaining failures to #8/#9. Leave physical acceptance unchecked until these observations exist. Automated keyboard/axe tests cover semantics and operation; they do not run NVDA, VoiceOver or TalkBack.

macOS native popup selection does not commit virtual keyboard choices through this Playwright build. Its automated test uses `selectOption` and records `keyboardSelection: false`; manual VoiceOver/native keyboard acceptance remains required. CI enables macOS keyboard navigation for focus traversal.
