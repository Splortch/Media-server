import { beforeEach, afterEach, expect, it, vi } from 'vitest';
import {
  fireEvent,
  render,
  screen,
  waitFor,
  act,
} from '@testing-library/react';
import ThemePicker from '../src/components/ThemePicker';
import {
  applyTheme,
  chooseTheme,
  currentTheme,
  initializeTheme,
  normalizeTheme,
  themes,
} from '../src/theme/themes';
beforeEach(() => {
  localStorage.clear();
  applyTheme('dark');
});
afterEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
  applyTheme('dark');
});
it('defaults to dark and rejects unknown future preference values', () => {
  expect(currentTheme()).toBe('dark');
  expect(normalizeTheme('system')).toBe('dark');
  expect(normalizeTheme('unknown')).toBe('dark');
  localStorage.setItem('media-server.theme.v1', 'unknown');
  initializeTheme();
  expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
  expect(new Set(themes.map((t) => t.key)).size).toBe(themes.length);
});
it('applies and persists light, restores it on startup and synchronizes selectors', async () => {
  render(
    <>
      <ThemePicker />
      <ThemePicker />
    </>,
  );
  const selectors = screen.getAllByRole('combobox', { name: 'Theme' });
  fireEvent.change(selectors[0], { target: { value: 'light' } });
  expect(document.documentElement).toHaveAttribute('data-theme', 'light');
  expect(localStorage.getItem('media-server.theme.v1')).toBe('light');
  await waitFor(() => expect(selectors[1]).toHaveValue('light'));
  act(() => {
    applyTheme('dark');
    initializeTheme();
  });
  expect(document.documentElement).toHaveAttribute('data-theme', 'light');
});
it('keeps the application usable if preference storage is unavailable', async () => {
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new Error('Storage unavailable');
  });
  render(<ThemePicker />);
  fireEvent.change(screen.getByRole('combobox', { name: 'Theme' }), {
    target: { value: 'light' },
  });
  expect(document.documentElement).toHaveAttribute('data-theme', 'light');
  expect(screen.getByRole('status')).toHaveTextContent('could not save');
});
it('synchronizes another-tab changes and falls back safely after deletion', () => {
  initializeTheme();
  localStorage.setItem('media-server.theme.v1', 'light');
  window.dispatchEvent(
    new StorageEvent('storage', {
      key: 'media-server.theme.v1',
      newValue: 'light',
    }),
  );
  expect(document.documentElement).toHaveAttribute('data-theme', 'light');
  localStorage.clear();
  window.dispatchEvent(new StorageEvent('storage', { key: null }));
  expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
  expect(chooseTheme('dark')).toBe(true);
});
