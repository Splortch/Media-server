# Early alpha distribution notes

Run `Api.exe` on Windows or `Api` on Linux from the extracted directory. The browser UI is bundled under `wwwroot`; use http://127.0.0.1:5050. Install FFprobe separately on PATH or configure `MediaServer__FfprobePath`. Set `MediaServer__DataDirectory` and `MediaServer__AllowedRoots__0` before starting. Read the local setup-token file and create the administrator in the browser.

These are self-contained .NET builds, not source archives. FFmpeg/FFprobe is not bundled into native release archives. No stable release is authorized by the build workflow; it creates draft prereleases only when manually dispatched with an alpha tag.

Dependency lock files pin application packages, but hosted SDK/container tags and OS packages still change. Builds are deterministic at the compiler level, not promised bit-for-bit reproducible across toolchains. A future release task must add SBOMs, artifact hashes, signing, base-image digests and reproducibility verification.

Apache-2.0 applies to project code. NuGet/npm packages retain their licenses. Docker includes Ubuntu's FFmpeg/FFprobe package, whose configured components may be GPL-licensed; an aggregate image is not wholly Apache-2.0. Before redistributing any image, review `ffmpeg -L`/`ffprobe -L`, preserve applicable notices and satisfy corresponding-source obligations for included GPL components (including build configuration and patches). Do not bundle proprietary codec libraries without explicit review. Native builds intentionally use a separately installed FFprobe executable.
