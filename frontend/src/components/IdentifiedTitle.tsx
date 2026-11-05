import { useEffect, useState, type ComponentProps } from 'react';
import TitleDetails from './TitleDetails';
import { api } from '../services/api';

type Details = {
  title: string;
  year: number | null;
  synopsis: string;
  genres: string[];
  cast: string[];
  series: string | null;
  season: number | null;
  episode: number | null;
  provider: string;
  providerId: string | null;
  attribution: string;
};
type Metadata = {
  details: Details;
  posterUrl: string | null;
  backdropUrl: string | null;
  revision: number;
};
type Candidate = {
  id: string;
  kind: string;
  title: string;
  year: number | null;
};
const empty: Details = {
  title: '',
  year: null,
  synopsis: '',
  genres: [],
  cast: [],
  series: null,
  season: null,
  episode: null,
  provider: 'manual',
  providerId: null,
  attribution: '',
};
export default function IdentifiedTitle({
  admin,
  ...props
}: ComponentProps<typeof TitleDetails> & { admin: boolean }) {
  const [value, setValue] = useState<Metadata | null>(null);
  const [draft, setDraft] = useState<Details>({
    ...empty,
    title: props.item.title,
  });
  const [enabled, setEnabled] = useState(false);
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('movies');
  const [season, setSeason] = useState('');
  const [episode, setEpisode] = useState('');
  const [results, setResults] = useState<Candidate[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const id = props.item.id;
  useEffect(() => {
    let active = true;
    void api<Metadata | null>(`/media/${id}/metadata`)
      .then((row) => {
        if (active) {
          setValue(row);
          setDraft(row?.details ?? { ...empty, title: props.item.title });
        }
      })
      .catch((e) => {
        if (active) setError(e.message);
      });
    if (admin)
      void api<{ enabled: boolean }>('/metadata/providers')
        .then((row) => {
          if (active) setEnabled(row.enabled);
        })
        .catch(() => {});
    return () => {
      active = false;
    };
  }, [id, props.item.title, admin]);
  async function change(action: string, method: string, body: object) {
    setBusy(true);
    setError('');
    try {
      const row = await api<Metadata>(
        `/media/${id}/metadata${action}`,
        method,
        { ...body, expectedRevision: value?.revision ?? 0 },
      );
      setValue(row);
      setDraft(row.details);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      <TitleDetails
        {...props}
        item={{
          ...props.item,
          title: value?.details.title ?? props.item.title,
        }}
        metadata={value}
      />
      {error && <p role="alert">{error}</p>}
      {admin && (
        <details className="title-admin metadata-editor">
          <summary>Identify or correct title</summary>
          <p>
            Read adjacent NFO and artwork files, enter details yourself, or
            explicitly match a provider result. Matching never changes content
            ratings.
          </p>
          <button
            disabled={busy}
            onClick={() => void change('/local', 'POST', {})}
          >
            Read local NFO and artwork
          </button>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              void change('', 'PUT', { details: draft });
            }}
          >
            <label>
              Title
              <input
                required
                maxLength={200}
                value={draft.title}
                onChange={(e) => setDraft({ ...draft, title: e.target.value })}
              />
            </label>
            <label>
              Year
              <input
                type="number"
                min="1888"
                max="2100"
                value={draft.year ?? ''}
                onChange={(e) =>
                  setDraft({
                    ...draft,
                    year: e.target.value ? Number(e.target.value) : null,
                  })
                }
              />
            </label>
            <label>
              Synopsis
              <textarea
                maxLength={5000}
                value={draft.synopsis}
                onChange={(e) =>
                  setDraft({ ...draft, synopsis: e.target.value })
                }
              />
            </label>
            <label>
              Genres (comma separated)
              <input
                value={draft.genres.join(', ')}
                onChange={(e) =>
                  setDraft({
                    ...draft,
                    genres: e.target.value.split(',').map((x) => x.trim()),
                  })
                }
              />
            </label>
            <label>
              Cast (comma separated)
              <input
                value={draft.cast.join(', ')}
                onChange={(e) =>
                  setDraft({
                    ...draft,
                    cast: e.target.value.split(',').map((x) => x.trim()),
                  })
                }
              />
            </label>
            <label>
              Series
              <input
                maxLength={200}
                value={draft.series ?? ''}
                onChange={(e) =>
                  setDraft({ ...draft, series: e.target.value || null })
                }
              />
            </label>
            <label>
              Season
              <input
                type="number"
                min="0"
                max="99"
                value={draft.season ?? ''}
                onChange={(e) =>
                  setDraft({
                    ...draft,
                    season: e.target.value ? Number(e.target.value) : null,
                  })
                }
              />
            </label>
            <label>
              Episode
              <input
                type="number"
                min="1"
                max="999"
                value={draft.episode ?? ''}
                onChange={(e) =>
                  setDraft({
                    ...draft,
                    episode: e.target.value ? Number(e.target.value) : null,
                  })
                }
              />
            </label>
            <button disabled={busy}>Save title details</button>
          </form>
          {enabled && (
            <form
              onSubmit={(e) => {
                e.preventDefault();
                setBusy(true);
                setError('');
                setResults([]);
                void api<Candidate[]>('/metadata/search', 'POST', {
                  query,
                  kind,
                })
                  .then(setResults)
                  .catch((e) => setError(e.message))
                  .finally(() => setBusy(false));
              }}
            >
              <p>
                Searching sends only the text you enter to TMDB. Choose a result
                to apply its details and cache artwork.
              </p>
              <label>
                Search TMDB
                <input
                  required
                  minLength={2}
                  maxLength={80}
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                />
              </label>
              <label>
                Media type
                <select
                  value={kind}
                  onChange={(e) => {
                    setKind(e.target.value);
                    setResults([]);
                  }}
                >
                  <option value="movies">Movie</option>
                  <option value="tv">TV series</option>
                </select>
              </label>
              {kind === 'tv' && (
                <>
                  <label>
                    Match season (optional)
                    <input
                      type="number"
                      min="0"
                      max="99"
                      value={season}
                      onChange={(e) => setSeason(e.target.value)}
                    />
                  </label>
                  <label>
                    Match episode (optional)
                    <input
                      type="number"
                      min="1"
                      max="999"
                      value={episode}
                      onChange={(e) => setEpisode(e.target.value)}
                    />
                  </label>
                </>
              )}
              <button disabled={busy}>Search provider</button>
              <ul>
                {results.map((row) => (
                  <li key={row.id}>
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() =>
                        void change('/tmdb', 'POST', {
                          id: row.id,
                          kind: row.kind,
                          season: season === '' ? null : Number(season),
                          episode: episode === '' ? null : Number(episode),
                        })
                      }
                    >
                      Match {row.title}
                      {row.year ? ` (${row.year})` : ''}
                    </button>
                  </li>
                ))}
              </ul>
            </form>
          )}
        </details>
      )}
      {(enabled || value?.details.attribution.includes('TMDB')) && (
        <aside className="metadata-attribution" aria-label="Metadata credits">
          <a href="https://www.themoviedb.org" target="_blank" rel="noreferrer">
            <img src="/tmdb.svg" alt="TMDB" width="100" height="40" />
          </a>
          <p>
            This product uses the TMDB API but is not endorsed or certified by
            TMDB.
          </p>
        </aside>
      )}
    </>
  );
}
