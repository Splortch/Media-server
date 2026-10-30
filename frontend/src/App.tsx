import {
  useEffect,
  useRef,
  useState,
  useCallback,
  type FormEvent,
} from 'react';
import {
  api,
  duration,
  resetCsrf,
  type Media,
  type Progress,
} from './services/api';
import Player from './components/Player';
import Icon from './components/Icon';
import Settings from './pages/Settings';
import { History, type Collection } from './features/Personal';
type User = { name: string; role: string };
export default function App() {
  const mainRef = useRef<HTMLElement>(null);
  const authHeading = useRef<HTMLHeadingElement>(null);
  const [phase, setPhase] = useState<
    'loading' | 'setup' | 'login' | 'ready' | 'offline'
  >('loading');
  const [user, setUser] = useState<User>();
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [route, setRoute] = useState(location.hash.slice(1) || 'home');
  const [items, setItems] = useState<Media[]>([]);
  const [progress, setProgress] = useState<Progress[]>([]);
  const [item, setItem] = useState<Media>();
  const focusedRoute = useRef('');
  const requestEpoch = useRef(0);
  const [watching, setWatching] = useState<Media[]>([]);
  const [collections, setCollections] = useState<Collection[]>([]);
  const [collection, setCollection] = useState('');
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [moreBusy, setMoreBusy] = useState(false);
  const [debouncedQuery, setDebouncedQuery] = useState('');
  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(true);
  const [server, setServer] = useState<{
    version: string;
    mediaCount: number;
    libraryCount: number;
  }>();
  useEffect(() => {
    let active = true;
    (async () => {
      try {
        const setup = await api<{ required: boolean }>('/setup');
        if (!active) return;
        if (setup.required) {
          setPhase('setup');
          return;
        }
        try {
          const u = await api<User>('/auth/me');
          if (active) {
            setUser(u);
            setPhase('ready');
          }
        } catch {
          if (active) setPhase('login');
        }
      } catch (e) {
        if (active) {
          setError((e as Error).message);
          setPhase('offline');
        }
      }
    })();
    return () => {
      active = false;
    };
  }, []);
  const endSession = useCallback(() => {
    requestEpoch.current++;
    resetCsrf();
    setPhase('login');
    setUser(undefined);
    setServer(undefined);
    setError('');
    setMoreBusy(false);
    setItems([]);
    setProgress([]);
    setItem(undefined);
    setWatching([]);
    setCollections([]);
    setNextCursor(null);
    setQuery('');
    setCollection('');
    setLoading(true);
    location.hash = 'home';
  }, []);
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedQuery(query), 250);
    return () => clearTimeout(timer);
  }, [query]);
  useEffect(() => {
    const change = () => {
      setRoute(location.hash.slice(1) || 'home');
      setLoading(true);
      setError('');
      setItem(undefined);
    };
    window.addEventListener('hashchange', change);
    window.addEventListener('session-expired', endSession);
    return () => {
      window.removeEventListener('hashchange', change);
      window.removeEventListener('session-expired', endSession);
    };
  }, [endSession]);
  const pageUrl = (cursor?: string) =>
    `/media-page?limit=60&prefix=${encodeURIComponent(debouncedQuery)}${collection ? '&collectionId=' + collection : ''}${cursor ? '&cursor=' + encodeURIComponent(cursor) : ''}`;
  useEffect(() => {
    if (phase !== 'ready') return;
    let active = true;
    requestEpoch.current++;
    (async () => {
      setLoading(true);
      try {
        const [s, c, w] = await Promise.all([
          api<{ version: string; mediaCount: number; libraryCount: number }>(
            '/status',
          ),
          api<Collection[]>('/collections'),
          api<Media[]>('/continue-watching-media'),
        ]);
        let visible: Media[] = [];
        let detail: Media | undefined;
        let next: string | null = null;
        if (route.startsWith('media/')) detail = await api<Media>('/' + route);
        else if (route === 'home' || route === 'library') {
          const page = await api<{ items: Media[]; nextCursor: string | null }>(
            pageUrl(),
          );
          visible = page.items;
          next = page.nextCursor;
        }
        const ids = [
          ...new Set(
            [...visible, ...w, ...(detail ? [detail] : [])].map((x) => x.id),
          ),
        ];
        const p = ids.length
          ? await api<Progress[]>('/progress?mediaIds=' + ids.join(','))
          : [];
        if (active) {
          setMoreBusy(false);
          setServer(s);
          setCollections(c);
          setWatching(w);
          setItems(visible);
          setItem(detail);
          setNextCursor(next);
          setProgress(p);
        }
      } catch (e) {
        if (active) setError((e as Error).message);
      } finally {
        if (active) setLoading(false);
      }
    })();
    return () => {
      active = false;
    };
    // The URL builder uses only these query inputs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, route, debouncedQuery, collection]);
  async function loadMore() {
    if (!nextCursor || moreBusy) return;
    const epoch = requestEpoch.current;
    setMoreBusy(true);
    try {
      const page = await api<{ items: Media[]; nextCursor: string | null }>(
        pageUrl(nextCursor),
      );
      const ids = page.items.map((x) => x.id);
      const p = ids.length
        ? await api<Progress[]>('/progress?mediaIds=' + ids.join(','))
        : [];
      if (epoch !== requestEpoch.current) return;
      setItems((old) => [...old, ...page.items]);
      setProgress((old) => [
        ...old.filter((x) => !ids.includes(x.mediaItemId)),
        ...p,
      ]);
      setNextCursor(page.nextCursor);
    } catch (e) {
      if (epoch === requestEpoch.current) setError((e as Error).message);
    } finally {
      if (epoch === requestEpoch.current) setMoreBusy(false);
    }
  }
  useEffect(() => {
    const key = phase + ':' + route;
    if (phase === 'ready' && !loading) {
      if (focusedRoute.current !== key) {
        mainRef.current?.focus();
        focusedRoute.current = key;
      }
      document.title = `${route === 'settings' ? 'Settings' : route === 'history' ? 'Watch history' : item?.title || (route === 'library' ? 'Library' : 'Home')} · Media Server`;
    } else if (phase === 'setup' || phase === 'login') {
      if (focusedRoute.current !== key) {
        authHeading.current?.focus();
        focusedRoute.current = key;
      }
      document.title = `${phase === 'setup' ? 'Set up your server' : 'Sign in'} · Media Server`;
    }
  }, [phase, route, loading, item]);
  async function authenticate(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    setBusy(true);
    setError('');
    try {
      const values = {
        name: String(f.get('name')),
        password: String(f.get('password')),
        token: String(f.get('token') || ''),
      };
      if (phase === 'setup') {
        await api('/setup', 'POST', values);
        setPhase('login');
      } else {
        const u = await api<User>('/auth/login', 'POST', values);
        resetCsrf();
        setUser(u);
        setPhase('ready');
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function logout() {
    try {
      await api('/auth/logout', 'POST');
      endSession();
    } catch (e) {
      setError((e as Error).message);
    }
  }
  const position = (id: number) =>
    progress.find((p) => p.mediaItemId === id)?.seconds || 0;
  const cards = (list: Media[]) => (
    <div className="media-grid">
      {list.map((m, i) => (
        <a
          className={`media-card tone-${i % 4}`}
          href={`#media/${m.id}`}
          key={m.id}
        >
          <div className="cover">
            <span className="format">
              {m.videoCodec.toUpperCase() || 'VIDEO'}
            </span>
            <span className="monogram" aria-hidden="true">
              {m.title.slice(0, 1).toUpperCase()}
            </span>
            <span className="play-symbol" aria-hidden="true">
              <Icon name="play" />
            </span>
            {position(m.id) > 0 && (
              <progress
                aria-label={`${m.title} watch progress`}
                value={position(m.id)}
                max={m.duration || 1}
              />
            )}
          </div>
          <h3>{m.title}</h3>
          <p>
            {duration(m.duration)} <span aria-hidden="true">·</span>{' '}
            {m.container.split(',')[0].toUpperCase()}
          </p>
        </a>
      ))}
    </div>
  );
  if (phase === 'loading')
    return (
      <main className="auth-shell">
        <p role="status">Connecting to your server…</p>
      </main>
    );
  if (phase === 'offline')
    return (
      <main className="auth-shell">
        <div className="panel">
          <h1>Server unavailable</h1>
          <p role="alert">{error}</p>
          <button onClick={() => location.reload()}>Try again</button>
        </div>
      </main>
    );
  if (phase !== 'ready')
    return (
      <main className="auth-shell">
        <div className="auth-intro">
          <a className="brand" href="#home">
            <span className="brand-icon">M</span> MEDIA SERVER
          </a>
          <p className="eyebrow">YOUR COLLECTION. YOUR RULES.</p>
          <h1>
            A little closer
            <br />
            to movie night.
          </h1>
          <p className="muted">Your own media. A private place to enjoy it.</p>
        </div>
        <form
          className="panel auth-form"
          onSubmit={authenticate}
          aria-busy={busy}
        >
          <p className="eyebrow">
            {phase === 'setup' ? 'WELCOME HOME' : 'GOOD TO SEE YOU'}
          </p>
          <h2 ref={authHeading} tabIndex={-1}>
            {phase === 'setup' ? 'Set up your server' : 'Sign in'}
          </h2>
          {phase === 'setup' && (
            <>
              <p className="muted">
                Create your local administrator account. Find the setup token in
                your server data directory.
              </p>
              <label>
                Setup token
                <input
                  name="token"
                  required
                  autoComplete="off"
                  type="password"
                />
              </label>
            </>
          )}
          <label>
            Username
            <input
              name="name"
              required
              maxLength={64}
              autoComplete="username"
            />
          </label>
          <label>
            Password
            <input
              name="password"
              type="password"
              required
              minLength={12}
              maxLength={256}
              autoComplete={
                phase === 'setup' ? 'new-password' : 'current-password'
              }
            />
          </label>
          {phase === 'setup' && (
            <p className="muted">
              Use at least 12 characters. No cloud account needed.
            </p>
          )}
          {error && (
            <p role="alert" className="error">
              {error}
            </p>
          )}
          <button disabled={busy}>
            {busy
              ? 'Please wait…'
              : phase === 'setup'
                ? 'Create administrator'
                : 'Sign in'}
          </button>
        </form>
      </main>
    );
  return (
    <div className="app-shell">
      <a
        className="skip-link"
        href="#main-content"
        onClick={(e) => {
          e.preventDefault();
          document.getElementById('main-content')?.focus();
        }}
      >
        Skip to content
      </a>
      <aside>
        <a className="brand" href="#home">
          <span className="brand-icon">M</span> MEDIA SERVER
        </a>
        <p className="nav-label">YOUR SPACE</p>
        <nav aria-label="Main navigation">
          <a
            aria-current={route === 'home' ? 'page' : undefined}
            className={route === 'home' ? 'active' : ''}
            href="#home"
          >
            <Icon name="home" /> <span>Home</span>
          </a>
          <a
            aria-current={route === 'library' ? 'page' : undefined}
            className={route === 'library' ? 'active' : ''}
            href="#library"
          >
            <Icon name="library" /> <span>Library</span>
          </a>
          <a
            aria-current={route === 'settings' ? 'page' : undefined}
            className={route === 'settings' ? 'active' : ''}
            href="#settings"
          >
            <Icon name="settings" /> <span>Settings</span>
          </a>
          <a
            aria-current={route === 'history' ? 'page' : undefined}
            className={route === 'history' ? 'active' : ''}
            href="#history"
          >
            <Icon name="history" /> <span>Watch history</span>
          </a>
        </nav>
        <div className="sidebar-bottom">
          <div className="connection-label">
            <span className="status-dot" /> Server connected
          </div>
          <div className="account-summary">
            <span className="avatar" aria-hidden="true">
              {user?.name.slice(0, 1).toUpperCase()}
            </span>
            <div>
              <strong>{user?.name}</strong>
              <p>
                {user?.role} · v{server?.version || '0.1.0-alpha'}
              </p>
            </div>
          </div>
          <button className="text-button" onClick={() => void logout()}>
            <Icon name="logout" /> Sign out · {user?.name}
          </button>
        </div>
      </aside>
      <div className="workspace">
        <header>
          <div className="workspace-heading">
            <span className="workspace-kicker">YOUR SPACE</span>
            <span>
              {route === 'settings'
                ? 'Server settings'
                : route === 'history'
                  ? 'Watch history'
                  : route === 'library'
                    ? 'Media library'
                    : route.startsWith('media/')
                      ? 'Now showing'
                      : 'Home'}
            </span>
          </div>
          <span className="alpha">EARLY ALPHA</span>
          <button className="mobile-signout" onClick={() => void logout()}>
            <Icon name="logout" /> Sign out
          </button>
        </header>
        <main ref={mainRef} id="main-content" tabIndex={-1} aria-busy={loading}>
          {error && (
            <p role="alert" className="error">
              {error}
            </p>
          )}
          {loading && <p role="status">Loading your collection…</p>}
          {route === 'settings' ? (
            <Settings admin={user?.role === 'Admin'} />
          ) : route === 'history' ? (
            <History />
          ) : route.startsWith('media/') ? (
            item && (
              <>
                <a href="#library" className="back-link">
                  <Icon name="back" /> Back to library
                </a>
                <div className="detail-heading">
                  <p className="eyebrow">FROM YOUR COLLECTION</p>
                  <h1>{item.title}</h1>
                  <p className="muted">
                    {duration(item.duration)} · {item.videoCodec.toUpperCase()}{' '}
                    / {item.audioCodec.toUpperCase() || 'No audio'} ·{' '}
                    {(item.size / 1024 ** 3).toFixed(2)} GB
                  </p>
                </div>
                {user?.role === 'Admin' && (
                  <details className="title-admin">
                    <summary>Manage content rating</summary>
                    <label>
                      Content rating
                      <select
                        aria-label="Content rating"
                        defaultValue={item.contentRating ?? 0}
                        onChange={(e) => {
                          void api(`/media/${item.id}/rating`, 'PUT', {
                            rating: Number(e.target.value),
                          }).catch((error) => setError(error.message));
                        }}
                      >
                        <option value="0">
                          Unrated (hidden from restricted accounts)
                        </option>
                        <option value="10">General (10)</option>
                        <option value="13">Teen (13)</option>
                        <option value="16">Mature (16)</option>
                        <option value="18">Adult (18)</option>
                      </select>
                    </label>
                  </details>
                )}
                <Player
                  item={item}
                  resume={position(item.id)}
                  progressRevision={
                    progress.find((p) => p.mediaItemId === item.id)?.revision ??
                    0
                  }
                />
              </>
            )
          ) : (
            <>
              {route === 'home' && (
                <section className="hero">
                  <div className="hero-copy">
                    <p className="eyebrow">YOUR COLLECTION. YOUR EVENING.</p>
                    <h1>
                      Make room for
                      <br />a good movie night.
                    </h1>
                    <p>
                      Welcome back, {user?.name}. Settle in with something from
                      your own collection.
                    </p>
                    <a
                      className="button"
                      href={
                        items.length || user?.role !== 'Admin'
                          ? '#library'
                          : '#settings'
                      }
                    >
                      {items.length || user?.role !== 'Admin'
                        ? 'Explore library'
                        : 'Add your first library'}{' '}
                      <Icon name="arrow" />
                    </a>
                    <div className="hero-stats">
                      <span>
                        <strong>{server?.mediaCount || 0}</strong> videos
                      </span>
                      <span>
                        <strong>{server?.libraryCount || 0}</strong> libraries
                      </span>
                      <span>100% yours</span>
                    </div>
                  </div>
                  <div className="hero-art" aria-hidden="true">
                    <div className="hero-orbit" />
                    <div className="cinema-tile">
                      <Icon name="play" />
                    </div>
                    <div className="film-lines">
                      <span />
                      <span />
                      <span />
                    </div>
                  </div>
                </section>
              )}
              {route === 'home' && watching.length > 0 && (
                <section>
                  <div className="section-heading">
                    <h2>Pick up where you left off</h2>
                    <span className="muted">Continue watching</span>
                  </div>
                  {cards(watching)}
                </section>
              )}
              <section>
                <div className="section-heading">
                  <div>
                    <p className="eyebrow">CURATED BY YOU</p>
                    {route === 'home' ? (
                      <h2>Your collection</h2>
                    ) : (
                      <h1>Media library</h1>
                    )}
                  </div>
                  <div className="library-toolbar">
                    <label>
                      Smart collection
                      <select
                        aria-label="Smart collection"
                        value={collection}
                        onChange={(e) => setCollection(e.target.value)}
                      >
                        <option value="">All media</option>
                        {collections.map((c) => (
                          <option value={c.id} key={c.id}>
                            {c.name}
                          </option>
                        ))}
                      </select>
                    </label>
                    <label className="search-label">
                      <span className="sr-only">Search library</span>
                      <Icon name="search" />
                      <input
                        type="search"
                        placeholder="Search your collection…"
                        value={query}
                        onChange={(e) => setQuery(e.target.value)}
                      />
                    </label>
                  </div>
                </div>
                {query && (
                  <p role="status">
                    {query !== debouncedQuery || loading
                      ? 'Searching…'
                      : `${items.length} matching videos${nextCursor ? ' loaded' : ''}`}
                  </p>
                )}
                {!items.length && !loading ? (
                  <div className="empty-state">
                    <span aria-hidden="true">▦</span>
                    <h2>
                      {query || collection
                        ? 'No matching videos.'
                        : user?.role === 'Admin'
                          ? 'A great collection starts somewhere.'
                          : 'No media available to this account.'}
                    </h2>
                    <p className="muted">
                      {query || collection
                        ? 'Try a different title prefix or collection rule.'
                        : user?.role === 'Admin'
                          ? 'Add a media directory in Settings, then scan it. Real files will appear here.'
                          : 'Ask your administrator for library access. Rating-restricted accounts cannot see unclassified media.'}
                    </p>
                    {query || collection ? (
                      <button
                        onClick={() => {
                          setQuery('');
                          setCollection('');
                        }}
                      >
                        Clear filters
                      </button>
                    ) : (
                      user?.role === 'Admin' && (
                        <a href="#settings" className="button">
                          Set up a library
                        </a>
                      )
                    )}
                  </div>
                ) : (
                  cards(items)
                )}
                {nextCursor && (
                  <button
                    className="secondary load-more"
                    disabled={moreBusy || loading}
                    onClick={() => void loadMore()}
                  >
                    {moreBusy ? 'Loading more…' : 'Load more videos'}
                  </button>
                )}
              </section>
            </>
          )}
        </main>
        <footer>Made for your media. Built in the open.</footer>
      </div>
    </div>
  );
}
