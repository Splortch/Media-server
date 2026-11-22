import { useEffect, useState } from 'react';
import { api } from '../../services/api';
import MatchReportReview, {
  ReviewActions,
  type ReportPage,
} from '../../components/MatchReportReview';
export default function MatchReports() {
  const [open, setOpen] = useState(false);
  const [page, setPage] = useState<ReportPage>();
  const [offset, setOffset] = useState(0);
  const [closed, setClosed] = useState(false);
  const [retry, setRetry] = useState(0);
  const [error, setError] = useState('');
  useEffect(() => {
    if (!open) return;
    let active = true;
    void api<ReportPage>(
      `/admin/match-reports?offset=${offset}&closed=${closed}`,
    )
      .then((value) => {
        if (active) {
          setPage(value);
          setError('');
        }
      })
      .catch(() => {
        if (active)
          setError(
            'Match reports could not be loaded. The feature may be disabled.',
          );
      });
    return () => {
      active = false;
    };
  }, [open, offset, closed, retry]);
  return (
    <details
      className="panel"
      onToggle={(event) => setOpen(event.currentTarget.open)}
    >
      <summary>Metadata match reports</summary>
      {open && (
        <>
          <label>
            <input
              type="checkbox"
              checked={closed}
              onChange={(event) => {
                setClosed(event.target.checked);
                setOffset(0);
              }}
            />{' '}
            Include reviewed reports
          </label>
          <button className="secondary" onClick={() => setRetry((x) => x + 1)}>
            Refresh match reports
          </button>
          {error && <p role="status">{error}</p>}
          {page?.items.map((report) => (
            <article key={`${report.id}:${report.revision}`} className="panel">
              <h3>{report.title}</h3>
              <p>{report.state}</p>
              <ReviewActions
                report={report}
                onChanged={() => setRetry((x) => x + 1)}
              />
              <MatchReportReview mediaId={report.mediaId} />
            </article>
          ))}
          {page?.items.length === 0 && <p>No matching reports.</p>}
          {offset > 0 && (
            <button onClick={() => setOffset(Math.max(0, offset - 50))}>
              Previous reports
            </button>
          )}
          {page?.nextOffset != null && (
            <button onClick={() => setOffset(page.nextOffset!)}>
              Next reports
            </button>
          )}
        </>
      )}
    </details>
  );
}
