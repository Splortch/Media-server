import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { it, expect, vi, afterEach } from 'vitest';
import IdentifiedTitle from '../src/components/IdentifiedTitle';
import { api } from '../src/services/api';
vi.mock('../src/services/api', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  api: vi.fn(),
}));
afterEach(() => vi.resetAllMocks());
const item = {
  id: 1,
  title: 'filename',
  duration: 100,
  container: 'webm',
  videoCodec: 'vp9',
  audioCodec: 'opus',
  size: 100,
};
const details = {
  title: 'Identified title',
  year: 2020,
  synopsis: 'Cached synopsis',
  genres: ['Drama'],
  cast: ['Local actor'],
  series: null,
  season: null,
  episode: null,
  provider: 'local-nfo',
  providerId: null,
  attribution: 'Local metadata',
};
const row = {
  details,
  posterUrl: '/api/media/1/artwork/poster?revision=1',
  backdropUrl: null,
  revision: 1,
};
it('shows saved metadata without starting playback or querying a cloud provider', async () => {
  vi.mocked(api).mockResolvedValue(row);
  const play = vi.fn();
  render(
    <IdentifiedTitle
      admin={false}
      item={item}
      resume={0}
      onPlay={play}
      onResume={play}
    />,
  );
  await screen.findByRole('heading', { name: 'Identified title' });
  expect(screen.getByText('Cached synopsis')).toBeInTheDocument();
  expect(screen.getByText('Cast: Local actor')).toBeInTheDocument();
  expect(api).toHaveBeenCalledExactlyOnceWith('/media/1/metadata');
  expect(play).not.toHaveBeenCalled();
  expect(
    screen.queryByText('Identify or correct title'),
  ).not.toBeInTheDocument();
});
it('reads sidecars only on request and saves corrections with the current revision', async () => {
  vi.mocked(api).mockImplementation(
    async (path, method) =>
      (path === '/metadata/providers'
        ? { enabled: false }
        : method
          ? { ...row, revision: 2 }
          : row) as never,
  );
  render(
    <IdentifiedTitle
      admin
      item={item}
      resume={0}
      onPlay={vi.fn()}
      onResume={vi.fn()}
    />,
  );
  await screen.findByRole('heading', { name: 'Identified title' });
  expect(vi.mocked(api).mock.calls.every((call) => !call[1])).toBe(true);
  fireEvent.click(screen.getByText('Identify or correct title'));
  fireEvent.click(
    screen.getByRole('button', { name: 'Read local NFO and artwork' }),
  );
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/media/1/metadata/local', 'POST', {
      expectedRevision: 1,
    }),
  );
  await waitFor(() =>
    expect(
      screen.getByRole('button', { name: 'Save title details' }),
    ).toBeEnabled(),
  );
  fireEvent.change(screen.getByLabelText('Title'), {
    target: { value: 'Corrected' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Save title details' }));
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/media/1/metadata', 'PUT', {
      details: { ...details, title: 'Corrected' },
      expectedRevision: 2,
    }),
  );
  expect(
    screen.queryByRole('button', { name: 'Search provider' }),
  ).not.toBeInTheDocument();
});
it('separates provider search from applying a selected match', async () => {
  vi.mocked(api).mockImplementation(
    async (path, method) =>
      (path === '/metadata/providers'
        ? { enabled: true }
        : path === '/metadata/search'
          ? [
              {
                id: '42',
                kind: 'movies',
                title: 'Provider fixture',
                year: 2020,
              },
            ]
          : method
            ? row
            : null) as never,
  );
  render(
    <IdentifiedTitle
      admin
      item={item}
      resume={0}
      onPlay={vi.fn()}
      onResume={vi.fn()}
    />,
  );
  fireEvent.click(screen.getByText('Identify or correct title'));
  const input = await screen.findByLabelText('Search TMDB');
  fireEvent.change(input, { target: { value: 'Fixture' } });
  fireEvent.click(screen.getByRole('button', { name: 'Search provider' }));
  const match = await screen.findByRole('button', {
    name: 'Match Provider fixture (2020)',
  });
  expect(
    vi.mocked(api).mock.calls.some((call) => call[0].endsWith('/tmdb')),
  ).toBe(false);
  fireEvent.click(match);
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/media/1/metadata/tmdb', 'POST', {
      id: '42',
      kind: 'movies',
      season: null,
      episode: null,
      expectedRevision: 0,
    }),
  );
});
