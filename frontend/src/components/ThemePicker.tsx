import { useState } from 'react';
import { chooseTheme, themes, useTheme, type Theme } from '../theme/themes';
export default function ThemePicker({
  onChange,
}: {
  onChange?: (theme: Theme) => void;
}) {
  const theme = useTheme();
  const [error, setError] = useState('');
  return (
    <div className="theme-picker">
      <label>
        Theme
        <select
          value={theme}
          onChange={(e) => {
            const next = e.target.value as Theme;
            setError(
              chooseTheme(next)
                ? ''
                : 'Theme applied for this visit, but this device could not save the preference.',
            );
            onChange?.(next);
          }}
        >
          {themes.map((t) => (
            <option value={t.key} key={t.key}>
              {t.label}
            </option>
          ))}
        </select>
      </label>
      <p className="muted">
        Dark is the default. Your choice stays on this device and applies to
        setup, browsing, settings and playback.
      </p>
      {error && <p role="status">{error}</p>}
    </div>
  );
}
