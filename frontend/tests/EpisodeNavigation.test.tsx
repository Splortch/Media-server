import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import EpisodeNavigation from '../src/components/EpisodeNavigation';
import { type api } from '../src/services/api';

const episode = {
  id: 2,
  title: 'Second chapter',
  season: 1,
  episode: 2,
  duration: 120,
  seconds: 30,
  completed: false,
};
const navigation = {
  series: 'Our show',
  seasons: [0, 1, 2],
  season: 1,
  episodes: [episode],
  nextOffset: null,
  nextEpisode: episode,
};
it('browses seasons and opens episode information without playback requests', async () => {
  const open = vi.fn();
  const request = vi.fn<typeof api>().mockImplementation(
    async (path) =>
      (path.endsWith('season=2')
        ? {
            ...navigation,
            season: 2,
            episodes: [
              { ...episode, id: 3, season: 2, episode: 1, completed: true },
            ],
          }
        : navigation) as never,
  );
  render(
    <EpisodeNavigation
      id={1}
      request={request as typeof api}
      onEpisode={open}
    />,
  );
  expect(
    await screen.findByRole('button', {
      name: /Episode 2 · Second chapter.*Resume from 30 sec/,
    }),
  ).toBeEnabled();
  expect(screen.getByRole('option', { name: 'Specials' })).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: /View next episode/ }));
  expect(open).toHaveBeenCalledExactlyOnceWith(2);
  fireEvent.change(screen.getByLabelText('Season'), { target: { value: '2' } });
  const watched = await screen.findByRole('button', {
    name: /Episode 1 · Second chapter.*Watched/,
  });
  fireEvent.click(watched);
  expect(open).toHaveBeenLastCalledWith(3);
  expect(screen.getByLabelText('Season')).toHaveValue('2');
  expect(request.mock.calls.map(([path]) => path)).toEqual([
    '/media/1/episodes',
    '/media/1/episodes?season=2',
  ]);
  expect(document.querySelector('video')).toBeNull();
});
it('paginates a long season and preserves previously loaded episodes', async () => {
  const request = vi
    .fn<typeof api>()
    .mockResolvedValueOnce({ ...navigation, nextOffset: 200 } as never)
    .mockResolvedValueOnce({
      ...navigation,
      episodes: [{ ...episode, id: 3, episode: 201 }],
    } as never);
  render(
    <EpisodeNavigation
      id={1}
      request={request as typeof api}
      onEpisode={vi.fn()}
    />,
  );
  fireEvent.click(
    await screen.findByRole('button', { name: 'Load more episodes' }),
  );
  expect(
    await screen.findByRole('button', { name: /Episode 201/ }),
  ).toBeEnabled();
  expect(screen.getByRole('button', { name: /^Episode 2 ·/ })).toBeEnabled();
  expect(request).toHaveBeenLastCalledWith('/media/1/episodes?offset=200');
});
it('reports unavailable episode metadata and allows a retry', async () => {
  const request = vi
    .fn<typeof api>()
    .mockRejectedValueOnce(new Error('Offline'))
    .mockResolvedValueOnce(navigation as never);
  render(
    <EpisodeNavigation
      id={1}
      request={request as typeof api}
      onEpisode={vi.fn()}
    />,
  );
  fireEvent.click(
    await screen.findByRole('button', { name: 'Retry episodes' }),
  );
  expect(
    await screen.findByRole('button', { name: /View next episode/ }),
  ).toBeEnabled();
  await waitFor(() =>
    expect(
      screen.queryByText(/Episodes could not be loaded/),
    ).not.toBeInTheDocument(),
  );
});
