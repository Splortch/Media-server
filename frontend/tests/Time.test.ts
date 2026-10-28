import { it, expect } from 'vitest';
import { serverTime } from '../src/services/api';
it('preserves UTC for SQLite timestamps and honors explicit offsets', () => {
  expect(serverTime('2026-10-28T12:34:56').toISOString()).toBe(
    '2026-10-28T12:34:56.000Z',
  );
  expect(serverTime('2026-10-28T12:34:56Z').toISOString()).toBe(
    '2026-10-28T12:34:56.000Z',
  );
  expect(serverTime('2026-10-28T12:34:56+02:00').toISOString()).toBe(
    '2026-10-28T10:34:56.000Z',
  );
});
