# Accessibility validation

The real-backend Playwright flow now runs axe WCAG 2 A/AA and 2.1 A/AA checks on setup, login,
settings, the discovered library, the player and its missing-file error. It also verifies active
navigation semantics, focus after hash navigation, keyboard entry into search, search-result
announcements, and keyboard operation of the diagnostics disclosure. No axe rules are suppressed.
The synthetic video uses test patterns and a tone, with no speech or meaningful dialogue.

Fixes include a page-level library heading, aria-current on navigation, deliberate route/login
focus and document titles, uniquely named scan controls, busy forms, search counts and buffering
announcements. Focus indicators cover disclosures and reduced-motion preferences remain respected.

Run `bash scripts/e2e.sh` with the .NET SDK, FFmpeg, npm dependencies and Playwright Chromium installed.
`PLAYWRIGHT_CHROMIUM_EXECUTABLE=/path/to/chrome` optionally selects an installed browser for local
testing; CI uses Playwright's own pinned Chromium. The suite uses an isolated temporary database
and legally distributable synthetic media and removes them when finished.

Automated checks do not certify accessibility. Before closing #15, both maintainers should test:

- Setup/login with keyboard only, including errors, busy states and session expiry.
- Tab/Shift+Tab order and visible focus through navigation, search, cards, settings and video controls.
- NVDA with Windows/Edge and VoiceOver with Safari: headings, labels, alerts, status messages and
  native video control announcements. Native media controls differ between browsers.
- 200%/400% zoom, narrow viewport, high contrast/forced colors and reduced motion.
- Captions for meaningful spoken content. Subtitle selection is still tracked by #9; user-provided
  video without captions cannot be made accessible solely by accessible surrounding controls.

Record browser/assistive technology versions and observations in the issue. Do not mark manual
screen-reader or physical Windows validation complete based on axe or DOM tests alone.
