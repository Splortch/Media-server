import { useEffect, useState } from 'react';
import SeasonWatchControls, {
  type SeasonProgress,
} from './SeasonWatchControls';
import EpisodeSpoilerPreference from './EpisodeSpoilerPreference';
import { api, duration } from '../services/api';

export type SeriesEpisode = {
  id: number;
  title: string;
  season: number;
  episode: number;
  duration: number;
  seconds: number;
  completed: boolean;
};
type Navigation = {
  series: string | null;
  seasons: number[];
  season: number | null;
  episodes: SeriesEpisode[];
  nextOffset: number | null;
  nextEpisode: SeriesEpisode | null;
  nextUnwatched?: SeriesEpisode | null;
  seasonProgress?: SeasonProgress;
};
export default function EpisodeNavigation({
  id,
  request = api,
  onEpisode,
  busy = false,
  canManage = false,
  onPreferenceChange,
  onWatchChange,
}: {
  id: number;
  request?: typeof api;
  onEpisode: (id: number) => void;
  busy?: boolean;
  canManage?: boolean;
  onPreferenceChange?: () => void;
  onWatchChange?: () => void;
}) {
  const [value, setValue] = useState<Navigation>();
  const [season, setSeason] = useState<number>();
  const [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let active = true;
    const query = new URLSearchParams();
    if (season !== undefined) query.set('season', String(season));
    if (offset) query.set('offset', String(offset));
    void request<Navigation>(
      `/media/${id}/episodes${query.size ? '?' + query : ''}`,
    )
      .then((row) => {
        if (!active) return;
        setValue((old) =>
          offset && old
            ? { ...row, episodes: [...old.episodes, ...row.episodes] }
            : row,
        );
        setError('');
      })
      .catch(() => {
        if (active)
          setError(
            'Episodes could not be loaded. You can still play this title.',
          );
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, [id, season, offset, request, retry]);
  return (
    <section
      className="panel episode-navigation"
      aria-label="Seasons and episodes"
      aria-busy={loading}
    >
      <h2>{value?.series ? `${value.series} episodes` : 'Episodes'}</h2>
      <p className="muted">
        Choose an episode to view its information. Play or Resume starts
        playback.
      </p>
      {error && (
        <p role="status">
          {error}{' '}
          <button
            onClick={() => {
              setLoading(true);
              setRetry(retry + 1);
            }}
          >
            Retry episodes
          </button>
        </p>
      )}
      {loading && <p role="status">Loading episodes…</p>}
      {value && (
        <>
          <label>
            Season
            <select
              value={season ?? value.season ?? ''}
              disabled={busy || loading}
              onChange={(e) => {
                setSeason(Number(e.target.value));
                setValue((old) =>
                  old ? { ...old, episodes: [], nextOffset: null } : old,
                );
                setOffset(0);
                setLoading(true);
                setError('');
              }}
            >
              {value.seasons.map((s) => (
                <option key={s} value={s}>
                  {s === 0 ? 'Specials' : `Season ${s}`}
                </option>
              ))}
            </select>
          </label>
          {value.seasonProgress && (
            <p role="status">
              {value.seasonProgress.watched} of {value.seasonProgress.total}{' '}
              accessible episodes watched in{' '}
              {value.season === 0 ? 'Specials' : `Season ${value.season}`}.
            </p>
          )}
          {value.nextUnwatched && (
            <button
              disabled={busy || loading}
              onClick={() => onEpisode(value.nextUnwatched!.id)}
            >
              View next unwatched: Season {value.nextUnwatched.season}, Episode{' '}
              {value.nextUnwatched.episode} · {value.nextUnwatched.title}
            </button>
          )}
          {canManage && (
            <EpisodeSpoilerPreference
              request={request}
              onChanged={() => onPreferenceChange?.()}
            />
          )}
          {canManage && value.seasonProgress && (
            <SeasonWatchControls
              key={`${id}:${value.season}`}
              id={id}
              progress={value.seasonProgress}
              request={request}
              disabled={busy || loading}
              onChanged={() => {
                setOffset(0);
                setLoading(true);
                setRetry((x) => x + 1);
                onWatchChange?.();
              }}
            />
          )}
          <ol className="episode-list">
            {value.episodes.map((episode) => (
              <li key={episode.id}>
                <button
                  disabled={busy || loading}
                  aria-current={episode.id === id ? 'page' : undefined}
                  onClick={() => onEpisode(episode.id)}
                >
                  Episode {episode.episode} · {episode.title} ·{' '}
                  {duration(episode.duration)}
                  {episode.completed
                    ? ' · Watched'
                    : episode.seconds > 0
                      ? ` · Resume from ${duration(episode.seconds)}`
                      : ''}
                  {episode.id === id ? ' · Current episode' : ''}
                </button>
              </li>
            ))}
          </ol>
          {!value.episodes.length && !loading && (
            <p>No accessible episodes in this season.</p>
          )}
          {value.nextOffset !== null && (
            <button
              disabled={busy || loading}
              onClick={() => {
                setOffset(value.nextOffset!);
                setLoading(true);
              }}
            >
              Load more episodes
            </button>
          )}
          {value.nextEpisode && (
            <button
              disabled={busy || loading}
              onClick={() => onEpisode(value.nextEpisode!.id)}
            >
              View next episode: Season {value.nextEpisode.season}, Episode{' '}
              {value.nextEpisode.episode} · {value.nextEpisode.title}
            </button>
          )}
        </>
      )}
    </section>
  );
}
