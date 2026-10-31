import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import {
  ApproveDownload,
  ImportFields,
  importPlan,
} from '../src/features/downloads/ImportForm';
import { api } from '../src/services/api';
vi.mock('../src/services/api', () => ({ api: vi.fn() }));
afterEach(() => vi.resetAllMocks());
const request = {
  id: 9,
  revision: 3,
  title: 'Family movie',
  year: 2026,
  libraryId: 1,
};
it('requires explicit administrator source, library, rating and budget approval', async () => {
  vi.mocked(api).mockResolvedValue([{ id: 1, name: 'Family', kind: 'movies' }]);
  const approved = vi.fn();
  render(<ApproveDownload request={request} onApproved={approved} />);
  fireEvent.click(
    screen.getByText('Approve authorized download for Family movie'),
  );
  fireEvent.change(await screen.findByLabelText('Authorized HTTPS source'), {
    target: { value: 'https://source.example/movie?key=synthetic' },
  });
  fireEvent.change(screen.getByLabelText('Download size budget (MiB)'), {
    target: { value: '50' },
  });
  fireEvent.change(screen.getByLabelText('Approved content rating'), {
    target: { value: '10' },
  });
  fireEvent.click(
    screen.getByRole('button', { name: 'Approve download and delivery' }),
  );
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/requests/9/download', 'POST', {
      requestRevision: 3,
      source: {
        name: 'Family movie',
        url: 'https://source.example/movie?key=synthetic',
        budgetBytes: 50 * 1024 * 1024,
        sha256: null,
        priority: 0,
      },
      plan: {
        libraryId: 1,
        classification: 'movies',
        title: 'Family movie',
        year: 2026,
        rating: 10,
        extension: '.mp4',
        series: null,
        season: null,
        episode: null,
      },
    }),
  );
  await waitFor(() => expect(approved).toHaveBeenCalled());
  expect(screen.getByLabelText('Authorized HTTPS source')).toHaveValue('');
});
it('declines with an explicit reason and revision', async () => {
  vi.mocked(api).mockResolvedValue([{ id: 1, name: 'Family', kind: 'movies' }]);
  const done = vi.fn();
  render(<ApproveDownload request={request} onApproved={done} />);
  fireEvent.click(
    screen.getByText('Approve authorized download for Family movie'),
  );
  fireEvent.change(screen.getByLabelText('Reason for declining'), {
    target: { value: 'No authorized source available.' },
  });
  fireEvent.click(
    screen.getByRole('button', { name: 'Decline download request' }),
  );
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/requests/9/download/decline', 'POST', {
      revision: 3,
      reason: 'No authorized source available.',
    }),
  );
});
it('leaves unknown episode matching blank instead of guessing from its name', () => {
  render(
    <form>
      <ImportFields
        libraries={[{ id: 2, name: 'TV', kind: 'tv' }]}
        requestLibrary={2}
        initial={{ title: 'Unknown episode' }}
      />
    </form>,
  );
  expect(screen.getByLabelText('Confirmed series')).toHaveValue('');
  expect(screen.getByLabelText('Season')).toHaveValue(null);
  expect(screen.getByLabelText('Episode')).toHaveValue(null);
  const data = new FormData(document.querySelector('form')!);
  expect(importPlan(data)).toMatchObject({
    classification: 'tv',
    series: null,
    season: null,
    episode: null,
  });
});
it('reports stale approvals without pretending a transfer started', async () => {
  vi.mocked(api).mockImplementation(async (_path, method) => {
    if (method === 'POST')
      throw new Error('This request was already reviewed.');
    return [{ id: 1, name: 'Family', kind: 'movies' }];
  });
  const done = vi.fn();
  render(<ApproveDownload request={request} onApproved={done} />);
  fireEvent.click(
    screen.getByText('Approve authorized download for Family movie'),
  );
  fireEvent.change(await screen.findByLabelText('Authorized HTTPS source'), {
    target: { value: 'https://source.example/movie' },
  });
  fireEvent.click(
    screen.getByRole('button', { name: 'Approve download and delivery' }),
  );
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'already reviewed',
  );
  expect(done).not.toHaveBeenCalled();
});
