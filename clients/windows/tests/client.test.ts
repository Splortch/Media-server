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
