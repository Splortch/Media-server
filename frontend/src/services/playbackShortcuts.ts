export const shortcutModes = {
  standard: 'Standard',
  navigation: 'Space, arrows, Home and End',
  off: 'Off',
} as const;
export type ShortcutMode = keyof typeof shortcutModes;

export function playbackShortcutKey(account: string) {
  return 'media-server.playback-shortcuts:' + account;
}

export function loadPlaybackShortcuts(key: string): ShortcutMode {
  try {
    const value = localStorage.getItem(key);
    if (value === 'off' || value === 'navigation') return value;
  } catch {
    // Storage can be disabled by the browser.
  }
  return 'standard';
}
