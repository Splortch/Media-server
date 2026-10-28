import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, it, expect } from 'vitest';
import App from '../src/App';
import * as bridge from '../src/bridge';
vi.mock('../src/bridge', () => ({
  preferences: vi.fn().mockResolvedValue({}),
  desktopCapabilities: vi
    .fn()
    .mockResolvedValue({ os: 'windows', persistentSessions: true }),
  savePreferences: vi.fn().mockResolvedValue(undefined),
  connect: vi.fn(),
  login: vi.fn(),
  logout: vi.fn(),
  errorMessage: (e: { message?: string }) => e.message || 'Connection failed',
  expired: () => false,
  api: {
    media: vi.fn().mockResolvedValue([]),
    continuing: vi.fn().mockResolvedValue([]),
    me: vi.fn(),
    capabilities: vi.fn().mockResolvedValue(undefined),
  },
}));
it('shows rejected server connections without presenting a fake library', async () => {
  vi.mocked(bridge.connect).mockRejectedValueOnce({
    message: 'Remote servers require HTTPS.',
  });
  render(<App />);
  fireEvent.change(screen.getByLabelText('Server address'), {
    target: { value: 'http://example.org' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Connect' }));
  await expect(screen.findByRole('alert')).resolves.toHaveTextContent('HTTPS');
  expect(
    screen.queryByRole('heading', { name: 'Library' }),
  ).not.toBeInTheDocument();
});
it('keeps passwords out of persistent browser storage', async () => {
  vi.mocked(bridge.connect).mockResolvedValueOnce({
    name: 'Test fixture',
    serverVersion: '0.2.0-alpha.1',
    apiVersion: 1,
    minimumClientApiVersion: 1,
    setupRequired: false,
    features: [],
  });
  vi.mocked(bridge.login).mockRejectedValueOnce({
    message: 'Sign-in rejected',
  });
  const persisted = vi.spyOn(Storage.prototype, 'setItem');
  render(<App />);
  fireEvent.change(screen.getByLabelText('Server address'), {
    target: { value: 'https://fixture.example' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Connect' }));
  await screen.findByLabelText('Password');
  fireEvent.change(screen.getByLabelText('Username'), {
    target: { value: 'test' },
  });
  fireEvent.change(screen.getByLabelText('Password'), {
    target: { value: 'test-only-password' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  await waitFor(() =>
    expect(screen.getByRole('alert')).toHaveTextContent('Sign-in rejected'),
  );
  expect(screen.getByLabelText('Password')).toHaveValue('');
  expect(persisted).not.toHaveBeenCalled();
  persisted.mockRestore();
});
