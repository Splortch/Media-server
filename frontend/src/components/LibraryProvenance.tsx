import { useEffect, useState } from 'react';
import { api } from '../services/api';
import MatchReportReview from './MatchReportReview';
type Report = {
  id: number;
  kind: string;
  note: string;
  state: string;
  revision: number;
};
type Provenance = {
  library: string;
  addedAt: string | null;
  lastScannedAt: string | null;
  identification: string;
  canReport: boolean;
  report: Report | null;
};
export default function LibraryProvenance({
  id,
  admin,
  request = api,
  onCorrect,
}: {
  id: number;
  admin: boolean;
  request?: typeof api;
  onCorrect: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const [value, setValue] = useState<Provenance>();
  const [retry, setRetry] = useState(0);
  const [error, setError] = useState('');
  const [kind, setKind] = useState('title');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  useEffect(() => {
    if (!open) return;
    let active = true;
    void request<Provenance | undefined>(`/media/${id}/provenance`)
      .then((row) => {
        if (active) {
          setValue(row);
          setLoaded(true);
          setError('');
        }
      })
      .catch(() => {
        if (active) setError('Library explanation could not be loaded.');
      });
    return () => {
      active = false;
    };
  }, [id, open, request, retry]);
  return (
    <details
      className="panel library-provenance"
      onToggle={(event) => setOpen(event.currentTarget.open)}
    >
      <summary>Why is this here?</summary>
      {open && (
        <>
          {!loaded && !error && (
            <p role="status">Loading library explanation…</p>
          )}
          {error && (
            <p role="status">
              {error}{' '}
              <button
                onClick={() => {
                  setError('');
                  setRetry((x) => x + 1);
                }}
              >
                Retry explanation
              </button>
            </p>
          )}
          {loaded && !value && (
            <p>
              Library explanations are hidden or disabled. Optional features can
              be changed in Settings.
            </p>
          )}
          {value && (
            <>
              <dl className="title-facts">
                <div>
                  <dt>Library</dt>
                  <dd>{value.library}</dd>
                </div>
                <div>
                  <dt>Title source</dt>
                  <dd>{value.identification}</dd>
                </div>
                <div>
                  <dt>Added to catalog</dt>
                  <dd>
                    {value.addedAt
                      ? new Date(value.addedAt).toLocaleString()
                      : 'Not recorded for this item'}
                  </dd>
                </div>
                <div>
                  <dt>Last successful scan</dt>
                  <dd>
                    {value.lastScannedAt
                      ? new Date(value.lastScannedAt).toLocaleString()
                      : 'Not recorded for this item'}
                  </dd>
                </div>
              </dl>
              {value.report && (
                <p role="status">
                  Your {value.report.kind} report: {value.report.state}.
                </p>
              )}
              {value.canReport && value.report?.state !== 'Open' && (
                <form
                  onSubmit={(event) => {
                    event.preventDefault();
                    setBusy(true);
                    setMessage('');
                    void request<Report>(`/media/${id}/match-report`, 'POST', {
                      kind,
                      note,
                    })
                      .then((report) => {
                        setValue((old) => (old ? { ...old, report } : old));
                        setNote('');
                        setMessage(
                          'Report saved for an administrator to review.',
                        );
                      })
                      .catch(() =>
                        setMessage(
                          'Report could not be saved. Check access and try again later; recently reviewed reports have a five-minute cooldown.',
                        ),
                      )
                      .finally(() => setBusy(false));
                  }}
                >
                  <h3>Report a wrong match</h3>
                  <label>
                    What looks wrong?
                    <select
                      value={kind}
                      disabled={busy}
                      onChange={(event) => setKind(event.target.value)}
                    >
                      <option value="title">Title</option>
                      <option value="artwork">Artwork</option>
                      <option value="episode">Series or episode</option>
                    </select>
                  </label>
                  <label>
                    Optional note (500 characters)
                    <textarea
                      value={note}
                      maxLength={500}
                      disabled={busy}
                      onChange={(event) => setNote(event.target.value)}
                    />
                  </label>
                  <p className="muted">
                    Your note is visible to server administrators. Reporting
                    does not change the file or its metadata.
                  </p>
                  <button disabled={busy}>Report wrong match</button>
                </form>
              )}
              {message && <p role="status">{message}</p>}
              {admin && (
                <MatchReportReview
                  mediaId={id}
                  request={request}
                  onCorrect={onCorrect}
                />
              )}
            </>
          )}
        </>
      )}
    </details>
  );
}
