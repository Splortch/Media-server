# Media Server Android phone client

A native Kotlin/Jetpack Compose phone application backed by the existing server API, with Media3 Direct Play and Android Keystore session protection.

Start with the [step-by-step installation, USB connection, development and signing guide](../../docs/clients/ANDROID.md). The server must already be configured; this app does not install it. Android 8.0+ is required.

From this directory, with JDK 17 and Android SDK 36 installed:

```sh
./gradlew :app:assembleDebug :app:assembleDebugAndroidTest :core:testDebugUnitTest :app:lint
```

On Windows use `gradlew.bat`. Debug APK: `app/build/outputs/apk/debug/app-debug.apk`. Unsigned release APK/AAB require signing before distribution; no production keys are included.

- `core`: generated shared-contract DTOs, Retrofit/OkHttp transport, origin validation and Keystore-backed session storage.
- `app`: Compose activity, lifecycle-aware playback state and Media3 player.
- `core/src/test`: JVM networking/security regressions.
- `app/src/androidTest`: installed APK exercised against the actual backend with synthetic media.

Kotlin sources use ktfmt 0.59's `--kotlinlang-style`. Keep `Models.kt` generated with `python3 scripts/generate-android-models.py`; do not manually reformat it. Do not commit SDK paths, signing keys, passwords or build outputs.

See [observed validation](../../docs/clients/VALIDATION.md). Emulator testing does not certify physical devices or all codecs. PiP, Cast, background audio, track switching and downloads remain follow-up work.
