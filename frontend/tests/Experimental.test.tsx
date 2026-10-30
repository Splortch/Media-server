import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import Experimental from '../src/pages/Experimental';
import { api } from '../src/services/api';
vi.mock('../src/services/api', () => ({ api: vi.fn() }));
afterEach(() => vi.resetAllMocks());
it('does not fetch or expose controls for non-admins', () => {
  render(<Experimental admin={false} />);
  expect(api).not.toHaveBeenCalled();
  expect(screen.queryByRole('checkbox')).toBeNull();
});
it('requires explicit library selection and saves the opt-in policy', async () => {
  vi.mocked(api)
    .mockResolvedValueOnce({ enabled: false, libraries: [] })
    .mockResolvedValueOnce([{ id: 3, name: 'Family movies' }])
    .mockResolvedValueOnce({ enabled: true, libraries: [3] });
  render(<Experimental admin />);
  const toggle = await screen.findByLabelText('Enable guest access');
  expect(toggle).not.toBeChecked();
  fireEvent.click(toggle);
  expect(
    screen.getByRole('button', { name: 'Save guest policy' }),
  ).toBeDisabled();
  fireEvent.click(screen.getByLabelText('Family movies'));
  fireEvent.click(screen.getByRole('button', { name: 'Save guest policy' }));
  await waitFor(() =>
    expect(api).toHaveBeenCalledWith('/experiments/guests', 'PUT', {
      enabled: true,
      libraries: [3],
    }),
  );
  expect(await screen.findByRole('status')).toHaveTextContent('revoked');
});
it('reports policy save failures without claiming success', async () => {
  vi.mocked(api)
    .mockResolvedValueOnce({ enabled: false, libraries: [] })
    .mockResolvedValueOnce([])
    .mockRejectedValueOnce(new Error('Server unavailable'));
  render(<Experimental admin />);
  fireEvent.click(
    await screen.findByRole('button', { name: 'Save guest policy' }),
  );
  expect(await screen.findByRole('alert')).toHaveTextContent(
    'Server unavailable',
  );
  expect(screen.queryByText(/policy saved/)).toBeNull();
});
