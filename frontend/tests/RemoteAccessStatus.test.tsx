import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import RemoteAccessStatus from '../src/features/admin/RemoteAccessStatus';
import { api } from '../src/services/api';
vi.mock('../src/services/api', () => ({ api: vi.fn() }));
afterEach(() => vi.resetAllMocks());
it('shows actual local-only readiness without claiming configuration was changed', async () => {
  vi.mocked(api).mockResolvedValue({
    configured: false,
    requestIsHttps: false,
    secureCookies: false,
    trustedProxyCount: 0,
    warnings: ['Remote access is not configured.'],
  });
  render(<RemoteAccessStatus />);
  expect(
    await screen.findByText('Local setup · remote access not configured'),
  ).toBeInTheDocument();
  expect(
    screen.getByText('Remote access is not configured.'),
  ).toBeInTheDocument();
  expect(api).toHaveBeenCalledWith('/remote-readiness');
  expect(screen.queryByText(/checks passed/)).toBeNull();
});
it('reports failures and retries instead of assuming HTTPS is ready', async () => {
  vi.mocked(api)
    .mockRejectedValueOnce(new Error('Server unavailable'))
    .mockResolvedValueOnce({
      configured: true,
      requestIsHttps: true,
      secureCookies: true,
      trustedProxyCount: 1,
      warnings: [],
    });
  render(<RemoteAccessStatus />);
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'Server unavailable',
  );
  fireEvent.click(screen.getByRole('button', { name: 'Retry remote status' }));
  expect(
    await screen.findByText('HTTPS origin configured'),
  ).toBeInTheDocument();
  expect(screen.queryByRole('alert')).toBeNull();
});
