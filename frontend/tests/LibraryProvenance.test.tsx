import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import LibraryProvenance from '../src/components/LibraryProvenance';
import { ReviewActions } from '../src/components/MatchReportReview';
import { type api } from '../src/services/api';
import { parseFeatures, featureVisible } from '../src/services/features';
const source = {
  library: 'Family films',
  addedAt: null,
  lastScannedAt: null,
  identification: 'Filename-derived title',
  canReport: true,
  report: null,
};
it('loads only on expansion and submits a private wrong-match report without file diagnostics', async () => {
  const request = vi
    .fn<typeof api>()
    .mockResolvedValueOnce(source as never)
    .mockResolvedValueOnce({
      id: 3,
      kind: 'artwork',
      note: 'Wrong poster',
      state: 'Open',
      revision: 1,
    } as never);
  render(
    <LibraryProvenance
      id={7}
      admin={false}
      request={request as typeof api}
      onCorrect={vi.fn()}
    />,
  );
  expect(request).not.toHaveBeenCalled();
  fireEvent.click(screen.getByText('Why is this here?'));
  expect(await screen.findByText('Filename-derived title')).toBeInTheDocument();
  expect(screen.getAllByText('Not recorded for this item')).toHaveLength(2);
  fireEvent.change(screen.getByLabelText('What looks wrong?'), {
    target: { value: 'artwork' },
  });
  fireEvent.change(screen.getByLabelText('Optional note (500 characters)'), {
    target: { value: 'Wrong poster' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Report wrong match' }));
  expect(
    await screen.findByText('Your artwork report: Open.'),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('button', { name: 'Report wrong match' }),
  ).toBeNull();
  expect(request.mock.calls).toEqual([
    ['/media/7/provenance'],
    [
      '/media/7/match-report',
      'POST',
      { kind: 'artwork', note: 'Wrong poster' },
    ],
  ]);
  expect(screen.queryByText('Exact file')).toBeNull();
});
it('does not offer writes when the feature is disabled or this session is a guest', async () => {
  const request = vi.fn<typeof api>().mockResolvedValue(undefined as never);
  const view = render(
    <LibraryProvenance
      id={1}
      admin={false}
      request={request as typeof api}
      onCorrect={vi.fn()}
    />,
  );
  fireEvent.click(screen.getByText('Why is this here?'));
  expect(
    await screen.findByText(/Library explanations are hidden or disabled/),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('button', { name: 'Report wrong match' }),
  ).toBeNull();
  view.unmount();
  request.mockResolvedValue({ ...source, canReport: false } as never);
  render(
    <LibraryProvenance
      id={1}
      admin={false}
      request={request as typeof api}
      onCorrect={vi.fn()}
    />,
  );
  fireEvent.click(screen.getByText('Why is this here?'));
  await screen.findByText('Family films');
  expect(
    screen.queryByRole('button', { name: 'Report wrong match' }),
  ).toBeNull();
});
it('requires a separate admin expansion before loading private paths and offers manual correction', async () => {
  const request = vi.fn<typeof api>().mockImplementation(async (path) => {
    if (path === '/media/1/provenance') return source as never;
    if (path === '/admin/media/1/provenance')
      return {
        relativePath: 'film.webm',
        filePath: '/private/media/film.webm',
        missing: false,
        pendingImport: false,
        size: 100,
        fingerprint: 'hash',
        identification: 'Local NFO metadata',
        metadataRevision: 1,
        providerId: null,
        lastLibraryScanState: 'completed',
        lastLibraryScanAt: null,
      } as never;
    if (path === '/admin/match-reports?mediaId=1&offset=0')
      return { items: [], nextOffset: null } as never;
    throw new Error('Unexpected request');
  });
  const correct = vi.fn();
  render(
    <LibraryProvenance
      id={1}
      admin
      request={request as typeof api}
      onCorrect={correct}
    />,
  );
  fireEvent.click(screen.getByText('Why is this here?'));
  fireEvent.click(await screen.findByText('Administrator investigation'));
  expect(
    await screen.findByText('/private/media/film.webm'),
  ).toBeInTheDocument();
  fireEvent.click(
    screen.getByRole('button', { name: "Correct this title's metadata" }),
  );
  expect(correct).toHaveBeenCalledOnce();
});
it('uses the report revision and preserves a conflict instead of claiming success', async () => {
  const request = vi.fn<typeof api>().mockRejectedValue(new Error('Conflict'));
  const changed = vi.fn();
  render(
    <ReviewActions
      report={{
        id: 2,
        mediaId: 1,
        title: 'Film',
        reporter: 'Viewer',
        kind: 'title',
        note: '<img src=x onerror=alert(1)>',
        state: 'Open',
        revision: 8,
      }}
      request={request as typeof api}
      onChanged={changed}
    />,
  );
  expect(document.querySelector('img')).toBeNull();
  fireEvent.click(screen.getByRole('button', { name: 'Resolve report' }));
  expect(await screen.findByRole('status')).toHaveTextContent(
    'another administrator',
  );
  expect(request).toHaveBeenCalledWith(
    '/admin/match-reports/2/review',
    'POST',
    { state: 'Resolved', expectedRevision: 8 },
  );
  await waitFor(() =>
    expect(
      screen.getByRole('button', { name: 'Resolve report' }),
    ).toBeEnabled(),
  );
  expect(changed).not.toHaveBeenCalled();
});
it('recognizes the shipped provenance feature and honors its visibility', () => {
  const state = parseFeatures({
    features: [
      {
        key: 'library-provenance',
        name: 'Explanations',
        enabled: true,
        visible: true,
        hidden: false,
        serverConfigurable: true,
      },
    ],
  });
  expect(featureVisible(state, 'library-provenance')).toBe(true);
});
