import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { it, expect, vi, afterEach } from 'vitest';
import CatalogPicker from '../src/features/downloads/CatalogPicker';
import { api } from '../src/services/api';
vi.mock('../src/services/api', () => ({ api: vi.fn() }));
afterEach(() => vi.resetAllMocks());
it('shows provenance and waits for explicit source selection without starting a download', async () => {
  const item = {
    provider: 'wikimedia-commons-cc0',
    id: '42',
    title: 'Reviewed fixture',
    url: 'https://upload.wikimedia.org/file',
    bytes: 100,
    license: 'CC0',
    licenseUrl: 'https://creativecommons.org/publicdomain/zero/1.0/',
    sourcePage: 'https://commons.wikimedia.org/wiki/File:Fixture',
    attribution: 'Synthetic creator',
    rightsEvidence: 'https://commons.wikimedia.org/wiki/File:Fixture',
    approvalToken: 'protected-proof',
  };
  vi.mocked(api).mockImplementation(
    async (path) =>
      (path.endsWith('capabilities') ? { enabled: true } : [item]) as never,
  );
  const select = vi.fn();
  render(<CatalogPicker onSelect={select} />);
  fireEvent.click(
    await screen.findByRole('button', { name: 'Search catalog' }),
  );
  await screen.findByText('Synthetic creator');
  expect(
    screen.getByRole('link', { name: 'Operator-reviewed rights evidence' }),
  ).toHaveAttribute('href', item.rightsEvidence);
  expect(select).not.toHaveBeenCalled();
  expect(
    vi.mocked(api).mock.calls.every((call) => !call[1] || call[1] === 'GET'),
  ).toBe(true);
  fireEvent.click(
    screen.getByRole('button', { name: 'Use this source for approval' }),
  );
  await waitFor(() => expect(select).toHaveBeenCalledWith(item));
});
it('does not query the provider while operator opt-in is disabled', async () => {
  vi.mocked(api).mockResolvedValue({ enabled: false });
  render(<CatalogPicker onSelect={vi.fn()} />);
  await waitFor(() => expect(api).toHaveBeenCalledOnce());
  expect(
    screen.queryByRole('button', { name: 'Search catalog' }),
  ).not.toBeInTheDocument();
});
