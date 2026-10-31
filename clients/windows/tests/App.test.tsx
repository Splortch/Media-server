import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, it, expect, beforeEach } from 'vitest';
import App from '../src/App';
import * as bridge from '../src/bridge';
vi.mock('../src/bridge', () => ({
  preferences: vi.fn().mockResolvedValue({}),
  playbackRequest: vi.fn(),
  bookmarkRequest: vi.fn().mockResolvedValue([]),
  playbackFileUrl: vi.fn(),
  desktopCapabilities: vi
    .fn()
    .mockResolvedValue({ os: 'windows', persistentSessions: true }),
  savePreferences: vi.fn().mockResolvedValue(undefined),
  connect: vi.fn(),
  login: vi.fn(),
  logout: vi.fn(),
  forgetSavedSession: vi.fn().mockResolvedValue(undefined),
  errorMessage: (e: { message?: string }) => e.message || 'Connection failed',
  expired: () => false,
  api: {
    media: vi.fn().mockResolvedValue([]),
    continuing: vi.fn().mockResolvedValue([]),
    me: vi.fn(),
    detail: vi.fn(),
    progress: vi.fn(),
    tracks: vi.fn(),
    transport: { streamUrl: vi.fn() },
    capabilities: vi.fn().mockResolvedValue(undefined),
  },
}));
beforeEach(() => {
  localStorage.clear();
  vi.clearAllMocks();
  vi.mocked(bridge.connect).mockReset();
  vi.mocked(bridge.login).mockReset();
});

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
  expect(JSON.stringify(persisted.mock.calls)).not.toContain(
    'test-only-password',
  );
  expect(
    JSON.parse(localStorage.getItem('media-server.saved-servers.v1')!),
  ).toEqual([{ name: 'fixture.example', address: 'https://fixture.example' }]);
  persisted.mockRestore();
});

it('opens title details before requesting a playback URL and resumes only after an explicit action', async () => {
  const media = {
    id: 7,
    libraryId: 1,
    title: 'Own video',
    duration: 100,
    size: 100,
    container: 'webm',
    videoCodec: 'vp9',
    audioCodec: 'opus',
  };
  vi.mocked(bridge.connect).mockResolvedValueOnce({
    name: 'Fixture',
    serverVersion: 'fixture',
    apiVersion: 1,
    minimumClientApiVersion: 1,
    setupRequired: false,
    features: [],
  });
  vi.mocked(bridge.login).mockResolvedValueOnce({
    name: 'Alice',
    role: 'Viewer',
  });
  vi.mocked(bridge.api.media).mockResolvedValue([media]);
  vi.mocked(bridge.api.detail).mockResolvedValue({
    ...media,
    contentRating: 0,
    playback: { mode: 'direct', mimeType: 'video/webm', warning: null },
  });
  vi.mocked(bridge.api.progress).mockResolvedValue({
    mediaItemId: 7,
    seconds: 32,
    revision: 3,
    completed: false,
    updatedAt: null,
  });
  vi.mocked(bridge.api.tracks).mockResolvedValue({
    tracks: [],
    canSelectAudio: false,
    canSelectSubtitles: false,
  });
  vi.mocked(bridge.api.transport.streamUrl).mockResolvedValue(
    'http://127.0.0.1:1234/media/7',
  );
  render(<App />);
  fireEvent.change(screen.getByLabelText('Server address'), {
    target: { value: 'https://fixture.example' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Connect' }));
  await screen.findByLabelText('Password');
  fireEvent.change(screen.getByLabelText('Username'), {
    target: { value: 'Alice' },
  });
  fireEvent.change(screen.getByLabelText('Password'), {
    target: { value: 'fixture-password' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  fireEvent.click(await screen.findByRole('heading', { name: 'Own video' }));
  await screen.findByRole('region', { name: 'Title information' });
  expect(document.querySelector('video')).toBeNull();
  expect(bridge.api.transport.streamUrl).not.toHaveBeenCalled();
  await waitFor(() =>
    expect(screen.getByRole('heading', { name: 'Own video' })).toHaveFocus(),
  );
  fireEvent.click(screen.getByRole('button', { name: 'Resume from 32 sec' }));
  await screen.findByLabelText('Own video');
  expect(bridge.api.transport.streamUrl).toHaveBeenCalledExactlyOnceWith(7);
  expect(bridge.api.progress).toHaveBeenCalledTimes(2);
});

it('saves named servers, clears a connection when switching and removes saved addresses', async () => {
  vi.mocked(bridge.logout).mockResolvedValue(undefined);
  vi.mocked(bridge.connect).mockResolvedValue({
    name: 'Fixture',
    serverVersion: 'fixture',
    apiVersion: 1,
    minimumClientApiVersion: 1,
    setupRequired: false,
    features: [],
  });
  render(<App />);
  fireEvent.change(screen.getByLabelText('Server name (optional)'), {
    target: { value: 'Home' },
  });
  fireEvent.change(screen.getByLabelText('Server address'), {
    target: { value: 'https://home.example' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Connect' }));
  await screen.findByLabelText('Password');
  fireEvent.click(screen.getByRole('button', { name: 'Change server' }));
  await screen.findByRole('button', { name: 'Use Home' });
  expect(bridge.logout).toHaveBeenCalled();
  expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText('Server name (optional)'), {
    target: { value: 'Family' },
  });
  fireEvent.change(screen.getByLabelText('Server address'), {
    target: { value: 'https://family.example' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Connect' }));
  await screen.findByLabelText('Password');
  fireEvent.click(screen.getByRole('button', { name: 'Change server' }));
  await screen.findByRole('button', { name: 'Use Family' });
  fireEvent.click(screen.getByRole('button', { name: 'Use Home' }));
  expect(screen.getByLabelText('Server address')).toHaveValue(
    'https://home.example',
  );
  fireEvent.click(screen.getByRole('button', { name: 'Forget Family' }));
  await waitFor(() =>
    expect(
      screen.queryByRole('button', { name: 'Use Family' }),
    ).not.toBeInTheDocument(),
  );
  expect(bridge.forgetSavedSession).toHaveBeenCalledWith(
    'https://family.example',
  );
  expect(localStorage.getItem('media-server.saved-servers.v1')).not.toContain(
    'family.example',
  );
});

it('keeps the saved address when the secure store cannot clear its credentials', async () => {
  localStorage.setItem(
    'media-server.saved-servers.v1',
    JSON.stringify([{ name: 'Home', address: 'https://home.example' }]),
  );
  vi.mocked(bridge.forgetSavedSession).mockRejectedValueOnce(
    new Error('Vault unavailable'),
  );
  render(<App />);
  fireEvent.click(screen.getByRole('button', { name: 'Forget Home' }));
  await expect(screen.findByRole('alert')).resolves.toHaveTextContent(
    'local credentials',
  );
  expect(screen.getByRole('button', { name: 'Use Home' })).toBeInTheDocument();
});
