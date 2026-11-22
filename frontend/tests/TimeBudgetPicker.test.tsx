import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import TimeBudgetPicker from '../src/components/TimeBudgetPicker';

type Request = Parameters<typeof TimeBudgetPicker>[0]['request'];

it('uses custom time, speed and the progress filter and opens the selected episode', async () => {
  const request = vi.fn<Request>().mockResolvedValueOnce({
    items: [
      {
        id: 14,
        title: 'Tuesday',
        series: 'Local series',
        season: 2,
        episode: 1,
        remainingSeconds: 2420,
        started: false,
      },
    ],
    hasMore: false,
  });
  const open = vi.fn();
  render(<TimeBudgetPicker request={request} onOpen={open} />);
  fireEvent.change(screen.getByLabelText('Time available'), {
    target: { value: 'custom' },
  });
  fireEvent.change(screen.getByLabelText('Minutes'), {
    target: { value: '42' },
  });
  fireEvent.change(screen.getByLabelText('Playback speed estimate'), {
    target: { value: '1.5' },
  });
  fireEvent.click(screen.getByRole('checkbox'));
  fireEvent.click(
    screen.getByRole('button', { name: 'Find something to watch' }),
  );
  const episode = await screen.findByRole('button', {
    name: 'Local series · S2 E1 · Tuesday',
  });
  expect(request).toHaveBeenCalledExactlyOnceWith(
    '/time-budget?minutes=42&speed=1.5&includeStarted=false',
  );
  expect(screen.getByText('About 41 min left')).toBeInTheDocument();
  fireEvent.click(episode);
  expect(open).toHaveBeenCalledExactlyOnceWith(14);
});

it('shows a retryable error and distinguishes it from an empty result', async () => {
  const request = vi
    .fn<Request>()
    .mockRejectedValueOnce(new Error('Offline'))
    .mockResolvedValueOnce({ items: [], hasMore: false });
  render(<TimeBudgetPicker request={request} onOpen={vi.fn()} />);
  const find = screen.getByRole('button', { name: 'Find something to watch' });
  fireEvent.click(find);
  expect(await screen.findByRole('alert')).toHaveTextContent('Try again');
  fireEvent.click(find);
  expect(
    await screen.findByText(/No unwatched titles fit/),
  ).toBeInTheDocument();
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
});
