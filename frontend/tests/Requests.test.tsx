import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import Requests from '../src/pages/Requests';
import { api } from '../src/services/api';
vi.mock('../src/services/api', () => ({
  api: vi.fn(),
  serverTime: (value: string) => new Date(value + 'Z'),
}));
afterEach(() => vi.resetAllMocks());
const item = {
  id: 4,
  libraryId: 1,
  library: 'Family',
  title: 'Future movie',
  year: 2020,
  note: 'Movie night',
  state: 'Requested',
  reply: '',
  mediaItemId: null,
  mine: false,
  supportCount: 1,
  supported: false,
  revision: 2,
  updatedAt: '2026-10-30T12:00:00',
};
function setup() {
  vi.mocked(api).mockImplementation(async (path, method = 'GET') => {
    if (path === '/browse-libraries') return [{ id: 1, name: 'Family' }];
    if (path.startsWith('/media-page'))
      return { items: [{ id: 7, title: 'Actual added movie' }] };
    if (path.startsWith('/requests') && method === 'GET') return [item];
    return undefined;
  });
}
it('submits a real request with scoped library and optional fields', async () => {
  setup();
  render(<Requests admin={false} />);
  await screen.findByRole('heading', { name: 'Future movie (2020)' });
  fireEvent.change(screen.getByLabelText('Movie title'), {
    target: { value: 'Another movie' },
  });
  fireEvent.change(screen.getByLabelText('Year (optional)'), {
    target: { value: '2025' },
  });
  fireEvent.change(screen.getByLabelText('Note (optional)'), {
    target: { value: 'For Friday' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Send movie request' }));
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/requests', 'POST', {
      libraryId: 1,
      title: 'Another movie',
      year: 2025,
      note: 'For Friday',
    }),
  );
  expect(await screen.findByText(/Request sent/)).toBeInTheDocument();
  expect(screen.queryByText(/Review request:/)).toBeNull();
});
it('supports visible requests and preserves useful server errors', async () => {
  setup();
  render(<Requests admin={false} />);
  const button = await screen.findByRole('button', { name: 'Support request' });
  vi.mocked(api).mockRejectedValueOnce(
    new Error('This request is no longer visible.'),
  );
  fireEvent.click(button);
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'no longer visible',
  );
  expect(screen.queryByText('Request supported.')).toBeNull();
});
it('requires an explicit title selection when an administrator marks a movie added', async () => {
  setup();
  render(<Requests admin />);
  fireEvent.click(await screen.findByText('Review request: Future movie'));
  fireEvent.change(screen.getByLabelText('Status'), {
    target: { value: 'Added' },
  });
  const selection = await screen.findByLabelText('Confirm library title');
  await screen.findByRole('option', { name: 'Actual added movie' });
  expect(selection).toHaveValue('');
  fireEvent.change(selection, { target: { value: '7' } });
  fireEvent.change(screen.getByLabelText('Reply'), {
    target: { value: 'Ready for Friday' },
  });
  fireEvent.click(
    screen.getByRole('button', { name: 'Save review for Future movie' }),
  );
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/requests/4', 'PUT', {
      state: 'Added',
      mediaItemId: 7,
      reply: 'Ready for Friday',
      revision: 2,
    }),
  );
});
