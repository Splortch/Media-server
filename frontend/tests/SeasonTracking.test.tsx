import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import SeasonWatchControls from '../src/components/SeasonWatchControls';
import EpisodeSpoilerPreference from '../src/components/EpisodeSpoilerPreference';
import TitleDetails from '../src/components/TitleDetails';
import { type api, type Media } from '../src/services/api';

it('confirms the entire season and undoes using the server-owned token', async () => {
  const request = vi
    .fn<typeof api>()
    .mockResolvedValueOnce({
      progress: { season: 1, total: 2, watched: 2, revision: 'new' },
      undoToken: 'private-token',
    } as never)
    .mockResolvedValueOnce({
      progress: { season: 1, total: 2, watched: 0, revision: 'last' },
      undoToken: null,
    } as never);
  const changed = vi.fn();
  render(
    <SeasonWatchControls
      id={1}
      progress={{ season: 1, total: 2, watched: 0, revision: 'shown' }}
      request={request as typeof api}
      onChanged={changed}
    />,
  );
  fireEvent.click(screen.getByRole('button', { name: 'Mark season watched' }));
  expect(request).not.toHaveBeenCalled();
  expect(screen.getByText(/all 2 accessible episodes/)).toBeInTheDocument();
  fireEvent.click(
    screen.getByRole('button', { name: 'Confirm season change' }),
  );
  fireEvent.click(
    await screen.findByRole('button', { name: 'Undo season change' }),
  );
  await waitFor(() => expect(changed).toHaveBeenCalledTimes(2));
  expect(request.mock.calls).toEqual([
    [
      '/media/1/season-watch-state',
      'POST',
      { season: 1, action: 'watched', expectedRevision: 'shown' },
    ],
    [
      '/media/1/season-watch-state',
      'POST',
      { season: 1, action: 'undo', undoToken: 'private-token' },
    ],
  ]);
  expect(
    screen.queryByRole('button', { name: 'Undo season change' }),
  ).toBeNull();
});
it('preserves the current view on a season conflict and explains recovery', async () => {
  const request = vi.fn<typeof api>().mockRejectedValue(new Error('Conflict'));
  const changed = vi.fn();
  render(
    <SeasonWatchControls
      id={1}
      progress={{ season: 1, total: 2, watched: 0, revision: 'old' }}
      request={request as typeof api}
      onChanged={changed}
    />,
  );
  fireEvent.click(
    screen.getByRole('button', { name: 'Mark season unwatched' }),
  );
  fireEvent.click(
    screen.getByRole('button', { name: 'Confirm season change' }),
  );
  expect(await screen.findByRole('status')).toHaveTextContent(
    'Refresh the episodes',
  );
  expect(changed).not.toHaveBeenCalled();
});
it('loads and saves spoiler preferences only after an explicit choice', async () => {
  const request = vi
    .fn<typeof api>()
    .mockResolvedValueOnce({ revealSpoilers: false } as never)
    .mockResolvedValueOnce({ revealSpoilers: true } as never);
  const changed = vi.fn();
  render(
    <EpisodeSpoilerPreference
      request={request as typeof api}
      onChanged={changed}
    />,
  );
  const checkbox = screen.getByRole('checkbox');
  await waitFor(() => expect(checkbox).toBeEnabled());
  expect(checkbox).not.toBeChecked();
  fireEvent.click(checkbox);
  await waitFor(() => expect(changed).toHaveBeenCalledOnce());
  expect(request).toHaveBeenLastCalledWith('/preferences/episodes', 'PUT', {
    revealSpoilers: true,
  });
});
it('does not render poster, backdrop or embedded synopsis while spoilers are hidden', () => {
  const item = {
    id: 1,
    title: 'Episode 2',
    duration: 120,
    container: 'webm',
    videoCodec: 'vp9',
    audioCodec: 'opus',
    size: 100,
    metadata: { description: 'Embedded spoiler', chapters: [] },
  } as unknown as Media;
  render(
    <TitleDetails
      item={item}
      resume={0}
      onPlay={vi.fn()}
      onResume={vi.fn()}
      metadata={{
        details: {
          year: 2020,
          synopsis: 'Secret ending',
          genres: [],
          cast: [],
          series: 'Show',
          season: 1,
          episode: 2,
          attribution: '',
        },
        posterUrl: '/poster.jpg',
        backdropUrl: '/backdrop.jpg',
        spoilerHidden: true,
      }}
    />,
  );
  expect(document.querySelector('img')).toBeNull();
  expect(screen.queryByText('Secret ending')).toBeNull();
  expect(screen.queryByText('Embedded spoiler')).toBeNull();
  expect(
    screen.getByText(/Episode summary and artwork are hidden/),
  ).toBeInTheDocument();
  expect(
    screen.getByRole('button', { name: /Play from beginning/ }),
  ).toBeEnabled();
});
it('keeps a pending checkbox choice visible and rolls it back when saving fails', async () => {
  let rejectSave!: (reason: Error) => void;
  const pending = new Promise((_, reject) => {
    rejectSave = reject;
  });
  const request = vi
    .fn<typeof api>()
    .mockResolvedValueOnce({ revealSpoilers: false } as never)
    .mockReturnValueOnce(pending as never);
  render(
    <EpisodeSpoilerPreference
      request={request as typeof api}
      onChanged={vi.fn()}
    />,
  );
  const checkbox = screen.getByRole('checkbox');
  await waitFor(() => expect(checkbox).toBeEnabled());
  fireEvent.click(checkbox);
  expect(checkbox).toBeChecked();
  expect(checkbox).toBeDisabled();
  rejectSave(new Error('Disconnected'));
  await waitFor(() => expect(checkbox).toBeEnabled());
  expect(checkbox).not.toBeChecked();
  expect(screen.getByRole('status')).toHaveTextContent('could not be saved');
});
