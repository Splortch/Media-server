import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, expect, it, afterEach } from 'vitest';
import Player from '../src/components/Player';
afterEach(() => vi.restoreAllMocks());
it('uses the authenticated stream endpoint and reports unsupported playback', () => {
  render(
    <Player
      item={{
        id: 3,
        title: 'Own video',
        duration: 100,
        size: 100,
        container: 'mp4',
        videoCodec: 'h264',
        audioCodec: 'aac',
      }}
      resume={0}
    />,
  );
  const video = screen.getByLabelText('Own video');
  expect(video).toHaveAttribute('src', '/api/media/3/stream');
  fireEvent.error(video);
  expect(screen.getByRole('alert')).toHaveTextContent(
    'Transcoding is not available',
  );
});
it('saves real playback progress with an antiforgery token', async () => {
  const fetch = vi
    .spyOn(globalThis, 'fetch')
    .mockResolvedValueOnce(new Response(JSON.stringify({ token: 'test-csrf' })))
    .mockResolvedValueOnce(
      new Response(JSON.stringify({ revision: 1 }), { status: 200 }),
    );
  render(
    <Player
      item={{
        id: 4,
        title: 'Progress video',
        duration: 100,
        size: 100,
        container: 'mp4',
        videoCodec: 'h264',
        audioCodec: 'aac',
      }}
      resume={0}
    />,
  );
  const video = screen.getByLabelText('Progress video');
  Object.defineProperty(video, 'currentTime', {
    value: 25,
    configurable: true,
  });
  fireEvent.pause(video);
  await waitFor(() =>
    expect(fetch).toHaveBeenCalledWith(
      '/api/v1/media/4/progress-sync',
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({
          seconds: 25,
          completed: false,
          expectedRevision: 0,
        }),
        headers: expect.objectContaining({ 'X-CSRF-TOKEN': 'test-csrf' }),
      }),
    ),
  );
});
