# Android dependency compatibility

The alpha uses Android Gradle Plugin (AGP) 8.13.2, Gradle 8.13, JDK 17, Kotlin 2.2.21 and compile/target SDK 36. Application/library plugins must have the same version; all Kotlin compiler/Compose/serialization plugins must also match. A green version-update PR is required before merging; no compiler-metadata checks, AAR checks or tests are suppressed.

## Why the October update PRs failed

- #67/#68 changed one AGP plugin to 9.5.0 independently. CI reports that version needs Gradle 9.6, while the wrapper is 8.13. A platform migration must also check built-in Kotlin DSL, JDK, R8, packaging and both modules.
- #64 selected Compose BOM 2026.09.00 and core-ktx 1.19.1. Their resolved AARs require SDK 37 and AGP 9.1+; the project builds SDK 36 with AGP 8.13.2.
- #65 selected serialization 1.12.0 and coroutines 1.11.0, resolving Kotlin 2.4 metadata which the current compiler cannot consume. Serialization 1.9.0 targets Kotlin 2.2 and is a compatible, separately validated update candidate.

## Dependabot policy

AGP plugins are grouped to avoid split application/library versions. Kotlin libraries/plugins remain grouped. Temporary Maven version ceilings keep routine updates inside the current platform family: AGP <9, Kotlin compiler <2.3, serialization <1.10, coroutines <1.11, Compose BOM <2026 and core-ktx <1.18. The Compose/core bounds are conservative review boundaries for the validated SDK line, not a claim that every excluded version is vulnerable or incompatible.

These ceilings defer version proposals; they do not dismiss vulnerability alerts or prove a dependency safe. A required security fix beyond a ceiling needs a coordinated migration PR and removal/adjustment of the relevant rule. Other dependencies and compatible patch releases remain eligible for updates.

## Coordinated SDK 37 migration

1. Select a compatible JDK/Gradle/AGP/Kotlin set using official release compatibility tables; update the checksum-pinned wrapper and both modules together.
2. Migrate AGP/Kotlin build DSL deliberately, without disabling metadata/version verification.
3. Update compile SDK and CI-installed SDK. Review target-SDK behavior separately from compile SDK and retain the supported minimum Android version unless justified.
4. Update Compose/core/Kotlinx dependencies together, then remove ceilings whose compatibility is now covered.
5. Build debug/test/release APK and unsigned AAB; run lint, JVM tests, generated-model checks and the actual-backend emulator test. Exercise authenticated playback, resume, conflicts, recreation, logout/revocation and Keystore storage.
6. Test both the existing supported emulator and the new API level; verify signing/version-code and independently published Android release assets. Record physical-device testing separately.

The web end-to-end timeout seen on #64 was independent of Android dependencies: the test navigated away immediately after starting collection creation, allowing its library GET to race the POST. Wait for the server-confirmed collection in the UI before navigating, rather than increasing timeouts or retries.
