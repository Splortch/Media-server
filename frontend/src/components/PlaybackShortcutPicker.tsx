import { useState } from 'react';
import {
  loadPlaybackShortcuts,
  shortcutModes,
  type ShortcutMode,
} from '../services/playbackShortcuts';

export default function PlaybackShortcutPicker({
  preferenceKey,
}: {
  preferenceKey: string;
}) {
  const [mode, setMode] = useState(() => loadPlaybackShortcuts(preferenceKey));
  const [error, setError] = useState(false);
  return (
    <div className="theme-picker">
      <label>
        Playback keyboard shortcuts
        <select
          value={mode}
          onChange={(event) => {
            const next = event.target.value as ShortcutMode;
            try {
              localStorage.setItem(preferenceKey, next);
              setMode(next);
              setError(false);
            } catch {
              setError(true);
            }
          }}
        >
          {Object.entries(shortcutModes).map(([value, label]) => (
            <option value={value} key={value}>
              {label}
            </option>
          ))}
        </select>
      </label>
      <p className="muted">
        Standard also enables K for play and pause, F for fullscreen and M for
        mute. Shortcuts work when the player itself has focus. Your choice stays
        on this device.
      </p>
      {error && (
        <p role="status">This device could not save the shortcut preference.</p>
      )}
    </div>
  );
}
