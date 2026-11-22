import { useEffect, useState } from 'react';
import { api } from '../services/api';
export type AdminReport = {
  id: number;
  mediaId: number;
  title: string;
  reporter: string;
  kind: string;
  note: string;
  state: string;
  revision: number;
};
export type ReportPage = { items: AdminReport[]; nextOffset: number | null };
type Diagnostics = {
  relativePath: string;
  filePath: string | null;
  missing: boolean;
  pendingImport: boolean;
  size: number;
  fingerprint: string;
  identification: string;
  metadataRevision: number | null;
  providerId: string | null;
  lastLibraryScanState: string | null;
  lastLibraryScanAt: string | null;
};
export function ReviewActions({
  report,
  request = api,
  onChanged,
}: {
  report: AdminReport;
  request?: typeof api;
  onChanged: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  return (
    <div role="group" aria-label={`Review report ${report.id}`}>
      <p>
        <strong>{report.reporter}</strong> reported {report.kind}:{' '}
        {report.note || 'No note supplied.'}
      </p>
      <div className="action-group">
        {(['Resolved', 'Dismissed'] as const).map((state) => (
          <button
            key={state}
            disabled={busy || report.state !== 'Open'}
            onClick={() => {
              setBusy(true);
              setMessage('');
              void request(`/admin/match-reports/${report.id}/review`, 'POST', {
                state,
                expectedRevision: report.revision,
              })
                .then(onChanged)
                .catch(() =>
                  setMessage(
                    'Report could not be reviewed. Refresh: another administrator may have changed it.',
                  ),
                )
                .finally(() => setBusy(false));
            }}
          >
            {state === 'Resolved' ? 'Resolve report' : 'Dismiss report'}
          </button>
        ))}
      </div>
      {message && <p role="status">{message}</p>}
    </div>
  );
}
export default function MatchReportReview({
  mediaId,
  request = api,
  onCorrect,
}: {
  mediaId: number;
  request?: typeof api;
  onCorrect?: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [diagnostics, setDiagnostics] = useState<Diagnostics>();
  const [page, setPage] = useState<ReportPage>();
  const [offset, setOffset] = useState(0);
  const [retry, setRetry] = useState(0);
  const [message, setMessage] = useState('');
  useEffect(() => {
    if (!open) return;
    let active = true;
    void Promise.all([
      request<Diagnostics>(`/admin/media/${mediaId}/provenance`),
      request<ReportPage>(
        `/admin/match-reports?mediaId=${mediaId}&offset=${offset}`,
      ),
    ])
      .then(([details, reports]) => {
        if (active) {
          setDiagnostics(details);
          setPage(reports);
          setMessage('');
        }
      })
      .catch(() => {
        if (active)
          setMessage('Private diagnostics or reports could not be loaded.');
      });
    return () => {
      active = false;
    };
  }, [mediaId, request, open, offset, retry]);
  return (
    <details
      className="match-report-investigation"
      onToggle={(event) => setOpen(event.currentTarget.open)}
    >
      <summary>Administrator investigation</summary>
      {open && (
        <>
          {message && <p role="status">{message}</p>}
          {!diagnostics && !message && (
            <p role="status">Loading private diagnostics…</p>
          )}
          <button className="secondary" onClick={() => setRetry((x) => x + 1)}>
            Refresh investigation
          </button>
          {diagnostics && (
            <dl className="title-facts">
              <div>
                <dt>Exact file</dt>
                <dd>
                  {diagnostics.filePath ||
                    `${diagnostics.relativePath} (current path unavailable)`}
                </dd>
              </div>
              <div>
                <dt>Catalog state</dt>
                <dd>
                  {diagnostics.missing
                    ? 'Missing'
                    : diagnostics.pendingImport
                      ? 'Import pending'
                      : 'Available'}
                </dd>
              </div>
              <div>
                <dt>Last library scan</dt>
                <dd>
                  {diagnostics.lastLibraryScanState || 'Not recorded'}
                  {diagnostics.lastLibraryScanAt
                    ? ` · ${new Date(diagnostics.lastLibraryScanAt).toLocaleString()}`
                    : ''}
                </dd>
              </div>
              <div>
                <dt>Matching evidence</dt>
                <dd>
                  {diagnostics.identification} · metadata revision{' '}
                  {diagnostics.metadataRevision ?? 'none'} · provider reference{' '}
                  {diagnostics.providerId ?? 'none'}
                </dd>
              </div>
              <div>
                <dt>Scan fingerprint</dt>
                <dd>
                  {diagnostics.fingerprint || 'Not recorded'} ·{' '}
                  {diagnostics.size} bytes
                </dd>
              </div>
            </dl>
          )}
          {onCorrect ? (
            <button onClick={onCorrect}>Correct this title's metadata</button>
          ) : (
            <a href={`#media/${mediaId}`}>Open title to correct metadata</a>
          )}
          {page?.items.map((report) => (
            <ReviewActions
              key={`${report.id}:${report.revision}`}
              report={report}
              request={request}
              onChanged={() => setRetry((x) => x + 1)}
            />
          ))}
          {page && page.items.length === 0 && (
            <p>No open reports for this title.</p>
          )}
          {offset > 0 && (
            <button onClick={() => setOffset(Math.max(0, offset - 50))}>
              Previous title reports
            </button>
          )}
          {page?.nextOffset != null && (
            <button onClick={() => setOffset(page.nextOffset!)}>
              Next title reports
            </button>
          )}
        </>
      )}
    </details>
  );
}
