import {
  render,
  screen,
  fireEvent,
  waitFor,
  act,
} from '@testing-library/react';
import { it, expect, vi } from 'vitest';
import App from '../src/App';
const state = vi.hoisted(() => ({
  user: 'Alice',
  deferred: undefined as undefined | ((value: unknown) => void),
}));
vi.mock('../src/services/api', () => ({
  resetCsrf: vi.fn(),
  duration: () => '1 min',
  api: async (path: string, method = 'GET') => {
    if (path === '/setup') return { required: false };
    if (path === '/auth/me') return { name: 'Alice', role: 'Admin' };
    if (path === '/auth/login' && method === 'POST') {
      state.user = 'Bob';
      return { name: 'Bob', role: 'Viewer' };
    }
    if (path === '/status')
      return { version: 'fixture', mediaCount: 1, libraryCount: 1 };
    if (
      path === '/collections' ||
      path === '/continue-watching-media' ||
      path.startsWith('/progress')
    )
      return [];
    if (path.startsWith('/media-page')) {
      if (path.includes('cursor='))
        return new Promise((resolve) => {
          state.deferred = resolve;
        });
      return {
        items: [
          {
            id: state.user === 'Alice' ? 1 : 2,
            title: state.user + ' private collection',
            duration: 60,
            size: 100,
            container: 'webm',
            videoCodec: 'vp9',
            audioCodec: 'opus',
          },
        ],
        nextCursor: state.user === 'Alice' ? 'old-cursor' : null,
      };
    }
    throw new Error('Unexpected fixture request ' + path);
  },
}));
it('ignores a previous account’s delayed page after session expiration and a new login', async () => {
  location.hash = 'home';
  state.user = 'Alice';
  render(<App />);
  await screen.findByRole('heading', { name: 'Alice private collection' });
  expect(
    screen.getByRole('combobox', { name: 'Smart collection' }),
  ).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Load more videos' }));
  await waitFor(() => expect(state.deferred).toBeTypeOf('function'));
  act(() => window.dispatchEvent(new Event('session-expired')));
  await screen.findByRole('heading', { name: 'Sign in' });
  fireEvent.change(screen.getByLabelText('Username'), {
    target: { value: 'Bob' },
  });
  fireEvent.change(screen.getByLabelText('Password', { exact: true }), {
    target: { value: 'bob-fixture-password' },
  });
  fireEvent.submit(
    screen.getByRole('button', { name: 'Sign in' }).closest('form')!,
  );
  await screen.findByRole('heading', { name: 'Bob private collection' });
  await act(async () =>
    state.deferred?.({
      items: [
        {
          id: 3,
          title: 'Alice delayed secret',
          duration: 60,
          size: 100,
          container: 'webm',
          videoCodec: 'vp9',
          audioCodec: 'opus',
        },
      ],
      nextCursor: null,
    }),
  );
  expect(
    screen.queryByRole('heading', { name: 'Alice delayed secret' }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole('heading', { name: 'Alice private collection' }),
  ).not.toBeInTheDocument();
});
