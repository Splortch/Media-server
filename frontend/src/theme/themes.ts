import { useEffect, useState } from 'react';
// New themes must define the shared semantic CSS tokens and pass both UI audits.
export const themes = [
  { key: 'dark', label: 'Dark', chrome: '#080f17' },
  { key: 'light', label: 'Light', chrome: '#f0f8fc' },
] as const;
export type Theme = (typeof themes)[number]['key'];
const storageKey = 'media-server.theme.v1';
const eventName = 'media-server-theme';
export function normalizeTheme(value: unknown): Theme {
  return themes.some((t) => t.key === value) ? (value as Theme) : 'dark';
}
export function storedTheme(): Theme | undefined {
  try {
    const value = localStorage.getItem(storageKey);
    return themes.some((t) => t.key === value) ? (value as Theme) : undefined;
  } catch {
    return undefined;
  }
}
export function currentTheme(): Theme {
  return storedTheme() ?? 'dark';
}
export function applyTheme(value: unknown) {
  const theme = normalizeTheme(value);
  document.documentElement.dataset.theme = theme;
  document
    .querySelector('meta[name="theme-color"]')
    ?.setAttribute('content', themes.find((t) => t.key === theme)!.chrome);
  window.dispatchEvent(new CustomEvent(eventName, { detail: theme }));
}
export function chooseTheme(value: unknown): boolean {
  const theme = normalizeTheme(value);
  applyTheme(theme);
  try {
    localStorage.setItem(storageKey, theme);
    return true;
  } catch {
    return false;
  }
}
let initialized = false;
export function initializeTheme() {
  applyTheme(currentTheme());
  if (initialized) return;
  initialized = true;
  window.addEventListener('storage', (e) => {
    if (e.key === storageKey || e.key === null) applyTheme(currentTheme());
  });
}
export function useTheme(): Theme {
  const [theme, setTheme] = useState<Theme>(() =>
    normalizeTheme(document.documentElement.dataset.theme ?? currentTheme()),
  );
  useEffect(() => {
    const update = (event: Event) =>
      setTheme(normalizeTheme((event as CustomEvent).detail));
    window.addEventListener(eventName, update);
    return () => window.removeEventListener(eventName, update);
  }, []);
  return theme;
}
