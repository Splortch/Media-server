import { act, fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import Player from '../src/components/Player';

const item = {
  id: 42,
  title: 'Family film',
  duration: 100,
  size: 100,
  container: 'mp4',
  videoCodec: 'h264',
  audioCodec: 'aac',
};
it('reveals the video after a decoded frame, not metadata or buffering', () => {
  const { container } = render(
    <Player
      item={item}
      resume={0}
      startPlaying
      onPlaybackError={async () => {}}
    />,
  );
  const video = screen.getByLabelText(item.title) as HTMLVideoElement;
  let decoded: (() => void) | undefined;
  Object.defineProperty(video, 'requestVideoFrameCallback', {
    value: (callback: () => void) => {
      decoded = callback;
      return 1;
    },
  });
  const cover = container.querySelector('.playback-entrance')!;
  fireEvent.canPlay(video);
  expect(cover).not.toHaveClass('playback-entrance-ready');
  fireEvent.playing(video);
  expect(cover).not.toHaveClass('playback-entrance-ready');
  act(() => decoded?.());
  expect(cover).toHaveClass('playback-entrance-ready');
});
it('clears the cover on playback failure and keeps controls available', () => {
  vi.spyOn(globalThis, 'fetch').mockRejectedValue(new Error('offline'));
  const { container } = render(
    <Player
      item={item}
      resume={0}
      startPlaying
      onPlaybackError={async () => {}}
    />,
  );
  fireEvent.error(screen.getByLabelText(item.title));
  expect(container.querySelector('.playback-entrance')).toHaveClass(
    'playback-entrance-ready',
  );
  expect(screen.getByRole('button', { name: 'Play' })).toBeEnabled();
  expect(screen.getByRole('alert')).toBeInTheDocument();
  vi.restoreAllMocks();
});
