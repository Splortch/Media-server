import { useEffect, useState } from 'react';
import { api } from '../services/api';
export default function EpisodeSpoilerPreference({
  request = api,
  onChanged,
}: {
  request?: typeof api;
  onChanged: () => void;
}) {
  const [reveal, setReveal] = useState(false);
  const [ready, setReady] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    void request<{ revealSpoilers: boolean }>('/preferences/episodes')
      .then((value) => {
        if (active) {
          setReveal(value.revealSpoilers);
          setReady(true);
        }
      })
      .catch(() => {
        if (active) setError('Spoiler preference could not be loaded.');
      });
    return () => {
      active = false;
    };
  }, [request]);
  return (
    <div>
      <label>
        <input
          type="checkbox"
          checked={reveal}
          disabled={!ready || busy}
          onChange={(event) => {
            const next = event.target.checked;
            setReveal(next);
            setBusy(true);
            setError('');
            void request<{ revealSpoilers: boolean }>(
              '/preferences/episodes',
              'PUT',
              { revealSpoilers: next },
            )
              .then((value) => {
                setReveal(value.revealSpoilers);
                onChanged();
              })
              .catch(() => {
                setReveal(!next);
                setError('Spoiler preference could not be saved.');
              })
              .finally(() => setBusy(false));
          }}
        />{' '}
        Reveal summaries and artwork for unseen episodes
      </label>
      <p className="muted">
        Saved for your account. Otherwise, episode details remain hidden until
        you start watching.
      </p>
      {error && <p role="status">{error}</p>}
    </div>
  );
}
