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

it('shows buffering and browser playback diagnostics without claiming measured network throughput', () => {
  vi.spyOn(HTMLMediaElement.prototype, 'canPlayType').mockReturnValue(
    'probably',
  );
  render(
    <Player
      item={{
        id: 8,
        title: 'Diagnostics video',
        duration: 100,
        size: 1000000,
        container: 'webm',
        videoCodec: 'vp9',
        audioCodec: 'opus',
      }}
      resume={0}
    />,
  );
  const video = screen.getByLabelText('Diagnostics video');
  Object.defineProperty(video, 'buffered', {
    value: { length: 1, start: () => 0, end: () => 30 },
    configurable: true,
  });
  Object.defineProperty(video, 'getVideoPlaybackQuality', {
    value: () => ({ droppedVideoFrames: 2, totalVideoFrames: 20 }),
    configurable: true,
  });
  fireEvent.loadedMetadata(video);
  expect(screen.getByText('30.0 seconds')).toBeInTheDocument();
  expect(screen.getByText('2 / 20')).toBeInTheDocument();
  expect(
    screen.getByText(/not measured network throughput/),
  ).toBeInTheDocument();
  fireEvent.waiting(video);
  expect(screen.getByRole('status')).toHaveTextContent('Buffering');
  fireEvent.playing(video);
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
});

it('distinguishes browser network and decode errors', () => {
  render(
    <Player
      item={{
        id: 9,
        title: 'Error video',
        duration: 100,
        size: 100,
        container: 'mp4',
        videoCodec: 'h264',
        audioCodec: 'aac',
      }}
      resume={0}
      onPlaybackError={async () => {}}
    />,
  );
  const video = screen.getByLabelText('Error video');
  Object.defineProperty(video, 'error', {
    value: { code: 2 },
    configurable: true,
  });
  fireEvent.error(video);
  expect(screen.getByRole('alert')).toHaveTextContent('Check your connection');
  Object.defineProperty(video, 'error', {
    value: { code: 3 },
    configurable: true,
  });
  fireEvent.error(video);
  expect(screen.getByRole('alert')).toHaveTextContent('could not decode');
});

it('provides custom controls, bounded seeking, volume and container fullscreen', async () => {
  const save = vi.fn().mockResolvedValue(undefined);
  render(
    <Player
      item={{
        id: 10,
        title: 'Controls video',
        duration: 100,
        size: 100,
        container: 'mp4',
        videoCodec: 'h264',
        audioCodec: 'aac',
      }}
      resume={20}
      onSaveProgress={save}
    />,
  );
  const video = screen.getByLabelText('Controls video') as HTMLVideoElement;
  expect(video.controls).toBe(false);
  Object.defineProperty(video, 'duration', { value: 100, configurable: true });
  fireEvent.loadedMetadata(video);
  expect(video.currentTime).toBe(20);
  fireEvent.change(screen.getByLabelText('Seek'), { target: { value: '95' } });
  expect(video.currentTime).toBe(95);
  fireEvent.click(screen.getByRole('button', { name: 'Forward 10 seconds' }));
  expect(video.currentTime).toBe(100);
  fireEvent.change(screen.getByLabelText('Volume'), {
    target: { value: '0.3' },
  });
  expect(video.volume).toBe(0.3);
  fireEvent.click(screen.getByRole('button', { name: 'Mute' }));
  expect(video.muted).toBe(true);
  const play = vi.spyOn(video, 'play').mockResolvedValue();
  fireEvent.click(screen.getByRole('button', { name: 'Play' }));
  expect(play).toHaveBeenCalledOnce();
  fireEvent.play(video);
  expect(screen.getByRole('button', { name: 'Pause' })).toBeInTheDocument();
  const surface = screen.getByLabelText('Playback controls');
  const fullscreen = vi.fn().mockResolvedValue(undefined);
  Object.defineProperty(surface, 'requestFullscreen', { value: fullscreen });
  fireEvent.click(screen.getByRole('button', { name: 'Fullscreen' }));
  expect(fullscreen).toHaveBeenCalledOnce();
  fireEvent.keyDown(surface, { key: 'ArrowLeft' });
  expect(video.currentTime).toBe(90);
  fireEvent.keyDown(screen.getByLabelText('Seek'), { key: 'ArrowLeft' });
  expect(video.currentTime).toBe(90);
  fireEvent.pause(video);
  await waitFor(() => expect(save).toHaveBeenCalledWith(90, false));
});

it('shows rejected play requests and disables seeking before metadata', async () => {
  render(
    <Player
      item={{
        id: 11,
        title: 'Unavailable video',
        duration: 0,
        size: 0,
        container: 'mp4',
        videoCodec: '',
        audioCodec: '',
      }}
      resume={0}
    />,
  );
  expect(screen.getByLabelText('Seek')).toBeDisabled();
  const video = screen.getByLabelText('Unavailable video') as HTMLVideoElement;
  vi.spyOn(video, 'play').mockRejectedValue(new Error('blocked'));
  fireEvent.click(screen.getByRole('button', { name: 'Play' }));
  await waitFor(() =>
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Playback could not start',
    ),
  );
});
