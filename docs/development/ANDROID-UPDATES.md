# Android dependency compatibility

## SDK 37 toolchain

The Android modules migrate together, without disabling compiler metadata, AAR
metadata, dependency-version validation or lint. The selected versions are:

| Component | Version / policy |
| --- | --- |
| JDK | 17 in CI; Java/Kotlin bytecode target 17 |
| Gradle | 9.7.0, distribution SHA-256 pinned in the wrapper |
| Android application/library plugins | Both AGP 9.1.1 |
| Kotlin compiler, Compose compiler and serialization compiler plugins | All 2.4.21 |
| Compile SDK / SDK package | API 37 / `platforms;android-37.0` |
| Build tools | 36.0.0 |
| Target SDK / minimum SDK | 36 / 26; unchanged by this migration |
| Compose BOM (app and instrumentation tests) | 2026.09.00 |
| AndroidX core-ktx | 1.19.1 |
| Kotlinx serialization / coroutines | 1.12.0 / 1.11.0 |

[AGP 9.1 compatibility](https://developer.android.com/build/releases/agp-9-1-0-release-notes)
supports API 37.0, requires at least Gradle 9.3.1 and uses JDK 17/build-tools 36.0.0.
[Kotlin's compatibility table](https://kotlinlang.org/docs/gradle-configure-project.html)
lists Gradle 9.7.0 and AGP 9.3.1 as the upper fully supported versions for Kotlin
2.4.21. The previous Gradle 9.9 wrapper was outside that published Gradle range;
the selected wrapper is inside the intersection of both compatibility tables.

Both modules use [AGP's built-in Kotlin](https://developer.android.com/build/migrate-to-built-in-kotlin)
and the new Android DSL. The standalone `kotlin("android")` plugin and temporary
`android.builtInKotlin=false` / `android.newDsl=false` switches are removed. An
explicit Kotlin Gradle plugin classpath selects compiler 2.4.21 above AGP's default,
as documented in [AGP's runtime dependency guidance](https://developer.android.com/build/releases/agp-9-0-0-release-notes#runtime-dependency-on-kgp).
Compose and serialization compiler plugins use the same version. JVM target 17 is
preserved in both modules.

## Dependabot policy

The old ceilings for AGP 8, Kotlin 2.2, SDK 36-era Compose/core and Kotlinx are
removed. AGP, Kotlin/compiler plugins, Kotlinx, Compose and core-ktx now share one
`android-platform` update group. Other AndroidX packages remain grouped separately.
This prevents independent application/library or compiler/library proposals from
silently diverging; it does not prove a future proposal is compatible.

Every proposed update still needs explicit review of the Gradle wrapper checksum,
JDK, compiler and SDK compatibility tables. Dependabot's Gradle group does not
automatically migrate the wrapper or SDK workflow. Keep version/metadata checks
enabled and require the full build/device checks before merging. A future target
SDK increase requires a separate Android behavior review.

## Validation and remaining acceptance (#69)

The build job runs debug and instrumentation APK assembly, JVM tests, unsigned
release APK/AAB assembly and lint. Generated API models and release safety checks
run before compilation. Release outputs retain the shared version-code range,
ephemeral debug signing and unsigned-release labeling.

The device matrix runs the same actual-backend playback/resume, progress conflict,
activity recreation, logout/revocation and Keystore checks on API 35 and API 37.0.
Both matrix jobs must succeed before packaging for the unified release publisher.
The new API uses the SDK repository's `37.0` package name, rather than guessing
`android-37`. Successful compilation is not evidence of installed-device behavior.

Record actual build/device results in the migration PR. Physical-phone testing,
production signing and a published-release acceptance run remain separate evidence;
do not close #69 solely because the build files or device matrix have changed.

## Historical failures

- #67/#68 changed one AGP plugin independently; AGP 9.5 required a newer wrapper.
- #64 selected Compose/core requiring SDK 37 and AGP 9.1+ against the former SDK 36 build.
- #65 selected Kotlinx libraries with Kotlin 2.4 metadata against a Kotlin 2.2 compiler.

Those errors motivated coordinated migration. They are not reasons to suppress
metadata validation, remove tests or dismiss dependency vulnerability alerts.
