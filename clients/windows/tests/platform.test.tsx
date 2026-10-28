import { render, screen, waitFor } from '@testing-library/react';
import { it, expect, vi } from 'vitest';
import App from '../src/App';
import { desktopCapabilities } from '../src/bridge';
vi.mock('../src/bridge', () => ({
  desktopCapabilities: vi.fn(),
  preferences: vi.fn().mockResolvedValue({}),
  api: {},
}));
it('does not offer unavailable persistent session storage on Linux or macOS', async () => {
  vi.mocked(desktopCapabilities).mockResolvedValue({
    os: 'linux',
    persistentSessions: false,
  });
  render(<App />);
  const remember = screen.getByRole('checkbox');
  await waitFor(() => expect(desktopCapabilities).toHaveBeenCalled());
  expect(remember).toBeDisabled();
  expect(remember).not.toBeChecked();
  expect(screen.getByText(/Sessions stay in memory/)).toBeInTheDocument();
});
it('preserves the Windows Credential Manager option', async () => {
  vi.mocked(desktopCapabilities).mockResolvedValue({
    os: 'windows',
    persistentSessions: true,
  });
  render(<App />);
  await waitFor(() => expect(screen.getByRole('checkbox')).toBeEnabled());
  expect(
    screen.getByText(/Remember this session in Windows Credential Manager/),
  ).toBeInTheDocument();
});
