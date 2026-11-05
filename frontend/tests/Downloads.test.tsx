import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import Downloads, {
  DownloadSettings,
} from '../src/features/downloads/Downloads';
import { api } from '../src/services/api';
vi.mock('../src/services/api', () => ({ api: vi.fn() }));
afterEach(() => vi.resetAllMocks());
const job = {
  id: 'fixture',
  name: 'Sample',
  sourceHost: 'media.example',
  state: 'running',
  desired: 'run',
  priority: 0,
  receivedBytes: 1024,
  totalBytes: 4096,
  budgetBytes: 8192,
  contentHash: null,
  error: null,
  revision: 3,
  canResume: true,
};
it('submits a real source request and clears the secret input after acceptance', async () => {
  vi.mocked(api).mockResolvedValue([]);
  render(<Downloads admin />);
  await screen.findByText('No downloads yet.');
  fireEvent.change(screen.getByLabelText('Download name'), {
    target: { value: 'Authorized movie' },
  });
  fireEvent.change(screen.getByLabelText('HTTPS source URL'), {
    target: { value: 'https://media.example/movie?token=synthetic' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Add download' }));
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/downloads', 'POST', {
      name: 'Authorized movie',
      url: 'https://media.example/movie?token=synthetic',
      budgetBytes: 1073741824,
      priority: 0,
      sha256: null,
      transferClient: 'builtin',
    }),
  );
  await waitFor(() =>
    expect(screen.getByLabelText('HTTPS source URL')).toHaveValue(''),
  );
});
it('sends revision protected pause and resume', async () => {
  let row = { ...job };
  vi.mocked(api).mockImplementation(async (_path, method = 'GET', body) => {
    if (method === 'PUT')
      row = {
        ...row,
        state:
          (body as { action: string }).action === 'pause'
            ? 'paused'
            : 'running',
        revision: row.revision + 1,
      };
    return [row];
  });
  render(<Downloads admin />);
  fireEvent.click(await screen.findByRole('button', { name: 'Pause Sample' }));
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/downloads/fixture', 'PUT', {
      action: 'pause',
      expectedRevision: 3,
      priority: undefined,
    }),
  );
  fireEvent.click(await screen.findByRole('button', { name: 'Resume Sample' }));
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/downloads/fixture', 'PUT', {
      action: 'resume',
      expectedRevision: 4,
      priority: undefined,
    }),
  );
});
it('reports server failures and blocks queue creation until loaded', async () => {
  vi.mocked(api).mockRejectedValue(new Error('Server unavailable'));
  render(<Downloads admin />);
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'Server unavailable',
  );
  expect(screen.getByRole('button', { name: 'Add download' })).toBeDisabled();
});
it('saves explicit opt in and refreshes the feature registry', async () => {
  const settings = {
    enabled: false,
    hosts: [],
    availableBytes: 2e9,
    reservedBytes: 0,
    safetyMarginBytes: 512 * 1024 * 1024,
    stagingLimitBytes: 100e9,
  };
  vi.mocked(api).mockImplementation(async (path) =>
    path === '/features' ? { features: [] } : settings,
  );
  const changed = vi.fn();
  render(<DownloadSettings onChange={changed} />);
  fireEvent.click(await screen.findByLabelText('Enable download center'));
  fireEvent.change(screen.getByLabelText('Approved source hosts'), {
    target: { value: 'media.example\nredirect.example' },
  });
  fireEvent.click(
    screen.getByRole('button', { name: 'Save download settings' }),
  );
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/downloads/settings', 'PUT', {
      enabled: true,
      hosts: ['media.example', 'redirect.example'],
    }),
  );
  await waitFor(() =>
    expect(changed).toHaveBeenCalledWith({ features: [], available: true }),
  );
});

