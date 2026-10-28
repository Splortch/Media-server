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
