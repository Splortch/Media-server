import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../services/api';
type Episode = { id: number; title: string; season: number; episode: number };
function Countdown({
  episode,
  enabled,
  onNext,
}: {
  episode: Episode;
  enabled: boolean;
  onNext: (id: number) => Promise<void>;
}) {
  const [cancelled, setCancelled] = useState(false);
  const [remaining, setRemaining] = useState(10);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const deadline = useRef<number | null>(null);
  const started = useRef(false);
  const play = useCallback(async () => {
    if (started.current) return;
    started.current = true;
    setBusy(true);
    setCancelled(true);
    try {
      await onNext(episode.id);
    } catch {
      setError('The next episode could not be opened. Try again.');
    } finally {
      setBusy(false);
      started.current = false;
    }
  }, [onNext, episode.id]);
  useEffect(() => {
    if (!enabled || cancelled) return;
    deadline.current ??= Date.now() + 10000;
    const timer = setInterval(() => {
      const seconds = Math.max(
        0,
        Math.ceil((deadline.current! - Date.now()) / 1000),
      );
      setRemaining(seconds);
      if (seconds === 0) {
        clearInterval(timer);
        void play();
      }
    }, 250);
    return () => clearInterval(timer);
  }, [enabled, cancelled, play]);
  return (
    <>
      {enabled && !cancelled && (
        <p role="status">Next episode starts in {remaining} seconds.</p>
      )}
      <button disabled={busy} onClick={() => void play()}>
        Play next episode
      </button>
      {enabled && !cancelled && (
        <button
          className="secondary"
          disabled={busy}
          onClick={() => setCancelled(true)}
        >
          Cancel next episode
        </button>
      )}
      {error && <p role="alert">{error}</p>}
    </>
  );
}
export default function NextEpisodePrompt({
  id,
  ended,
  request = api,
  preferenceKey,
  onNext,
}: {
  id: number;
  ended: boolean;
  request?: typeof api;
  preferenceKey?: string;
  onNext: (id: number) => Promise<void>;
}) {
  const [next, setNext] = useState<Episode | null>(null);
  const [enabled, setEnabled] = useState(() => {
    try {
      return !!preferenceKey && localStorage.getItem(preferenceKey) === 'true';
    } catch {
      return false;
    }
  });
  useEffect(() => {
    let active = true;
    void request<{ nextEpisode: Episode | null }>(`/media/${id}/episodes`)
      .then((value) => {
        if (active) setNext(value?.nextEpisode ?? null);
      })
      .catch(() => {});
    return () => {
      active = false;
    };
  }, [id, request]);
  if (!next) return null;
  return (
    <section
      className="panel next-episode"
      aria-label="Next episode"
      data-ended={ended}
    >
      <label>
        <input
          type="checkbox"
          checked={enabled}
          onChange={(e) => {
            setEnabled(e.target.checked);
            try {
              if (preferenceKey)
                localStorage.setItem(preferenceKey, String(e.target.checked));
            } catch {
              /* Session-only preference still works. */
            }
          }}
        />{' '}
        Automatically play the next episode
      </label>
      <p>
        Next: Season {next.season}, Episode {next.episode} · {next.title}
      </p>
      {ended && (
        <Countdown
          key={`${id}:${enabled}`}
          episode={next}
          enabled={enabled}
          onNext={onNext}
        />
      )}
    </section>
  );
}
