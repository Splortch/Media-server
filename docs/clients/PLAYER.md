# Custom video player

The web app and Windows desktop client share `frontend/src/components/Player.tsx`.
Custom controls replace the default browser toolbar. Authenticated streaming and
revision-checked progress saving remain unchanged.

- Play / pause, seek timeline, elapsed and total time, and ten-second skips.
- Mute, volume, and fullscreen including the custom toolbar.
- Fullscreen video fills the available viewport while preserving its aspect ratio;
  the compact icon toolbar overlays the bottom of the video.
- Click or tap the video to pause or resume. This also focuses the player for shortcuts.
- The native video context menu is disabled so browser controls cannot be exposed.
- Focus the player surface: Space or K toggles playback, Left / Right skips ten
  seconds, Up / Down changes volume by 5%, Home / End seeks to the beginning / end,
  M toggles mute, and F toggles fullscreen. Escape exits browser fullscreen.
- Use Tab and Enter for buttons, and arrow keys for timeline and volume sliders.
  Shortcuts do not override slider input.

Fullscreen controls fade out after 2.5 seconds of playback inactivity, and the
cursor hides. Pointer movement, a tap, or a supported keyboard shortcut reveals
them. Controls remain visible while paused, buffering, showing a playback error,
hovered, or focused with the keyboard. Hidden controls leave the tab order;
Tab reveals them again. Outside fullscreen, controls remain visible.
Errors, buffering, diagnostics,
and synchronization failures remain available below the player.

This interface uses HTML video. Browser/WebView2 codec limits still apply.
Transcoding and server-side subtitle/audio selection are not added. Android retains
its native Media3 player.

Component tests cover controls, bounded seeking, resume, progress persistence,
buffering, errors, and rejected play requests. The real-backend Playwright scenario
uses custom play, pause, and seek controls and verifies saved progress. Actual
Windows fullscreen, touch use, and codec behavior require device testing.
