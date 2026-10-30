import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import App from '../src/App';
import { api } from '../src/services/api';
vi.mock('../src/services/api', async (original) => ({
  ...(await original<typeof import('../src/services/api')>()),
  resetCsrf: vi.fn(),
  api: vi.fn(),
}));
const media = {
  id: 7,
  title: 'Own video',
  duration: 100,
  size: 100,
  container: 'webm',
  videoCodec: 'vp9',
  audioCodec: 'opus',
};
beforeEach(() => {
  location.hash = '#library';
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined);
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  vi.mocked(api).mockImplementation(async (path) => {
    if (path === '/features')
      return {
        features: [
          {
            key: 'smart-collections',
            name: 'Smart collections',
            enabled: true,
            visible: true,
            hidden: false,
            serverConfigurable: true,
          },
          {
            key: 'watch-history',
            name: 'Watch history navigation',
            enabled: true,
            visible: true,
            hidden: false,
            serverConfigurable: false,
          },
        ],
      };
    if (path === '/setup') return { required: false };
    if (path === '/auth/me') return { name: 'Alice', role: 'Viewer' };
    if (path === '/status')
      return { version: 'fixture', mediaCount: 1, libraryCount: 1 };
    if (path.startsWith('/media-page'))
      return { items: [media], nextCursor: null };
    if (path === '/media/7') return media;
    if (path.startsWith('/progress'))
      return [
        {
          mediaItemId: 7,
          seconds: 32,
          revision: 3,
          completed: false,
          updatedAt: '2026-10-30T00:00:00Z',
        },
      ];
    if (path === '/collections' || path === '/continue-watching-media')
      return [];
    throw new Error('Unexpected fixture request: ' + path);
  });
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.mocked(api).mockReset();
});
async function details(query = '') {
  render(<App />);
  const card = await screen.findByRole('heading', { name: 'Own video' });
  if (query) {
    fireEvent.change(screen.getByLabelText('Search library'), {
      target: { value: query },
    });
    await waitFor(() =>
      expect(api).toHaveBeenCalledWith(expect.stringContaining('prefix=Own')),
    );
  }
  fireEvent.click(card);
  await screen.findByRole('region', { name: 'Title information' });
}
it('opens honest details without mounting a player, preserves filters and restores the card on back', async () => {
  await details('Own');
  expect(document.querySelector('video')).toBeNull();
  expect(
    screen.getByText(
      /Synopsis, artwork and series information are not available/,
    ),
  ).toBeVisible();
  expect(
    screen.getByRole('button', { name: 'Resume from 32 sec' }),
  ).toBeVisible();
  fireEvent.click(screen.getByRole('link', { name: 'Back to collection' }));
  await screen.findByLabelText('Search library');
  expect(screen.getByLabelText('Search library')).toHaveValue('Own');
  await waitFor(() =>
    expect(
      screen.getByRole('heading', { name: 'Own video' }).closest('a'),
    ).toHaveFocus(),
  );
});
it.each([
  ['Resume from 32 sec', 32],
  ['Play from beginning', 0],
])('starts only after %s and focuses the player', async (label, seconds) => {
  await details();
  expect(document.querySelector('video')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: label }));
  const video = await screen.findByLabelText('Own video');
  Object.defineProperty(video, 'duration', { value: 100, configurable: true });
  fireEvent.loadedMetadata(video);
  expect((video as HTMLVideoElement).currentTime).toBe(seconds);
  expect(HTMLMediaElement.prototype.play).toHaveBeenCalledOnce();
  expect(
    screen.getByRole('group', { name: 'Playback controls' }),
  ).toHaveFocus();
});
