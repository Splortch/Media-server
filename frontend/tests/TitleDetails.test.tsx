import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import TitleDetails from '../src/components/TitleDetails';

const item = {
  id: 7,
  title: 'Own movie',
  duration: 100,
  container: 'webm',
  videoCodec: 'vp9',
  audioCodec: 'opus',
  size: 100,
};
it('renders metadata and tracks together without mounting playback', () => {
  const onPlay = vi.fn();
  render(
    <TitleDetails
      item={item}
      resume={0}
      onPlay={onPlay}
      onResume={vi.fn()}
      metadata={{
        details: {
          year: 2024,
          synopsis: 'A family adventure.',
          genres: ['Adventure'],
          cast: ['Example Actor'],
          series: null,
          season: null,
          episode: null,
          attribution: 'Local metadata',
        },
        posterUrl: null,
        backdropUrl: null,
      }}
      tracks={[{ type: 'audio', codec: 'opus', language: 'sv' }]}
    />,
  );
  expect(screen.getByText('A family adventure.')).toBeVisible();
  expect(screen.getByText('Adventure')).toBeVisible();
  expect(screen.getByText('Example Actor')).toBeVisible();
  expect(screen.queryByText('Not available')).not.toBeInTheDocument();
  fireEvent.click(screen.getByText('File and track details'));
  expect(screen.getByText('audio · opus · sv')).toBeVisible();
  expect(document.querySelector('video')).toBeNull();
  expect(onPlay).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Play from beginning' }));
  expect(onPlay).toHaveBeenCalledOnce();
});
it('labels missing descriptive metadata honestly', () => {
  render(
    <TitleDetails item={item} resume={0} onPlay={vi.fn()} onResume={vi.fn()} />,
  );
  expect(screen.getAllByText('Not available')).toHaveLength(3);
  expect(document.querySelector('video')).toBeNull();
});
