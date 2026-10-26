import { useEffect, useState, type FormEvent } from 'react';
import {
  api,
  duration,
  resetCsrf,
  type Media,
  type Progress,
} from './services/api';
import Player from './components/Player';
import Settings from './pages/Settings';
type User = { name: string; role: string };
export default function App() {
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
  useEffect(() => {
    const change = () => {
      setRoute(location.hash.slice(1) || 'home');
      setLoading(true);
      setError('');
      setItem(undefined);
    };
    const expired = () => {
      setPhase('login');
      setUser(undefined);
      resetCsrf();
    };
    window.addEventListener('hashchange', change);
    window.addEventListener('session-expired', expired);
    return () => {
      window.removeEventListener('hashchange', change);
      window.removeEventListener('session-expired', expired);
    };
  }, []);
  useEffect(() => {
    if (phase !== 'ready') return;
    let active = true;
    (async () => {
      try {
        const [p, s] = await Promise.all([
          api<Progress[]>('/progress'),
          api<{ version: string; mediaCount: number; libraryCount: number }>(
            '/status',
          ),
        ]);
        if (active) {
          setProgress(p);
          setServer(s);
        }
        if (route.startsWith('media/')) {
          const detail = await api<Media>('/' + route);
          if (active) setItem(detail);
        } else if (route !== 'settings') {
          let all: Media[] = [];
          for (let offset = 0; ; offset += 200) {
            const page = await api<Media[]>(
              `/media?offset=${offset}&limit=200`,
            );
            all = all.concat(page);
            if (page.length < 200) break;
          }
          if (active) setItems(all);
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
  }, [phase, route]);
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
      resetCsrf();
      setPhase('login');
      setUser(undefined);
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
              ▶
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
        <form className="panel auth-form" onSubmit={authenticate}>
          <p className="eyebrow">
            {phase === 'setup' ? 'WELCOME HOME' : 'GOOD TO SEE YOU'}
          </p>
          <h2>{phase === 'setup' ? 'Set up your server' : 'Sign in'}</h2>
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
  const watching = items.filter(
    (m) =>
      position(m.id) > 0 && (!m.duration || position(m.id) < m.duration * 0.95),
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
          <a className={route === 'home' ? 'active' : ''} href="#home">
            <span aria-hidden="true">⌂</span> <span>Home</span>
          </a>
          <a className={route === 'library' ? 'active' : ''} href="#library">
            <span aria-hidden="true">▦</span> <span>Library</span>
          </a>
          <a className={route === 'settings' ? 'active' : ''} href="#settings">
            <span aria-hidden="true">⚙</span> <span>Settings</span>
          </a>
        </nav>
        <div className="sidebar-bottom">
          <span className="status-dot" /> Connected locally
          <p>v{server?.version || '0.1.0-alpha'}</p>
          <button className="text-button" onClick={() => void logout()}>
            Sign out · {user?.name}
          </button>
        </div>
      </aside>
      <div className="workspace">
        <header>
          <span className="muted">Your private screening room</span>
          <span className="alpha">EARLY ALPHA</span>
          <button className="mobile-signout" onClick={() => void logout()}>
            Sign out
          </button>
        </header>
        <main id="main-content" tabIndex={-1}>
          {error && (
            <p role="alert" className="error">
              {error}
            </p>
          )}
          {loading && <p role="status">Loading your collection…</p>}
          {route === 'settings' ? (
            <Settings />
          ) : route.startsWith('media/') ? (
            item && (
              <>
                <a href="#library" className="back-link">
                  ← Back to library
                </a>
                <p className="eyebrow">FROM YOUR COLLECTION</p>
                <h1>{item.title}</h1>
                <p className="muted">
                  {duration(item.duration)} · {item.videoCodec.toUpperCase()} /{' '}
                  {item.audioCodec.toUpperCase() || 'No audio'} ·{' '}
                  {(item.size / 1024 ** 3).toFixed(2)} GB
                </p>
                <Player item={item} resume={position(item.id)} />
              </>
            )
          ) : (
            <>
              {route === 'home' && (
                <section className="hero">
                  <p className="eyebrow">ALL YOUR FAVORITES, ONE PLACE</p>
                  <h1>
                    Tonight starts
                    <br />
                    with your collection.
                  </h1>
                  <p>
                    Less searching. More watching. Welcome home, {user?.name}.
                  </p>
                  <a
                    className="button"
                    href={items.length ? '#library' : '#settings'}
                  >
                    {items.length
                      ? 'Explore library'
                      : 'Add your first library'}{' '}
                    <span aria-hidden="true">↗</span>
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
                    <h2>
                      {route === 'home' ? 'Your collection' : 'Media library'}
                    </h2>
                  </div>
                  <label className="search-label">
                    <span className="sr-only">Search library</span>
                    <input
                      type="search"
                      placeholder="Search your collection…"
                      value={query}
                      onChange={(e) => setQuery(e.target.value)}
                    />
                  </label>
                </div>
                {!items.length && !loading ? (
                  <div className="empty-state">
                    <span aria-hidden="true">▦</span>
                    <h2>A great collection starts somewhere.</h2>
                    <p className="muted">
                      Add a media directory in Settings, then scan it. Real
                      files will appear here.
                    </p>
                    <a href="#settings" className="button">
                      Set up a library
                    </a>
                  </div>
                ) : (
                  cards(
                    items.filter((m) =>
                      m.title.toLowerCase().includes(query.toLowerCase()),
                    ),
                  )
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
