import { useState } from 'react';
import { api } from '../services/api';
export type SeasonProgress = {
  season: number;
  total: number;
  watched: number;
  revision: string | null;
};
export default function SeasonWatchControls({
  id,
  progress,
  request = api,
  onChanged,
  disabled = false,
}: {
  id: number;
  progress: SeasonProgress;
  request?: typeof api;
  onChanged: () => void;
  disabled?: boolean;
}) {
  const [confirm, setConfirm] = useState<'watched' | 'unwatched'>();
  const [undo, setUndo] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  async function change(action: 'watched' | 'unwatched' | 'undo') {
    setBusy(true);
    setMessage('');
    try {
      const result = await request<{
        progress: SeasonProgress;
        undoToken: string | null;
      }>(`/media/${id}/season-watch-state`, 'POST', {
        season: progress.season,
        action,
        ...(action === 'undo'
          ? { undoToken: undo }
          : { expectedRevision: progress.revision }),
      });
      setUndo(result.undoToken ?? undefined);
      setConfirm(undefined);
      setMessage(
        action === 'undo'
          ? 'Previous season watch states restored.'
          : `Season marked ${action}. Undo is available for ten minutes, unless progress changes.`,
      );
      onChanged();
    } catch {
      setConfirm(undefined);
      setMessage(
        'Season could not be changed. Refresh the episodes: progress, access or metadata may have changed. Undo also expires after ten minutes.',
      );
    } finally {
      setBusy(false);
    }
  }
  return (
    <div
      className="season-watch-controls"
      aria-label="Season watch state"
      aria-busy={busy}
    >
      <div className="action-group">
        <button
          className="secondary"
          disabled={disabled || busy || !progress.total || !progress.revision}
          onClick={() => setConfirm('watched')}
        >
          Mark season watched
        </button>
        <button
          className="secondary"
          disabled={disabled || busy || !progress.total || !progress.revision}
          onClick={() => setConfirm('unwatched')}
        >
          Mark season unwatched
        </button>
      </div>
      {confirm && (
        <div role="group" aria-label="Confirm season watch state change">
          <p>
            {confirm === 'watched'
              ? `Mark all ${progress.total} accessible episodes in this season watched for your account?`
              : `Mark all ${progress.total} accessible episodes in this season unwatched and reset their resume positions?`}{' '}
            Missing and restricted episodes are excluded.
          </p>
          <button
            disabled={disabled || busy}
            onClick={() => void change(confirm)}
          >
            Confirm season change
          </button>{' '}
          <button
            className="secondary"
            disabled={busy}
            onClick={() => setConfirm(undefined)}
          >
            Cancel season change
          </button>
        </div>
      )}
      {message && <p role="status">{message}</p>}
      {undo && (
        <button disabled={disabled || busy} onClick={() => void change('undo')}>
          Undo season change
        </button>
      )}
    </div>
  );
}
