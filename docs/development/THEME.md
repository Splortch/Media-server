# Lime Soda visual theme

The user-supplied palette is shared by the web, desktop and native Android UI:

| Color | Hex | Role |
| --- | --- | --- |
| Cyan | `#00B8FF` | Cool end of action gradients and blue card highlights |
| Mint | `#2BE0A0` | Primary accent, focus, active controls and gradient midpoint |
| Lime | `#C6FF3D` | Warm card highlights and gradient transition |
| Yellow | `#FFE53B` | Warm gradient endpoint and hero glow |

Web/desktop tokens and gradients live in `frontend/src/styles/app.css`.
Desktop light/system-light surface tokens live in `clients/windows/src/desktop.css`.
Android shares named colors, Material colors and the reusable `GradientButton`
in `clients/android/app/src/main/java/org/splortch/mediaserver/android/MediaTheme.kt`.

Keep primary button labels dark (`#061A23`) over the bright action gradient.
Normal and hover gradients keep that same foreground; do not inherit a dark
secondary-button hover background with dark text. The four-color action gradient
has a sampled minimum contrast above 7.8:1 with this foreground in sRGB. Sampling
is a design check, not a substitute for rendered browser/device review.

Reading and playback surfaces remain dark navy, with quiet secondary actions
and distinct red destructive actions. Desktop light mode uses pale blue/white
surfaces and dark teal accents while keeping the same bright primary gradient.
The mobile home banner uses a stronger dark scrim behind text.

Frosted hero bands, artwork placeholders and action gradients use native CSS or
Compose brushes, without new image/animation dependencies. Gradients are static;
existing restrained entrance/hover motion respects reduced-motion preferences.
Text remains fully opaque throughout entrance animations. Keep keyboard focus,
forced-colors behavior and disabled-control semantics intact.

Run web/desktop builds, component tests, lint and formatting checks after changes.
Use real-backend browser accessibility checks and captured desktop/mobile images
for visual review. Android CI builds/lints the APK and runs its actual-backend
emulator flow. These checks do not replace physical-device, screen-reader,
high-DPI or hardware decoder validation.
