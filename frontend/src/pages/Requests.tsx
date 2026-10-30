import { useEffect, useState, type FormEvent } from 'react';
import { api, serverTime } from '../services/api';
import type { components } from '../../../shared/api/src/generated';

type Request = components['schemas']['MovieRequestView'];
type Library = { id: number; name: string };
export default function Requests({
  admin,
  initialTitle = '',
}: {
  admin: boolean;
  initialTitle?: string;
}) {
  const [items, setItems] = useState<Request[]>([]);
  const [libraries, setLibraries] = useState<Library[]>([]);
  const [title, setTitle] = useState(initialTitle);
  const [query, setQuery] = useState(initialTitle);
  const [matches, setMatches] = useState<{ id: number; title: string }[]>([]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [message, setMessage] = useState('');
  const [next, setNext] = useState<number>();
  const url = (before?: number) =>
    '/requests?q=' +
    encodeURIComponent(query) +
    (before ? '&before=' + before : '');
  useEffect(() => {
    let active = true;
    api<Library[]>('/browse-libraries')
      .then((value) => {
        if (active) setLibraries(value);
      })
      .catch((e) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
  }, []);
  useEffect(() => {
    let active = true;
    const timer = setTimeout(() => {
      setLoading(true);
      Promise.all([
        api<Request[]>('/requests?q=' + encodeURIComponent(query)),
        query.trim()
          ? api<{ items: { id: number; title: string }[] }>(
              '/media-page?limit=10&prefix=' +
                encodeURIComponent(query.slice(0, 128)),
            ).then((x) => x.items)
          : Promise.resolve([]),
      ])
        .then(([rows, media]) => {
          if (active) {
            setItems(rows);
            setNext(rows.length === 200 ? rows.at(-1)?.id : undefined);
            setMatches(media);
          }
        })
        .catch((e) => {
          if (active) setError(e.message);
        })
        .finally(() => {
          if (active) setLoading(false);
        });
    }, 250);
    return () => {
      active = false;
      clearTimeout(timer);
    };
  }, [query]);
  async function refresh() {
    const rows = await api<Request[]>(url());
    setItems(rows);
    setNext(rows.length === 200 ? rows.at(-1)?.id : undefined);
  }
  async function mutate(action: () => Promise<unknown>, success: string) {
    setBusy(true);
    setError('');
    setMessage('');
    try {
      await action();
      await refresh();
      setMessage(success);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    await mutate(async () => {
      await api('/requests', 'POST', {
        title,
        year: data.get('year') ? Number(data.get('year')) : null,
        libraryId: Number(data.get('library')),
        note: data.get('note'),
      });
      form.reset();
      setTitle('');
    }, 'Request sent. Your administrator can review it here.');
  }
  return (
    <>
      <p className="eyebrow">PLAN THE NEXT MOVIE NIGHT</p>
      <h1>Movie requests</h1>
      <p className="muted">
        Ask your administrator to add a movie. Requests never download or
        acquire media automatically.
      </p>
      <section className="panel">
        <h2>Find a title or existing request</h2>
        <label>
          Search requests and your library
          <input
            type="search"
            maxLength={160}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </label>
        {loading && <p role="status">Looking for requests…</p>}
        {!!matches.length && (
          <>
            <h3>Already in your library</h3>
            <ul>
              {matches.map((x) => (
                <li key={x.id}>
                  <a href={'#media/' + x.id}>{x.title}</a>
                </li>
              ))}
            </ul>
          </>
        )}
        <p className="muted">
          Only accessible libraries and requests are shown. Rating-restricted
          accounts see their own requests only.
        </p>
      </section>
      <section className="panel">
        <h2>Request a movie</h2>
        <p>
          Check the search above first. Your title and note are shared with
          authorized members of the selected library and administrators.
        </p>
        <form
          onSubmit={(e) => void submit(e)}
          className="settings-form"
          aria-busy={busy}
        >
          <fieldset disabled={busy || !libraries.length}>
            <legend>New request</legend>
            <label>
              Movie title
              <input
                required
                maxLength={160}
                value={title}
                onChange={(e) => {
                  setTitle(e.target.value);
                  setQuery(e.target.value);
                }}
              />
            </label>
            <label>
              Year (optional)
              <input type="number" name="year" min={1888} max={2100} />
            </label>
            <label>
              Library
              <select name="library" required>
                {libraries.map((x) => (
                  <option key={x.id} value={x.id}>
                    {x.name}
                  </option>
                ))}
              </select>
            </label>
            <label>
              Note (optional)
              <textarea name="note" maxLength={500} />
            </label>
            <button>Send movie request</button>
          </fieldset>
        </form>
        {!libraries.length && (
          <p>
            Ask an administrator for library access before requesting a movie.
          </p>
        )}
      </section>
      {error && (
        <p className="error" role="alert">
          {error}{' '}
          <button
            className="secondary"
            disabled={busy}
            onClick={() => void mutate(refresh, 'Requests refreshed.')}
          >
            Refresh requests
          </button>
        </p>
      )}
      {message && <p role="status">{message}</p>}
      <section aria-label="Movie requests">
        {!loading && !items.length && (
          <p>No visible requests match this search.</p>
        )}
        {items.map((item) => (
          <article className="panel" key={item.id}>
            <h2>
              {item.title}
              {item.year ? ` (${item.year})` : ''}
            </h2>
            <p className="eyebrow">
              {item.state} · {item.library}
            </p>
            {item.note && <p>{item.note}</p>}
            {item.reply && (
              <p>
                <strong>Administrator reply:</strong> {item.reply}
              </p>
            )}
            <p className="muted">
              Updated {serverTime(item.updatedAt).toLocaleString()} ·{' '}
              {item.supportCount} supporting members
            </p>
            {item.mediaItemId != null && (
              <a className="button" href={'#media/' + item.mediaItemId}>
                View added movie
              </a>
            )}
            <div className="action-group">
              {!item.mine && !['Added', 'Declined'].includes(item.state) && (
                <button
                  className="secondary"
                  disabled={busy}
                  onClick={() =>
                    void mutate(
                      () =>
                        api(
                          `/requests/${item.id}/support`,
                          item.supported ? 'DELETE' : 'PUT',
                        ),
                      item.supported
                        ? 'Support removed.'
                        : 'Request supported.',
                    )
                  }
                >
                  {item.supported ? 'Remove support' : 'Support request'}
                </button>
              )}
              {(item.mine || admin) && (
                <details>
                  <summary>
                    {item.mine ? 'Withdraw request' : 'Remove request'}
                  </summary>
                  <p>This permanently removes the request and its support.</p>
                  <button
                    className="danger"
                    disabled={busy}
                    onClick={() =>
                      void mutate(
                        () => api(`/requests/${item.id}`, 'DELETE'),
                        'Request removed.',
                      )
                    }
                  >
                    Confirm removal of {item.title}
                  </button>
                </details>
              )}
            </div>
            {admin && (
              <Review
                key={item.id + ':' + item.revision}
                item={item}
                disabled={busy}
                onSave={(value) =>
                  mutate(
                    () => api(`/requests/${item.id}`, 'PUT', value),
                    'Review saved.',
                  )
                }
              />
            )}
          </article>
        ))}
        {next && (
          <button
            disabled={busy}
            onClick={() => {
              setBusy(true);
              setError('');
              void api<Request[]>(url(next))
                .then((rows) => {
                  setItems((old) => [...old, ...rows]);
                  setNext(rows.length === 200 ? rows.at(-1)?.id : undefined);
                })
                .catch((e) => setError(e.message))
                .finally(() => setBusy(false));
            }}
          >
            Load older requests
          </button>
        )}
      </section>
    </>
  );
}
function Review({
  item,
  disabled,
  onSave,
}: {
  item: Request;
  disabled: boolean;
  onSave: (input: unknown) => Promise<void>;
}) {
  const [state, setState] = useState(item.state);
  const [query, setQuery] = useState('');
  const [media, setMedia] = useState<{ id: number; title: string }[]>([]);
  const [mediaId, setMediaId] = useState(item.mediaItemId?.toString() ?? '');
  const [error, setError] = useState('');
  useEffect(() => {
    if (state !== 'Added') return;
    let active = true;
    const timer = setTimeout(() => {
      api<{ items: { id: number; title: string }[] }>(
        `/media-page?limit=20&libraryId=${item.libraryId}&prefix=${encodeURIComponent(query)}`,
      )
        .then((value) => {
          if (active) setMedia(value.items);
        })
        .catch((e) => {
          if (active) setError(e.message);
        });
    }, 250);
    return () => {
      active = false;
      clearTimeout(timer);
    };
  }, [query, state, item.libraryId]);
  return (
    <details>
      <summary>Review request: {item.title}</summary>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          void onSave({
            state,
            reply: data.get('reply'),
            mediaItemId: state === 'Added' ? Number(mediaId) : null,
            revision: item.revision,
          });
        }}
      >
        <fieldset disabled={disabled}>
          <legend>Administrator review</legend>
          <label>
            Status
            <select value={state} onChange={(e) => setState(e.target.value)}>
              {['Requested', 'Considering', 'Added', 'Declined'].map((x) => (
                <option key={x}>{x}</option>
              ))}
            </select>
          </label>
          <label>
            Reply
            <textarea name="reply" maxLength={500} defaultValue={item.reply} />
          </label>
          {state === 'Added' && (
            <>
              <label>
                Find the added movie
                <input
                  maxLength={128}
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                />
              </label>
              <label>
                Confirm library title
                <select
                  required
                  value={mediaId}
                  onChange={(e) => setMediaId(e.target.value)}
                >
                  <option value="">Choose a movie explicitly</option>
                  {item.mediaItemId &&
                    !media.some((x) => x.id === item.mediaItemId) && (
                      <option value={item.mediaItemId}>
                        Previously linked title #{item.mediaItemId}
                      </option>
                    )}
                  {media.map((x) => (
                    <option key={x.id} value={x.id}>
                      {x.title}
                    </option>
                  ))}
                </select>
              </label>
              <p>
                No automatic match is made. Confirm the selected movie is
                correct.
              </p>
            </>
          )}
          <button>Save review for {item.title}</button>
        </fieldset>
        {error && <p role="alert">{error}</p>}
      </form>
    </details>
  );
}
