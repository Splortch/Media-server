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
    if (path.startsWith('/catalog'))
      return { items: [media], nextCursor: null };
    if (path === '/media/7') return media;
    if (path === '/media/7/metadata') return null;
    if (path === '/v1/media/7/tracks')
      return {
        tracks: [
          { type: 'audio', codec: 'opus', language: 'sv', title: 'Swedish' },
        ],
      };
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
  expect(HTMLMediaElement.prototype.play).not.toHaveBeenCalled();
  await waitFor(() =>
    expect(screen.getByRole('heading', { name: 'Own video' })).toHaveFocus(),
  );
  fireEvent.click(screen.getByText('File and track details'));
  expect(await screen.findByText('audio · opus · sv · Swedish')).toBeVisible();
  expect(
    vi.mocked(api).mock.calls.some(([path]) => /stream|playback/.test(path)),
  ).toBe(false);
  expect(
    screen.getByText(/No description is embedded in this file/),
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

it('keeps explicit playback available when track metadata cannot be loaded', async () => {
  const implementation = vi.mocked(api).getMockImplementation()!;
  vi.mocked(api).mockImplementation((path, ...args) => {
    if (path === '/v1/media/7/tracks')
      return Promise.reject(new Error('Unavailable'));
    return implementation(path, ...args);
  });
  await details();
  fireEvent.click(screen.getByText('File and track details'));
  expect(
    await screen.findByText('Track information is not available.'),
  ).toBeVisible();
  expect(document.querySelector('video')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Play from beginning' }));
  expect(await screen.findByLabelText('Own video')).toBeInTheDocument();
});

it('selects the next identified episode without streaming and returns to the original filtered card', async () => {
  const implementation = vi.mocked(api).getMockImplementation()!;
  const episode = {
    id: 8,
    title: 'Second episode',
    season: 2,
    episode: 1,
    duration: 100,
    seconds: 0,
    completed: false,
  };
  vi.mocked(api).mockImplementation((path, ...args) => {
    if (path === '/media/7/metadata' || path === '/media/8/metadata')
      return Promise.resolve({
        details: {
          title: path.includes('/8/') ? episode.title : media.title,
          year: 2020,
          synopsis: 'Our series',
          genres: [],
          cast: [],
          series: 'Our show',
          season: path.includes('/8/') ? 2 : 1,
          episode: 1,
          attribution: 'Local metadata',
        },
        posterUrl: null,
        backdropUrl: null,
        revision: 1,
      });
    if (path === '/media/7/episodes' || path === '/media/8/episodes')
      return Promise.resolve({
        series: 'Our show',
        seasons: [1, 2],
        season: 2,
        episodes: [episode],
        nextOffset: null,
        nextEpisode: path.includes('/8/') ? null : episode,
      });
    if (path === '/media/8')
      return Promise.resolve({ ...media, id: 8, title: episode.title });
    if (path === '/v1/media/8/tracks') return Promise.resolve({ tracks: [] });
    return implementation(path, ...args);
  });
  await details('Own');
  fireEvent.click(
    await screen.findByRole('button', { name: /View next episode/ }),
  );
  await waitFor(() =>
    expect(
      screen.getByRole('heading', { name: 'Second episode' }),
    ).toHaveFocus(),
  );
  expect(document.querySelector('video')).toBeNull();
  expect(
    vi.mocked(api).mock.calls.some(([path]) => /stream|playback/.test(path)),
  ).toBe(false);
  fireEvent.click(screen.getByRole('link', { name: 'Back to collection' }));
  await waitFor(() =>
    expect(screen.getByLabelText('Search library')).toHaveValue('Own'),
  );
  await waitFor(() =>
    expect(
      screen.getByRole('heading', { name: 'Own video' }).closest('a'),
    ).toHaveFocus(),
  );
});

it('opens a grouped series overview and requires episode selection before offering playback', async () => {
  const implementation = vi.mocked(api).getMockImplementation()!;
  const details = {
    title: 'Pilot',
    year: 2020,
    synopsis: 'Episode description',
    genres: [],
    cast: [],
    series: 'Our show',
    season: 1,
    episode: 1,
    attribution: 'Local metadata',
  };
  vi.mocked(api).mockImplementation((path, ...args) => {
    if (path.startsWith('/catalog'))
      return Promise.resolve({
        items: [
          { ...media, title: 'Our show', series: 'Our show', episodeCount: 2 },
        ],
        nextCursor: null,
      });
    if (path === '/media/7/metadata' || path === '/media/8/metadata')
      return Promise.resolve({
        details: path.includes('/8/')
          ? { ...details, title: 'Second episode', episode: 2 }
          : details,
        posterUrl: null,
        backdropUrl: null,
        revision: 1,
      });
    if (path === '/media/7/episodes' || path === '/media/8/episodes')
      return Promise.resolve({
        series: 'Our show',
        seasons: [1],
        season: 1,
        episodes: [
          {
            id: 8,
            title: 'Second episode',
            season: 1,
            episode: 2,
            duration: 100,
            seconds: 0,
            completed: false,
          },
        ],
        nextOffset: null,
        nextEpisode: null,
      });
    if (path === '/media/8')
      return Promise.resolve({ ...media, id: 8, title: 'Second episode' });
    if (path === '/v1/media/8/tracks') return Promise.resolve({ tracks: [] });
    return implementation(path, ...args);
  });
  render(<App />);
  fireEvent.click(await screen.findByRole('heading', { name: 'Our show' }));
  await waitFor(() =>
    expect(screen.getByRole('heading', { name: 'Our show' })).toHaveFocus(),
  );
  expect(
    screen.queryByRole('button', { name: 'Play from beginning' }),
  ).toBeNull();
  expect(screen.queryByText('Episode description')).toBeNull();
  expect(document.querySelector('video')).toBeNull();
  fireEvent.click(
    await screen.findByRole('button', { name: /^Episode 2 · Second episode/ }),
  );
  await waitFor(() =>
    expect(
      screen.getByRole('heading', { name: 'Second episode' }),
    ).toHaveFocus(),
  );
  expect(
    screen.getByRole('button', { name: 'Play from beginning' }),
  ).toBeVisible();
  await waitFor(() => expect(api).toHaveBeenCalledWith('/media/8/episodes'));
  await screen.findByRole('button', { name: /^Episode 2 · Second episode/ });
  expect(document.querySelector('video')).toBeNull();
});
