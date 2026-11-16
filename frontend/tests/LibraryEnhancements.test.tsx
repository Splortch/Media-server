import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import CardArtwork from '../src/components/CardArtwork';
import Player from '../src/components/Player';
import WatchControls from '../src/components/WatchControls';
import NextEpisodePrompt from '../src/components/NextEpisodePrompt';
import SeekPreview from '../src/components/SeekPreview';
import ArtworkManager from '../src/components/ArtworkManager';
import { api } from '../src/services/api';
vi.mock('../src/services/api', async (original) => ({
  ...(await original<object>()),
  api: vi.fn(),
}));
beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
});
afterEach(() => {
  vi.useRealTimers();
  vi.resetAllMocks();
});
it('uses a poster first and falls back to a generated thumbnail when it cannot load', async () => {
  vi.mocked(api).mockResolvedValue({
    posterUrl: '/poster.png',
    thumbnailUrl: '/thumbnail.jpg',
  });
  const view = render(<CardArtwork id={1} />);
  await waitFor(() =>
    expect(view.container.querySelector('img')).toHaveAttribute(
      'src',
      '/poster.png',
    ),
  );
  fireEvent.error(view.container.querySelector('img')!);
  await waitFor(() =>
    expect(view.container.querySelector('img')).toHaveAttribute(
      'src',
      '/thumbnail.jpg',
    ),
  );
  expect(view.container.querySelector('img')).toHaveAttribute(
    'loading',
    'lazy',
  );
  expect(
    vi.mocked(api).mock.calls.every(([path]) => path === '/media/1/images'),
  ).toBe(true);
});
it('uses the native image resolver rather than putting a remote URL into desktop cards', async () => {
  const request = vi.fn().mockResolvedValue({
    posterUrl: '/remote-poster',
    thumbnailUrl: '/remote-thumbnail',
  });
  const resolver = vi
    .fn()
    .mockResolvedValue('http://127.0.0.1/nonce/artwork/1/thumbnail');
  request.mockResolvedValue({
    posterUrl: null,
    thumbnailUrl: '/remote-thumbnail',
  });
  const view = render(
    <CardArtwork id={1} request={request as typeof api} imageUrl={resolver} />,
  );
  await waitFor(() =>
    expect(view.container.querySelector('img')).toHaveAttribute(
      'src',
      'http://127.0.0.1/nonce/artwork/1/thumbnail',
    ),
  );
  expect(resolver).toHaveBeenCalledWith(1, 'thumbnail');
});
it('confirms watch changes and undoes against the acknowledged revision', async () => {
  const previous = { seconds: 25, completed: false, hidden: false };
  vi.mocked(api).mockImplementation(async (_path, method, body) => {
    const action = (body as { action?: string } | undefined)?.action;
    return {
      progress: {
        revision: method === 'POST' ? (action === 'restore' ? 5 : 4) : 3,
        seconds: 25,
        completed: action !== 'restore',
      },
      hidden: false,
      previous,
    } as never;
  });
  const changed = vi.fn();
  render(<WatchControls id={1} onChange={changed} />);
  fireEvent.click(screen.getByRole('button', { name: 'Mark watched' }));
  expect(api).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Confirm change' }));
  await screen.findByText('Marked watched.');
  expect(api).toHaveBeenCalledWith('/media/1/watch-state', 'POST', {
    action: 'watched',
    expectedRevision: 3,
  });
  fireEvent.click(
    screen.getByRole('button', { name: 'Undo watch state change' }),
  );
  await screen.findByText('Previous watch state restored.');
  expect(api).toHaveBeenCalledWith('/media/1/watch-state', 'POST', {
    action: 'restore',
    expectedRevision: 4,
    restore: previous,
  });
  expect(changed).toHaveBeenCalledTimes(2);
});
it('keeps failed watch changes visible without claiming success', async () => {
  vi.mocked(api).mockRejectedValue(new Error('Conflict'));
  render(<WatchControls id={1} />);
  fireEvent.click(
    screen.getByRole('button', { name: 'Remove from Continue Watching' }),
  );
  expect(screen.getByText(/resume position will be kept/)).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Confirm change' }));
  expect(await screen.findByRole('status')).toHaveTextContent(
    'could not be changed',
  );
  expect(
    screen.queryByRole('button', { name: 'Undo watch state change' }),
  ).toBeNull();
});
const next = {
  nextEpisode: { id: 2, title: 'Second episode', season: 1, episode: 2 },
};
it('defaults next-episode autoplay off and cancels the opted-in countdown', async () => {
  vi.mocked(api).mockResolvedValue(next);
  const play = vi.fn().mockResolvedValue(undefined);
  const view = render(
    <NextEpisodePrompt
      id={1}
      ended={false}
      preferenceKey="viewer-a"
      onNext={play}
    />,
  );
  const toggle = await screen.findByLabelText(
    'Automatically play the next episode',
  );
  expect(toggle).not.toBeChecked();
  view.rerender(
    <NextEpisodePrompt id={1} ended preferenceKey="viewer-a" onNext={play} />,
  );
  vi.useFakeTimers();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(11000);
  });
  expect(play).not.toHaveBeenCalled();
  fireEvent.click(toggle);
  expect(localStorage.getItem('viewer-a')).toBe('true');
  await act(async () => {
    await vi.advanceTimersByTimeAsync(3000);
  });
  fireEvent.click(screen.getByRole('button', { name: 'Cancel next episode' }));
  await act(async () => {
    await vi.advanceTimersByTimeAsync(11000);
  });
  expect(play).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Play next episode' }));
  await act(async () => {});
  expect(play).toHaveBeenCalledExactlyOnceWith(2);
});
it('advances once after the full countdown and cancels timers on unmount', async () => {
  vi.mocked(api).mockResolvedValue(next);
  localStorage.setItem('viewer-a', 'true');
  const play = vi.fn().mockResolvedValue(undefined);
  const view = render(
    <NextEpisodePrompt
      id={1}
      ended={false}
      preferenceKey="viewer-a"
      onNext={play}
    />,
  );
  await screen.findByLabelText('Automatically play the next episode');
  vi.useFakeTimers();
  view.rerender(
    <NextEpisodePrompt id={1} ended preferenceKey="viewer-a" onNext={play} />,
  );
  await act(async () => {
    await vi.advanceTimersByTimeAsync(10000);
  });
  expect(play).toHaveBeenCalledExactlyOnceWith(2);
  view.unmount();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(20000);
  });
  expect(play).toHaveBeenCalledOnce();
});
it('debounces seek images and uses image routes without touching video playback', async () => {
  vi.useFakeTimers();
  const resolver = vi.fn().mockResolvedValue('/local-preview.jpg');
  const view = render(
    <SeekPreview id={1} seconds={42} duration={100} imageUrl={resolver} />,
  );
  view.rerender(
    <SeekPreview id={1} seconds={47} duration={100} imageUrl={resolver} />,
  );
  expect(resolver).not.toHaveBeenCalled();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(150);
  });
  expect(resolver).toHaveBeenCalledExactlyOnceWith(1, 40);
  expect(view.container.querySelector('img')).toHaveAttribute(
    'src',
    '/local-preview.jpg',
  );
  expect(document.querySelector('video')).toBeNull();
});
it('offers current/local images and applies a selected image with revision protection', async () => {
  vi.mocked(api).mockResolvedValue([{ key: 'local-1', url: '/candidate.png' }]);
  const changed = vi.fn();
  render(<ArtworkManager id={1} revision={7} onChanged={changed} />);
  fireEvent.click(screen.getByText('Manage artwork'));
  fireEvent.click(
    screen.getByRole('button', { name: 'Show available artwork' }),
  );
  fireEvent.click(
    await screen.findByRole('button', {
      name: 'Use artwork option 1 as poster',
    }),
  );
  await waitFor(() => expect(changed).toHaveBeenCalledOnce());
  expect(api).toHaveBeenCalledWith('/media/1/artwork/choice', 'POST', {
    kind: 'poster',
    source: 'existing',
    key: 'local-1',
    expectedRevision: 7,
  });
});

