import { useState } from 'react';
import { api } from '../services/api';
type Snapshot = { seconds: number; completed: boolean; hidden: boolean };
type State = {
  progress: { revision: number; completed: boolean; seconds: number };
  hidden: boolean;
  previous: Snapshot;
};
export default function WatchControls({
  id,
  request = api,
  onChange,
}: {
  id: number;
  request?: typeof api;
  onChange?: (progress: State['progress']) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState<'watched' | 'unwatched' | 'hide'>();
  const [undo, setUndo] = useState<{ revision: number; snapshot: Snapshot }>();
  const [message, setMessage] = useState('');
  async function change(action: string) {
    setBusy(true);
    setMessage('');
    try {
      const current =
        action === 'restore'
          ? null
          : (await request<State>(`/media/${id}/watch-state`)).progress;
      const result = await request<State>(`/media/${id}/watch-state`, 'POST', {
        action,
        expectedRevision:
          action === 'restore' ? undo!.revision : current!.revision,
        ...(action === 'restore' ? { restore: undo!.snapshot } : {}),
      });
      setUndo(
        action === 'restore'
          ? undefined
          : { revision: result.progress.revision, snapshot: result.previous },
      );
      setConfirm(undefined);
      setMessage(
        action === 'restore'
          ? 'Previous watch state restored.'
          : action === 'hide'
            ? 'Removed from Continue Watching. Resume position kept.'
            : action === 'watched'
              ? 'Marked watched.'
              : 'Marked unwatched.',
      );
      onChange?.(result.progress);
    } catch {
      setMessage(
        'Watch state could not be changed. It may have changed on another device.',
      );
    } finally {
      setBusy(false);
    }
  }
  return (
    <section
      className="panel watch-controls"
      aria-label="Watch state"
      aria-busy={busy}
    >
      <h2>Watch state</h2>
      <div className="action-group">
        <button
          className="secondary"
          disabled={busy}
          onClick={() => setConfirm('watched')}
        >
          Mark watched
        </button>
        <button
          className="secondary"
          disabled={busy}
          onClick={() => setConfirm('unwatched')}
        >
          Mark unwatched
        </button>
        <button
          className="secondary"
          disabled={busy}
          onClick={() => setConfirm('hide')}
        >
          Remove from Continue Watching
        </button>
      </div>
      {confirm && (
        <div role="group" aria-label="Confirm watch state change">
          <p>
            {confirm === 'hide'
              ? 'Hide this title from Continue Watching? Its resume position will be kept.'
              : confirm === 'watched'
                ? 'Mark this title watched for your account?'
                : 'Mark this title unwatched and reset your resume position?'}
          </p>
          <button disabled={busy} onClick={() => void change(confirm)}>
            Confirm change
          </button>{' '}
          <button
            className="secondary"
            disabled={busy}
            onClick={() => setConfirm(undefined)}
          >
            Cancel change
          </button>
        </div>
      )}
      {message && <p role="status">{message}</p>}
      {undo && (
        <button disabled={busy} onClick={() => void change('restore')}>
          Undo watch state change
        </button>
      )}
    </section>
  );
}
