import { render, screen, cleanup, act } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import Monitoring from '../src/features/admin/Monitoring';
import { api } from '../src/services/api';
vi.mock('../src/services/api', () => ({ api: vi.fn() }));
afterEach(() => {
  cleanup();
  vi.resetAllMocks();
  vi.useRealTimers();
});
const sample = {
  resources: {
    processCpuPercent: null,
    processMemoryBytes: 1024,
    managedMemoryBytes: 512,
    uptimeSeconds: 60,
    dataDiskAvailableBytes: null,
    dataDiskTotalBytes: null,
    gpuStatus: '',
  },
  gpu: { status: 'Disabled by operator', devices: [] },
  activeHttpStreams: [],
  queuedScans: 0,
  runningScans: 0,
  transcodingSupported: true,
  transcodingQueueCount: 0,
  transcoding: {
    capacity: 2,
    preparing: 1,
    encoding: 1,
    ready: 0,
    failed: 0,
    jobs: [
      {
        mediaId: 4,
        state: 'preparing',
        phase: 'encoding',
        encoder: 'libx264',
        rendition: '480p',
        completedRenditions: 1,
        elapsedSeconds: 2,
        progress: {
          frames: 48,
          framesPerSecond: 24,
          outputSeconds: 2,
          speed: 1,
        },
      },
    ],
  },
};
it('shows real conversion rates without presenting cache entries as active playback', async () => {
  vi.mocked(api).mockResolvedValue(sample);
  render(<Monitoring />);
  expect(await screen.findByText(/48 frames/)).toHaveTextContent('24.0 FPS');
  expect(screen.getByText(/48 frames/)).toHaveTextContent('1.00×');
  expect(screen.getByText('Disabled by operator')).toBeInTheDocument();
  expect(screen.getByText(/excess jobs are rejected/)).toBeInTheDocument();
  expect(screen.getByText(/not active viewers/)).toBeInTheDocument();
  expect(screen.queryByText(/Direct Play only/)).toBeNull();
});
it('shows authorization failures without leaking previous metrics', async () => {
  vi.mocked(api).mockRejectedValue(new Error('Administrator access required.'));
  render(<Monitoring />);
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'Administrator access required.',
  );
});

it('clears a previous private sample when the next poll loses authorization', async () => {
  vi.useFakeTimers();
  vi.mocked(api)
    .mockResolvedValueOnce(sample)
    .mockRejectedValue(new Error('Access revoked.'));
  await act(async () => {
    render(<Monitoring />);
  });
  expect(screen.getByText(/48 frames/)).toBeInTheDocument();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(5000);
  });
  expect(screen.getByRole('alert')).toHaveTextContent('Access revoked.');
  expect(screen.queryByText(/48 frames/)).toBeNull();
  expect(screen.queryByText('Disabled by operator')).toBeNull();
});
