# Appearance themes

Dark is the default on a fresh installation, including devices whose operating system uses light mode. Light is an explicit choice. Both keep the Lime Soda brand gradient; surfaces, fields, text, borders, focus indicators and error colors use semantic theme tokens. Video surfaces and playback controls stay dark so the image and transport remain readable.

## Choose a theme

- **Web:** use **Theme** on setup/sign-in, or **Settings → Appearance**.
- **Desktop (Windows/Linux/macOS):** use **Theme** on the server connection screen, or **Application settings → Appearance**.
- **Android phone:** use the **Dark** or **Light** theme chip above the connection/library content. The preference survives activity recreation/app restarts. Fullscreen playback hides this row.

The choice is local to the device/browser, not an administrator policy or account setting. Switching servers or signing out does not reset it. No viewing data is uploaded and no server request is required to change appearance. Web/desktop synchronize same-origin browser-tab preference changes. Storage failure still applies the theme for the current visit and shows a message instead of blocking use.

Existing explicit desktop dark/light preferences are migrated when no newer appearance preference exists. Legacy `system` and unrecognized future values fall back to dark. The desktop native preference file remains compatible with existing installations; web/desktop use the new `media-server.theme.v1` local-storage key, and Android uses private `appearance` preferences. OS dialogs, installer windows and Windows Server Control retain their operating-system styling.

## Extend the theme system

1. Add a stable key/label/browser chrome color to `frontend/src/theme/themes.ts`. Keep keys stable across releases; unrecognized keys must remain safe.
2. Add a matching `:root[data-theme='KEY']` rule defining the semantic tokens already listed in `frontend/src/styles/app.css`. Keep the gradient action colors and dark video controls readable, or deliberately supply audited replacements. Avoid overriding scattered components with hardcoded theme-specific colors.
3. The shared web/desktop selector reads the registry automatically. Update the native desktop `save_preferences` theme allowlist when adding a key, preserving validation and legacy fallback. Do not load theme CSS or executable scripts from arbitrary URLs.
4. Add the key to Android core `AppTheme` and provide its Material color scheme. The exhaustive `when` expression in `MediaTheme` forces a palette decision for each new enum entry.
5. Add preference round-trip/fallback tests, storage/recreation coverage and accessibility checks. Audit all states: setup/login, library/details, forms, disabled/focused controls, errors, notes and playback. Keep reduced-motion preferences and media playback behavior intact.

Current automated coverage includes dark defaults despite a light OS preference, light persistence/reload, multiple selectors/tab changes, corrupt/unknown preferences, unavailable storage, real-backend light-mode settings/library/details/player axe checks, and Android preference/recreation tests. Automated accessibility checks do not replace physical display or screen-reader testing. The dedicated Android TV/iOS applications are not yet implemented and are not claimed to support these settings.