it('keeps the download center inaccessible to non-admins without requesting private data', () => {
  render(<Downloads admin={false} />);
  expect(screen.getByText(/administrator account/)).toBeInTheDocument();
  expect(api).not.toHaveBeenCalled();
});
it('sends the chosen library plan together with the initial download', async () => {
  vi.mocked(api).mockImplementation(async (path) =>
    path === '/browse-libraries'
      ? [{ id: 7, name: 'Movies', kind: 'movies' }]
      : [],
  );
  render(<Downloads admin />);
  await screen.findByText('No downloads yet.');
  fireEvent.change(screen.getByLabelText('Download name'), {
    target: { value: 'A movie' },
  });
  await waitFor(() =>
    expect(
      screen.getByRole('option', {
        name: 'Add to a media library automatically',
      }),
    ).not.toBeDisabled(),
  );
  fireEvent.change(screen.getByLabelText('After downloading'), {
    target: { value: 'library' },
  });
  expect(screen.getByLabelText('Confirmed title')).toHaveValue('A movie');
  fireEvent.change(screen.getByLabelText('HTTPS source URL'), {
    target: { value: 'https://media.example/movie.mp4' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Add download' }));
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith(
      '/downloads',
      'POST',
      expect.objectContaining({
        name: 'A movie',
        importPlan: expect.objectContaining({
          libraryId: 7,
          classification: 'movies',
          title: 'A movie',
          extension: '.mp4',
        }),
      }),
    ),
  );
});

it('finds downloads by destination library and separates paused transfers', async () => {
  vi.mocked(api).mockImplementation(async (path) =>
    path === '/browse-libraries'
      ? [{ id: 7, name: 'Family films', kind: 'movies' }]
      : [
          { ...job, import: null },
          {
            ...job,
            id: 'movie',
            name: 'Another film',
            state: 'paused',
            import: {
              libraryId: 7,
              title: 'Matched film',
              year: 2020,
              state: 'waiting',
              classification: 'movies',
              mediaItemId: null,
            },
          },
        ],
  );
  render(<Downloads admin />);
  await screen.findByRole('button', { name: 'Pause Sample' });
  fireEvent.change(screen.getByLabelText('Search downloads'), {
    target: { value: 'Family films' },
  });
  expect(
    screen.queryByRole('button', { name: 'Pause Sample' }),
  ).not.toBeInTheDocument();
  expect(screen.getByText(/Destination: Family films/)).toHaveTextContent(
    'Matched film (2020)',
  );
  fireEvent.change(screen.getByLabelText('Show downloads'), {
    target: { value: 'paused' },
  });
  expect(
    screen.getByRole('button', { name: 'Resume Another film' }),
  ).toBeInTheDocument();
  fireEvent.change(screen.getByLabelText('Search downloads'), {
    target: { value: 'missing' },
  });
  expect(
    screen.getByText('No downloads match your search and category.'),
  ).toBeInTheDocument();
});

it('offers only the configured external client and requires a checksum for it', async () => {
  vi.mocked(api).mockImplementation(
    async (path) =>
      (path === '/downloads/clients'
        ? [{ id: 'aria2', configured: true }]
        : []) as never,
  );
  render(<Downloads admin />);
  await screen.findByText('No downloads yet.');
  fireEvent.change(await screen.findByLabelText('Transfer client'), {
    target: { value: 'aria2' },
  });
  const checksum = screen.getByLabelText(
    'SHA-256 checksum (required for aria2)',
  );
  expect(checksum).toBeRequired();
  fireEvent.change(screen.getByLabelText('Download name'), {
    target: { value: 'External fixture' },
  });
  fireEvent.change(screen.getByLabelText('HTTPS source URL'), {
    target: { value: 'https://media.example/file' },
  });
  fireEvent.change(checksum, { target: { value: 'a'.repeat(64) } });
  fireEvent.click(screen.getByRole('button', { name: 'Add download' }));
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith(
      '/downloads',
      'POST',
      expect.objectContaining({
        transferClient: 'aria2',
        sha256: 'a'.repeat(64),
      }),
    ),
  );
});
