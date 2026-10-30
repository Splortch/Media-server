import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import App from '../src/App';
import { api } from '../src/services/api';
vi.mock('../src/services/api', () => ({
  resetCsrf: vi.fn(),
  duration: () => '1 min',
  api: vi.fn(async (path: string, method = 'GET') => {
    if (path === '/setup') return { required: false };
    if (path === '/auth/me') throw new Error('Sign in');
    if (path === '/auth/guest')
      return method === 'POST'
        ? { name: 'Guest', role: 'Guest' }
        : { enabled: true };
    if (path === '/features') return { features: [] };
    if (path === '/status')
      return { version: 'test', mediaCount: 0, libraryCount: 1 };
    if (path.startsWith('/media-page')) return { items: [], nextCursor: null };
    return [];
  }),
}));
it('enters an opted-in guest session without submitting password fields and hides private controls', async () => {
  location.hash = 'home';
  render(<App />);
  fireEvent.click(
    await screen.findByRole('button', { name: 'Continue as guest' }),
  );
  expect(await screen.findByText(/Welcome back, Guest/)).toBeInTheDocument();
  expect(api).toHaveBeenCalledWith('/auth/guest', 'POST');
  expect(screen.queryByRole('link', { name: 'Settings' })).toBeNull();
  expect(screen.queryByRole('link', { name: 'Watch history' })).toBeNull();
  expect(screen.queryByRole('link', { name: 'Experimental' })).toBeNull();
});
