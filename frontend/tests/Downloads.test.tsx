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
  render(<Downloads />);
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
  render(<Downloads />);
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
  render(<Downloads />);
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
