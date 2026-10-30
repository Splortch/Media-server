import {
  render,
  screen,
  fireEvent,
  waitFor,
  cleanup,
} from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { createRef } from 'react';
import PlaybackSettings from '../src/components/PlaybackSettings';
import Settings from '../src/pages/Settings';
import { api } from '../src/services/api';
vi.mock('../src/services/api', () => ({ api: vi.fn() }));
vi.mock('../src/features/admin/Accounts', () => ({ default: () => null }));
vi.mock('../src/features/admin/Monitoring', () => ({ default: () => null }));
vi.mock('../src/features/admin/LibraryTools', () => ({ default: () => null }));
vi.mock('../src/features/Personal', () => ({
  Collections: () => null,
  Sessions: () => null,
}));
afterEach(() => {
  cleanup();
  vi.resetAllMocks();
  vi.unstubAllGlobals();
});
it('labels real tracks, disables bitmap captions and exposes only authorized encoders', async () => {
  vi.mocked(api).mockResolvedValue({
    tracks: [
      {
        index: 1,
        type: 'audio',
        codec: 'aac',
        language: 'swe',
        title: 'Swedish',
      },
      {
        index: 2,
        type: 'subtitle',
        codec: 'hdmv_pgs_subtitle',
        language: 'eng',
      },
      {
        index: 3,
        type: 'subtitle',
        codec: 'subrip',
        language: 'swe',
        title: '<script>text</script>',
      },
    ],
    externalWebVtt: true,
    encoders: ['libx264'],
    conversionNotice: 'Conversion limits',
  });
  render(
    <PlaybackSettings
      id={1}
      video={createRef()}
      source="/api/media/1/stream"
      onSource={() => {}}
      onCaption={() => {}}
    />,
  );
  const details = screen
    .getByText('Quality, audio and subtitles')
    .closest('details')!;
  details.open = true;
  fireEvent(details, new Event('toggle'));
  await waitFor(() =>
    expect(screen.getByLabelText('Audio track')).toHaveTextContent('Swedish'),
  );
  expect(
    screen.getByRole('option', { name: /hdmv_pgs_subtitle/ }),
  ).toBeDisabled();
  expect(
    screen.getByRole('option', { name: /<script>text/ }),
  ).toBeInTheDocument();
  expect(document.querySelector('script')).toBeNull();
  expect(screen.getByLabelText('Encoder preference').children).toHaveLength(1);
});
it('removes a library only after explicit confirmation and reports server refusal', async () => {
  vi.mocked(api).mockImplementation(async (path, method) => {
    if (method === 'DELETE')
      throw new Error('Wait for the active library scan to finish.');
    if (path === '/libraries')
      return [{ id: 4, name: 'Movies', path: '/media/movies' }];
    return [];
  });
  render(<Settings />);
  fireEvent.click(
    await screen.findByRole('button', { name: 'Remove library Movies' }),
  );
  expect(api).not.toHaveBeenCalledWith('/libraries/4', 'DELETE');
  fireEvent.click(screen.getByRole('button', { name: 'Confirm removal' }));
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'Wait for the active library scan',
  );
  expect(screen.getByText('/media/movies')).toBeInTheDocument();
  vi.mocked(api).mockResolvedValue(undefined);
  fireEvent.click(screen.getByRole('button', { name: 'Confirm removal' }));
  await waitFor(() => expect(screen.queryByText('/media/movies')).toBeNull());
});

it('starts an already-ready cached conversion through the native transport', async () => {
  const Hls = (await import('hls.js')).default;
  vi.spyOn(Hls, 'isSupported').mockReturnValue(true);
  const id = 'a'.repeat(32);
  const request = vi.fn().mockResolvedValue({
    id,
    state: 'ready',
    encoder: 'libx264',
    error: null,
    encodeSeconds: 1,
    manifest: `/api/v1/playback-sessions/${id}/files/master.m3u8`,
  });
  const fileUrl = vi
    .fn()
    .mockResolvedValue(
      `http://127.0.0.1:5058/secret/playback/${id}/files/master.m3u8`,
    );
  const onSource = vi.fn();
  render(
    <PlaybackSettings
      id={1}
      video={createRef()}
      source="http://127.0.0.1:5058/secret/media/1"
      onSource={onSource}
      onCaption={() => {}}
      transport={{
        request,
        fileUrl,
        directUrl: 'http://127.0.0.1:5058/secret/media/1',
      }}
    />,
  );
  fireEvent.click(screen.getByRole('button', { name: 'Prepare HLS playback' }));
  await waitFor(() =>
    expect(onSource).toHaveBeenCalledWith(
      `http://127.0.0.1:5058/secret/playback/${id}/files/master.m3u8`,
    ),
  );
  expect(fileUrl).toHaveBeenCalledWith(id, 'master.m3u8');
  expect(request).toHaveBeenCalledWith(
    '/v1/media/1/playback-sessions',
    'POST',
    { audioTrack: null, subtitleTrack: null, encoder: 'libx264' },
  );
  vi.mocked(Hls.isSupported).mockRestore();
});

it('settings shortcuts move keyboard focus and respect reduced motion without changing routes', async () => {
  vi.mocked(api).mockResolvedValue([]);
  const scroll = vi.fn();
  vi.stubGlobal(
    'matchMedia',
    vi.fn(() => ({ matches: true })),
  );
  location.hash = '#settings';
  render(<Settings />);
  await waitFor(() => expect(api).toHaveBeenCalledWith('/libraries'));
  const target = document.getElementById('settings-account')!;
  target.scrollIntoView = scroll;
  fireEvent.click(screen.getByRole('button', { name: 'Account' }));
  expect(target).toHaveFocus();
  expect(scroll).toHaveBeenCalledWith({ behavior: 'auto', block: 'start' });
  expect(location.hash).toBe('#settings');
});
