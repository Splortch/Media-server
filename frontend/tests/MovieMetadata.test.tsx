import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import TitleDetails from '../src/components/TitleDetails';

it('displays embedded movie details and starts a selected chapter', () => {
  const seek = vi.fn();
  render(
    <TitleDetails
      item={{
        id: 1,
        title: 'Movie',
        duration: 120,
        container: 'matroska',
        videoCodec: 'h264',
        audioCodec: 'aac',
        size: 100,
        metadata: {
          description: 'A story from the file',
          releaseDate: '2025',
          genre: 'Drama',
          director: 'A Director',
          chapters: [{ title: 'Finale', start: 60, end: 120 }],
        },
      }}
      resume={0}
      onPlay={vi.fn()}
      onResume={vi.fn()}
      onBookmark={seek}
    />,
  );
  expect(screen.getByText('A story from the file')).toBeInTheDocument();
  expect(screen.getByText('2025')).toBeInTheDocument();
  expect(screen.getByText('Drama')).toBeInTheDocument();
  expect(screen.getByText('A Director')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: /Finale/ }));
  expect(seek).toHaveBeenCalledWith(60);
});

it('preserves matched metadata when embedded descriptions are also present', () => {
  render(
    <TitleDetails
      item={{
        id: 1,
        title: 'Matched movie',
        duration: 120,
        container: 'matroska',
        videoCodec: 'h264',
        audioCodec: 'aac',
        size: 100,
        metadata: { description: 'Embedded description', chapters: [] },
      }}
      metadata={{
        details: {
          year: 2025,
          synopsis: 'Matched description',
          genres: ['Drama'],
          cast: ['An Actor'],
          series: null,
          season: null,
          episode: null,
          attribution: 'Metadata provider',
        },
        posterUrl: null,
        backdropUrl: null,
      }}
      resume={0}
      onPlay={vi.fn()}
      onResume={vi.fn()}
    />,
  );
  expect(screen.getByText('Matched description')).toBeInTheDocument();
  expect(screen.queryByText('Embedded description')).not.toBeInTheDocument();
  expect(screen.getByText('Cast: An Actor')).toBeInTheDocument();
});
