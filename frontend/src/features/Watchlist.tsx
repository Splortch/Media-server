import { useEffect, useState } from 'react';
import { api, type Media } from '../services/api';

export type WatchlistPage = { items: Media[]; nextCursor: string | null };

export function WatchlistButton({ mediaId }: { mediaId: number }) {
  const [saved, setSaved] = useState<boolean>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [status, setStatus] = useState('');
  const path = '/watchlist/' + mediaId;

  useEffect(() => {
    let active = true;
    void api<{ saved: boolean }>(path)
      .then((value) => {
        if (active) setSaved(value.saved);
      })
      .catch((error: Error) => {
        if (active) setError(error.message);
      });
    return () => {
      active = false;
    };
  }, [path]);

  async function toggle() {
    setBusy(true);
    setError('');
    setStatus('');
    try {
      await api(path, saved ? 'DELETE' : 'PUT');
      setSaved(!saved);
      setStatus(
        saved ? 'Removed from your watchlist.' : 'Added to your watchlist.',
      );
    } catch (error) {
      setError((error as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="watchlist-action">
      {saved !== undefined && (
        <button
          className="secondary"
          aria-disabled={busy}
          onClick={() => {
            if (!busy) void toggle();
          }}
        >
          {saved ? 'Remove from watchlist' : 'Add to watchlist'}
        </button>
      )}
      {error && <p role="alert">{error}</p>}
      <span className="sr-only" role="status">
        {status}
      </span>
    </div>
  );
}

export function WatchlistRecommendations({
  onOpen,
}: {
  onOpen: (mediaId: number) => void;
}) {
  const [recommendations, setRecommendations] = useState<{
    basedOn: string | null;
    items: Media[];
  }>();
  useEffect(() => {
    let active = true;
    void api<NonNullable<typeof recommendations>>('/watchlist/recommendations')
      .then((value) => {
        if (active) setRecommendations(value);
      })
      .catch(() => {
        // Discovery is optional and must never block the rest of Home.
      });
    return () => {
      active = false;
    };
  }, []);
  if (!recommendations?.items.length) return null;
  return (
    <section className="panel" aria-labelledby="watchlist-recommendations">
      <h2 id="watchlist-recommendations">Recommended for you</h2>
      <p className="muted">
        From the same library as {recommendations.basedOn}, which you saved to
        your watchlist.
      </p>
      <ul className="recommendation-list">
        {recommendations.items.map((item) => (
          <li key={item.id}>
            <a href={`#media/${item.id}`} onClick={() => onOpen(item.id)}>
              {item.title}
            </a>
          </li>
        ))}
      </ul>
    </section>
  );
}