it('uploads a selected image with the current revision and clears the file input', async () => {
  vi.mocked(api).mockResolvedValue({});
  const changed = vi.fn();
  render(<ArtworkManager id={1} revision={3} onChanged={changed} />);
  fireEvent.click(screen.getByText('Manage artwork'));
  const file = new File([new Uint8Array([137, 80, 78, 71])], 'poster.png', {
    type: 'image/png',
  });
  Object.defineProperty(file, 'arrayBuffer', {
    value: async () => new Uint8Array([137, 80, 78, 71]).buffer,
  });
  const input = screen.getByLabelText('Upload artwork');
  fireEvent.change(input, { target: { files: [file] } });
  await waitFor(() => expect(changed).toHaveBeenCalledOnce());
  expect(api).toHaveBeenCalledWith('/media/1/artwork/upload', 'POST', {
    kind: 'poster',
    data: 'iVBORw==',
    expectedRevision: 3,
  });
  expect(input).toHaveValue('');
});

it('cancels the completion countdown when the user seeks back into the current episode', async () => {
  const request = vi.fn().mockResolvedValue(next);
  const play = vi.fn().mockResolvedValue(undefined);
  localStorage.setItem('viewer-a', 'true');
  render(
    <Player
      item={{
        id: 1,
        title: 'Episode',
        duration: 4,
        size: 100,
        container: 'webm',
        videoCodec: 'vp9',
        audioCodec: 'opus',
      }}
      resume={0}
      guest
      bookmarkRequest={request as typeof api}
      autoplayPreference="viewer-a"
      onSaveProgress={async () => {}}
      onNextEpisode={play}
    />,
  );
  await screen.findByLabelText('Automatically play the next episode');
  fireEvent.ended(screen.getByLabelText('Episode'));
  expect(
    await screen.findByText('Next episode starts in 10 seconds.'),
  ).toBeVisible();
  fireEvent.seeking(screen.getByLabelText('Episode'));
  expect(screen.queryByText(/Next episode starts in/)).toBeNull();
  expect(play).not.toHaveBeenCalled();
  expect(
    screen
      .getByRole('region', { name: 'Next episode' })
      .closest('.player-surface'),
  ).not.toBeNull();
});
