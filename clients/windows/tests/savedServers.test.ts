import { describe, it, expect } from 'vitest';
import {
  parseSavedServers,
  rememberServer,
  serverOrigin,
  writeSavedServers,
  readSavedServers,
} from '../src/savedServers';
describe('saved server metadata', () => {
  it('deduplicates origins, supports naming and survives reloading', () => {
    const first = rememberServer([], 'https://HOME.example/', 'Home');
    const second = rememberServer(
      first,
      'https://other.example:8443',
      'Family',
    );
    const renamed = rememberServer(
      second,
      'https://home.example',
      'Living room',
    );
    writeSavedServers(renamed);
    expect(readSavedServers()).toEqual([
      { name: 'Living room', address: 'https://home.example' },
      { name: 'Family', address: 'https://other.example:8443' },
    ]);
  });
  it('rejects unsafe addresses rather than preserving credentials or weakening HTTPS', () => {
    for (const address of [
      'https://user:password@example.org',
      'http://192.168.1.5',
      'https://example.org/api',
      'https://example.org?token=secret',
      'https://example.org#secret',
      'file:///etc/passwd',
    ])
      expect(() => serverOrigin(address)).toThrow();
    expect(serverOrigin('http://127.0.0.1:5050')).toBe('http://127.0.0.1:5050');
  });
  it('handles corrupt data and bounds untrusted stored metadata', () => {
    expect(parseSavedServers('{invalid')).toEqual([]);
    expect(
      parseSavedServers(
        JSON.stringify([
          { name: 'Bad', address: 'https://user:secret@example.org' },
          { name: 'Okay', address: 'https://safe.example', token: 'secret' },
        ]),
      ),
    ).toEqual([{ name: 'Okay', address: 'https://safe.example' }]);
    let servers: ReturnType<typeof readSavedServers> = [];
    for (let i = 0; i < 25; i++)
      servers = rememberServer(
        servers,
        `https://server${i}.example`,
        'x'.repeat(100),
      );
    expect(servers).toHaveLength(20);
    expect(servers[0].name).toHaveLength(80);
  });
});
