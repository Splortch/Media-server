import { describe, it, expect, vi } from 'vitest';
import {
  ClientApi,
  ProgressWriter,
  serverInfo,
  type Transport,
} from '../../../shared/api/src/client';
describe('client contract', () => {
  it('rejects incompatible server responses', () => {
    expect(() => serverInfo({ apiVersion: 2 })).toThrow();
  });
  it('serializes revisions and stops after conflicts', async () => {
    const request = vi
      .fn()
      .mockResolvedValueOnce({ revision: 1 })
      .mockRejectedValueOnce({ code: 'CONFLICT' });
    const transport = { request, streamUrl: vi.fn() } as Transport;
    const writer = new ProgressWriter(new ClientApi(transport), 4, 0);
    await writer.save(10);
    await expect(writer.save(20)).rejects.toEqual({ code: 'CONFLICT' });
    await expect(writer.save(30)).rejects.toThrow('Reopen');
    expect(request).toHaveBeenNthCalledWith(
      2,
      'PUT',
      '/api/v1/media/4/progress-sync',
      { seconds: 20, completed: false, expectedRevision: 1 },
    );
    expect(request).toHaveBeenCalledTimes(2);
  });
  it('rejects malicious library field types', async () => {
    const transport = {
      request: vi.fn().mockResolvedValue([{ id: 1, title: { bad: true } }]),
      streamUrl: vi.fn(),
    } as Transport;
    await expect(new ClientApi(transport).media()).rejects.toThrow(
      'Invalid media',
    );
  });
});

it('reports the real desktop engine while preserving the Windows defaults', async () => {
  const request = vi.fn().mockResolvedValue(undefined);
  const api = new ClientApi({ request, streamUrl: vi.fn() } as Transport);
  await api.capabilities(['mp4'], [], {
    platform: 'macos',
    player: 'WKWebView',
  });
  expect(request).toHaveBeenLastCalledWith('PUT', '/api/v1/capabilities', {
    platform: 'macos',
    player: 'WKWebView',
    containers: ['mp4'],
    codecs: [],
  });
  await api.report(3, 'decode', 'macos-wkwebview');
  expect(request).toHaveBeenLastCalledWith(
    'POST',
    '/api/v1/media/3/playback-errors',
    {
      code: 'decode',
      client: 'macos-wkwebview',
    },
  );
  await api.capabilities(['webm'], []);
  expect(request).toHaveBeenLastCalledWith('PUT', '/api/v1/capabilities', {
    platform: 'windows',
    player: 'WebView2',
    containers: ['webm'],
    codecs: [],
  });
});
