import { useState } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import Customize from '../src/features/Customize';
import { api } from '../src/services/api';
import { parseFeatures, type FeatureState } from '../src/services/features';
vi.mock('../src/services/api', () => ({ api: vi.fn() }));
afterEach(() => vi.resetAllMocks());
const initial: FeatureState = {
  available: true,
  features: [
    {
      key: 'smart-collections',
      name: 'Smart collections',
      enabled: true,
      visible: true,
      hidden: false,
      serverConfigurable: true,
    },
    {
      key: 'watch-history',
      name: 'Watch history navigation',
      enabled: true,
      visible: true,
      hidden: false,
      serverConfigurable: false,
    },
  ],
};
function Harness({ admin = false }: { admin?: boolean }) {
  const [state, setState] = useState(initial);
  return <Customize state={state} admin={admin} onChange={setState} />;
}
it('saves personal visibility without exposing admin controls and resets to server defaults', async () => {
  vi.mocked(api)
    .mockResolvedValueOnce({
      features: initial.features.map((f) => ({
        ...f,
        visible: f.key !== 'watch-history',
        hidden: f.key === 'watch-history',
      })),
    })
    .mockResolvedValueOnce(initial);
  render(<Harness />);
  expect(screen.queryByText('Server-wide availability')).toBeNull();
  fireEvent.click(screen.getByLabelText('Show Watch history navigation'));
  await waitFor(() =>
    expect(
      screen.getByLabelText('Show Watch history navigation'),
    ).not.toBeChecked(),
  );
  expect(api).toHaveBeenCalledWith('/preferences/features', 'PUT', {
    hidden: ['watch-history'],
  });
  fireEvent.click(
    screen.getByRole('button', { name: 'Reset visibility to defaults' }),
  );
  await waitFor(() =>
    expect(
      screen.getByLabelText('Show Watch history navigation'),
    ).toBeChecked(),
  );
  expect(api).toHaveBeenCalledWith(
    '/preferences/features',
    'DELETE',
    undefined,
  );
});
it('distinguishes admin-disabled from personally hidden and retains the personal choice', async () => {
  vi.mocked(api).mockResolvedValueOnce({
    features: initial.features.map((f) =>
      f.key === 'smart-collections'
        ? { ...f, enabled: false, visible: false }
        : f,
    ),
  });
  render(<Harness admin />);
  fireEvent.click(screen.getByText('Server-wide availability'));
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Disable Smart collections for everyone',
    }),
  );
  await waitFor(() =>
    expect(screen.getByLabelText('Show Smart collections')).toBeDisabled(),
  );
  expect(screen.getByLabelText('Show Smart collections')).toBeChecked();
  expect(screen.getByText(/Disabled by the administrator/)).toBeVisible();
  expect(api).toHaveBeenCalledWith('/features/smart-collections', 'PUT', {
    enabled: false,
  });
});
it('keeps the previous state on a server refusal', async () => {
  vi.mocked(api).mockRejectedValueOnce(new Error('Session expired'));
  render(<Harness />);
  fireEvent.click(screen.getByLabelText('Show Smart collections'));
  await screen.findByRole('alert');
  expect(screen.getByLabelText('Show Smart collections')).toBeChecked();
});
it('rejects malformed capabilities and ignores unsupported future keys', () => {
  expect(() => parseFeatures({ features: null })).toThrow();
  const state = parseFeatures({
    features: [...initial.features, { key: 'not-implemented', name: 'future' }],
  });
  expect(state.features).toHaveLength(2);
});

it('does not apply a previous account response after the settings view unmounts', async () => {
  let resolve!: (value: unknown) => void;
  vi.mocked(api).mockImplementationOnce(
    () =>
      new Promise((r) => {
        resolve = r;
      }),
  );
  const changed = vi.fn();
  const view = render(
    <Customize state={initial} admin={false} onChange={changed} />,
  );
  fireEvent.click(screen.getByLabelText('Show Smart collections'));
  view.unmount();
  resolve(initial);
  await Promise.resolve();
  await Promise.resolve();
  expect(changed).not.toHaveBeenCalled();
});

it('reflects a checkbox change immediately while persistence is pending', async () => {
  let resolve!: (value: unknown) => void;
  vi.mocked(api).mockImplementationOnce(
    () =>
      new Promise((r) => {
        resolve = r;
      }),
  );
  render(<Harness />);
  fireEvent.click(screen.getByLabelText('Show Watch history navigation'));
  expect(
    screen.getByLabelText('Show Watch history navigation'),
  ).not.toBeChecked();
  expect(screen.getByLabelText('Show Watch history navigation')).toBeDisabled();
  resolve({
    features: initial.features.map((f) => ({
      ...f,
      visible: f.key !== 'watch-history',
      hidden: f.key === 'watch-history',
    })),
  });
  await waitFor(() =>
    expect(
      screen.getByLabelText('Show Watch history navigation'),
    ).toBeEnabled(),
  );
  expect(
    screen.getByLabelText('Show Watch history navigation'),
  ).not.toBeChecked();
});
