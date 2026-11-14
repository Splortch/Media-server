# Media Server Android phone client

A native Kotlin/Jetpack Compose phone application backed by the existing server API, with Media3 Direct Play and Android Keystore session protection.

Start with the [step-by-step installation, USB connection, development and signing guide](../../docs/clients/ANDROID.md). The server must already be configured; this app does not install it. Android 8.0+ is required.

From this directory, with JDK 17, Python 3.11+, Android SDK platform `android-37.0` and build-tools `36.0.0` installed:

```sh
./gradlew :app:assembleDebug :app:assembleDebugAndroidTest :core:testDebugUnitTest :app:lint
```

On Windows use `gradlew.bat`. Debug APK: `app/build/outputs/apk/debug/app-debug.apk`. Unsigned release APK/AAB require signing before distribution; no production keys are included.

Every application variant generates and embeds its own dependency inventory before
assets are merged. If Python is named `python` on Windows, pass
`-PinventoryPython=python`. Evidence copies are in
`app/build/reports/dependencyInventory/debug` and `release`; see the
[distribution evidence guide](../../docs/deployment/DISTRIBUTION-EVIDENCE.md).

The checksum-pinned Gradle 9.9.0 wrapper, AGP 9.5.1 and Kotlin 2.4.21 use AGP's
built-in Kotlin and new DSL. Compose and serialization compiler plugins match
the Kotlin compiler. Compile SDK 37 is separate from the reviewed target SDK 36;
minimum Android remains API 26. See the [toolchain policy](../../docs/development/ANDROID-UPDATES.md).

Application and test compile/runtime dependency versions are strictly locked in
`app/gradle.lockfile` and `core/gradle.lockfile`. Check all registered classpaths
without changing them:

```sh
./gradlew :app:resolveDependencyLocks :core:resolveDependencyLocks
```

After an intentional dependency change, regenerate both files with
`./gradlew :app:resolveDependencyLocks :core:resolveDependencyLocks --write-locks`,
review the complete diff and commit both lockfiles with the build-file change.
For a targeted update, use the same tasks with
`--update-locks group:artifact` instead of `--write-locks`; Gradle can also update
related constraints, so review both diffs. Ordinary builds and CI never write
lockfiles. These locks pin module versions; they do not authenticate downloaded
bytes or cover Gradle plugins/compiler tooling. See the distribution evidence
guide for the inventory's observed archive checksums and remaining limitations.

- `core`: generated shared-contract DTOs, Retrofit/OkHttp transport, origin validation and Keystore-backed session storage.
- `app`: Compose activity, lifecycle-aware playback state and Media3 player.
- `core/src/test`: JVM networking/security regressions.
- `app/src/androidTest`: installed APK exercised against the actual backend with synthetic media.

Kotlin sources use ktfmt 0.59's `--kotlinlang-style`. Keep `Models.kt` generated with `python3 scripts/generate-android-models.py`; do not manually reformat it. Do not commit SDK paths, signing keys, passwords or build outputs.

See [observed validation](../../docs/clients/VALIDATION.md). Emulator testing does not certify physical devices or all codecs. PiP, Cast, background audio, track switching and downloads remain follow-up work.
