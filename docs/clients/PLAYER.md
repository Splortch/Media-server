# Custom video player

The web app and Windows desktop client share `frontend/src/components/Player.tsx`.
Custom controls replace the default browser toolbar. Authenticated streaming and
revision-checked progress saving remain unchanged.

- Play / pause, seek timeline, elapsed and total time, and ten-second skips.
- Mute, volume, and fullscreen including the custom toolbar.
- Focus the player surface: Space or K toggles playback, Left / Right skips ten
  seconds, M toggles mute, and F toggles fullscreen.
- Use Tab and Enter for buttons, and arrow keys for timeline and volume sliders.
  Shortcuts do not override slider input.

Controls stay visible for keyboard and touch use. Errors, buffering, diagnostics,
and synchronization failures remain available below the player.

This interface uses HTML video. Browser/WebView2 codec limits still apply.
Transcoding and server-side subtitle/audio selection are not added. Android retains
its native Media3 player.

Component tests cover controls, bounded seeking, resume, progress persistence,
buffering, errors, and rejected play requests. The real-backend Playwright scenario
uses custom play, pause, and seek controls and verifies saved progress. Actual
Windows fullscreen, touch use, and codec behavior require device testing.
