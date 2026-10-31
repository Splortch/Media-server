import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import Bookmarks, { type BookmarkRequest } from '../src/components/Bookmarks';
it('manages private notes with named controls, jumps explicitly and preserves literal text', async () => {
  let rows: {
    id: string;
    seconds: number;
    name: string;
    note: string;
    revision: number;
  }[] = [];
  const run = vi.fn(async (_path: string, method = 'GET', body?: unknown) => {
    if (method === 'POST')
      rows = [
        { ...(body as (typeof rows)[number]), id: 'a'.repeat(32), revision: 1 },
      ];
    if (method === 'PUT')
      rows = [
        { ...(body as (typeof rows)[number]), id: 'a'.repeat(32), revision: 2 },
      ];
    if (method === 'DELETE') rows = [];
    return JSON.parse(JSON.stringify(rows));
  });
  const jump = vi.fn();
  render(
    <Bookmarks
      id={4}
      duration={100}
      onSeek={jump}
      currentPosition={() => 32.5}
      request={run as BookmarkRequest}
    />,
  );
  expect(run).not.toHaveBeenCalled();
  fireEvent.click(screen.getByText('Private bookmarks and notes'));
  await waitFor(() => expect(run).toHaveBeenCalledWith('/media/4/bookmarks'));
  fireEvent.change(screen.getByLabelText('Bookmark name'), {
    target: { value: 'Scene' },
  });
  fireEvent.change(screen.getByLabelText('Private note'), {
    target: { value: '<script>private</script>' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Use current position' }));
  expect(screen.getByLabelText('Timestamp in seconds')).toHaveValue(32.5);
  fireEvent.click(screen.getByRole('button', { name: 'Add bookmark' }));
  const seek = await screen.findByRole('button', {
    name: 'Jump to Scene at 32.5 seconds',
  });
  expect(document.querySelector('script')).toBeNull();
  expect(jump).not.toHaveBeenCalled();
  fireEvent.click(seek);
  expect(jump).toHaveBeenCalledWith(32.5);
  fireEvent.click(screen.getByRole('button', { name: 'Edit Scene' }));
  expect(screen.getByLabelText('Bookmark name')).toHaveFocus();
  fireEvent.change(screen.getByLabelText('Bookmark name'), {
    target: { value: 'Renamed' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Save bookmark' }));
  await screen.findByRole('button', { name: 'Edit Renamed' });
  fireEvent.click(screen.getByRole('button', { name: 'Delete Renamed' }));
  await waitFor(() =>
    expect(
      screen.queryByRole('button', { name: 'Edit Renamed' }),
    ).not.toBeInTheDocument(),
  );
  expect(screen.getByText('Private bookmarks and notes')).toHaveFocus();
});
it('reports failed imports without replacing visible notes or automatically seeking', async () => {
  const run = vi.fn(async (_path: string, method = 'GET') => {
    if (method === 'POST') throw new Error('Wrong content hash');
    return [];
  });
  const seek = vi.fn();
  render(
    <Bookmarks
      id={1}
      duration={100}
      onSeek={seek}
      request={run as BookmarkRequest}
    />,
  );
  fireEvent.click(screen.getByText('Private bookmarks and notes'));
  await waitFor(() => expect(run).toHaveBeenCalled());
  fireEvent.click(screen.getByText('Portable export and import'));
  fireEvent.change(screen.getByLabelText('Portable bookmark JSON'), {
    target: { value: '{"version":1}' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Import bookmarks' }));
  await expect(screen.findByRole('alert')).resolves.toHaveTextContent(
    'Wrong content hash',
  );
  expect(seek).not.toHaveBeenCalled();
});
