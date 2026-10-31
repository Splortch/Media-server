import { useEffect, useRef, useState, type FormEvent } from 'react';
import {
  api,
  playbackRequest,
  playbackFileUrl,
  connect,
  login,
  logout,
  forgetSavedSession,
  preferences,
  desktopCapabilities,
  savePreferences,
  errorMessage,
  expired,
} from './bridge';
import {
  ProgressWriter,
  type Media,
  type Detail,
  type ProgressState,
  type Tracks,
  type SessionInfo,
} from '../../../shared/api/src/client';
import {
  readSavedServers,
  rememberServer,
  writeSavedServers,
  serverOrigin,
} from './savedServers';
import Player from '../../../frontend/src/components/Player';
import Icon from '../../../frontend/src/components/Icon';
import TitleDetails from '../../../frontend/src/components/TitleDetails';
import { duration } from '../../../frontend/src/services/api';
type Selection = {
  item: Detail;
  progress: ProgressState;
  url?: string;
  startAt?: number;
  tracks: Tracks;
  writer: ProgressWriter;
};
function MediaCards({
  list,
  onOpen,
}: {
  list: (Media | Detail)[];
  onOpen: (item: Media | Detail) => Promise<void>;
}) {
  return (
    <div className="media-grid">
      {list.map((m, i) => (
        <button
          className={`media-card desktop-card tone-${i % 4}`}
          key={m.id}
          data-media-id={m.id}
          onClick={() => void onOpen(m)}
        >
          <div className="cover">
            <span className="monogram" aria-hidden="true">
              {m.title[0]}
            </span>
            <span className="play-symbol" aria-hidden="true">
              <Icon name="arrow" />
            </span>
          </div>
          <h3>{m.title}</h3>
          <p>
            {duration(m.duration)} · {m.videoCodec.toUpperCase()}
          </p>
        </button>
      ))}
    </div>
  );
}

export default function App() {
  const [address, setAddress] = useState('https://');
  const [savedServers, setSavedServers] = useState(readSavedServers);
  const [serverName, setServerName] = useState('');
  const [remember, setRemember] = useState(false);
  const [persistentSessions, setPersistentSessions] = useState(false);
  const [clientPlatform, setClientPlatform] = useState('desktop');
  const playerEngine =
    clientPlatform === 'windows'
      ? 'WebView2'
      : clientPlatform === 'macos'
        ? 'WKWebView'
        : clientPlatform === 'linux'
          ? 'WebKitGTK'
          : 'HTML video';
  useEffect(() => {
    void desktopCapabilities()
      .then((c) => {
        setPersistentSessions(c.persistentSessions);
        setClientPlatform(c.os);
      })
      .catch(() => {});
  }, []);
  const [theme, setTheme] = useState('system');
  const [connected, setConnected] = useState(false);
  const [version, setVersion] = useState('');
  const [user, setUser] = useState<{ name: string; role: string }>();
  const [items, setItems] = useState<Media[]>([]);
  const [watching, setWatching] = useState<ProgressState[]>([]);
  const [continueItems, setContinueItems] = useState<Detail[]>([]);
  const [selection, setSelection] = useState<Selection>();
  const detailHeading = useRef<HTMLHeadingElement>(null);
  const listContext = useRef({ id: 0, scroll: 0 });
  const restoreList = useRef(false);
  const [sessions, setSessions] = useState<SessionInfo[]>([]);
  const [view, setView] = useState<'library' | 'settings'>('library');
  const [search, setSearch] = useState('');
  const [offset, setOffset] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [online, setOnline] = useState(true);
  const mounted = useRef(true);
  const searchTimer = useRef<ReturnType<typeof setTimeout> | undefined>(
    undefined,
  );
  const loadVersion = useRef(0);
  useEffect(() => {
    if (selection && !selection.url) detailHeading.current?.focus();
    else if (!selection && restoreList.current && !busy) {
      document
        .querySelector<HTMLElement>(
          `[data-media-id="${listContext.current.id}"]`,
        )
        ?.focus({ preventScroll: true });
      window.scrollTo({ top: listContext.current.scroll, behavior: 'instant' });
      restoreList.current = false;
    }
  }, [selection, busy]);

  useEffect(() => {
    mounted.current = true;
    preferences()
      .then((p) => {
        if (!mounted.current) return;
        if (typeof p.address === 'string') setAddress(p.address);
        if (p.theme) setTheme(p.theme);
      })
      .catch(() => {});
    return () => {
      mounted.current = false;
    };
  }, []);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);
  useEffect(() => {
    const onExpired = () => {
      setUser(undefined);
      setSelection(undefined);
      setItems([]);
      setWatching([]);
      setContinueItems([]);
      setSessions([]);
      setError('Your session expired. Sign in again.');
    };
    window.addEventListener('desktop-session-expired', onExpired);
    return () =>
      window.removeEventListener('desktop-session-expired', onExpired);
  }, []);

  function failure(e: unknown) {
    setError(errorMessage(e));
    if (expired(e)) {
      setUser(undefined);
      setSelection(undefined);
      setItems([]);
      setWatching([]);
      setContinueItems([]);
      setSessions([]);
    } else setOnline(false);
  }
  async function load(query = search, start = offset) {
    const requestVersion = ++loadVersion.current;
    setBusy(true);
    setError('');
    try {
      const [list, progress] = await Promise.all([
        api.media(query, start),
        api.continuing(),
      ]);
      const recent = await Promise.all(
        progress.slice(0, 24).map((p) => api.detail(p.mediaItemId)),
      );
      if (requestVersion !== loadVersion.current) return;
      setContinueItems(recent);
      setItems(list);
      setWatching(progress);
      setOnline(true);
    } catch (e) {
      if (requestVersion === loadVersion.current) failure(e);
    } finally {
      if (requestVersion === loadVersion.current) setBusy(false);
    }
  }
  useEffect(() => {
    if (!user) return;
    const update = () => {
      void api
        .me()
        .then(() => setOnline(true))
        .catch(failure);
    };
    window.addEventListener('focus', update);
    return () => window.removeEventListener('focus', update);
  }, [user]);
  useEffect(() => {
    if (!selection) return;
    const shortcut = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.altKey || event.metaKey) return;
      if (
        event.target instanceof HTMLElement &&
        (event.target.closest(
          'input,textarea,select,button,video,.player-surface',
        ) ||
          event.target.isContentEditable)
      )
        return;
      const video = document.querySelector('video');
      if (!video) return;
      if (event.key === ' ' || event.key.toLowerCase() === 'k') {
        event.preventDefault();
        if (video.paused)
          void video.play().catch(() => setError('Playback could not start.'));
        else video.pause();
      }
      if (event.key.toLowerCase() === 'f') {
        event.preventDefault();
        if (document.fullscreenElement) void document.exitFullscreen();
        else
          void (video.closest('.player-surface') ?? video)
            .requestFullscreen()
            .catch(() => setError('Fullscreen is unavailable.'));
      }
      if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
        event.preventDefault();
        if (Number.isFinite(video.duration))
          video.currentTime = Math.max(
            0,
            Math.min(
              video.duration,
              video.currentTime + (event.key === 'ArrowRight' ? 10 : -10),
            ),
          );
      }
    };
    window.addEventListener('keydown', shortcut);
    return () => window.removeEventListener('keydown', shortcut);
  }, [selection]);
  async function attach(e: FormEvent) {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setError('');
    try {
      const origin = serverOrigin(address);
      const info = await connect(origin, remember);
      if (info.setupRequired)
        throw new Error(
          'This server needs initial setup. Open its web interface in your browser first.',
        );
      setAddress(origin);
      const updatedServers = rememberServer(savedServers, origin, serverName);
      try {
        writeSavedServers(updatedServers);
        setSavedServers(updatedServers);
      } catch {
        setError(
          'Connected, but the server address could not be saved on this device.',
        );
      }
      setConnected(true);
      setVersion(info.serverVersion);
      setOnline(true);
      await savePreferences(origin, theme);
      if (remember) {
        try {
          setUser(await api.me());
          await load('', 0);
        } catch (e) {
          if (!expired(e)) throw e;
        }
      }
    } catch (e) {
      failure(e);
    } finally {
      setBusy(false);
    }
  }
  async function signIn(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (busy) return;
    const form = e.currentTarget;
    const data = new FormData(form);
    const password = String(data.get('password') || '');
    form.reset();
    setBusy(true);
    setError('');
    try {
      const account = await login(String(data.get('name') || ''), password);
      setUser(account);
      await load('', 0);
      const video = document.createElement('video');
      const formats = [
        ['mp4', 'video/mp4; codecs="avc1.42E01E, mp4a.40.2"'],
        ['webm', 'video/webm; codecs="vp9, opus"'],
      ];
      const supported = formats
        .filter(([, mime]) => video.canPlayType(mime))
        .map(([name]) => name);
      await api.capabilities(supported, [], {
        platform: clientPlatform,
        player: playerEngine,
      });
    } catch (e) {
      failure(e);
    } finally {
      setBusy(false);
    }
  }
  async function open(item: Media | Detail) {
    const requestVersion = ++loadVersion.current;
    listContext.current = { id: item.id, scroll: window.scrollY };
    setBusy(true);
    setError('');
    try {
      const [detail, progress, tracks] = await Promise.all([
        api.detail(item.id),
        api.progress(item.id),
        api.tracks(item.id),
      ]);
      if (requestVersion !== loadVersion.current) return;
      setSelection({
        item: detail,
        progress,
        tracks,
        writer: new ProgressWriter(api, item.id, progress.revision),
      });
      setOnline(true);
    } catch (e) {
      if (requestVersion === loadVersion.current) failure(e);
    } finally {
      if (requestVersion === loadVersion.current) setBusy(false);
    }
  }
  async function beginPlayback(resume: boolean) {
    if (!selection || busy) return;
    const requestVersion = loadVersion.current;
    const id = selection.item.id;
    setBusy(true);
    setError('');
    try {
      const [url, progress] = await Promise.all([
        api.transport.streamUrl(id),
        api.progress(id),
      ]);
      if (requestVersion !== loadVersion.current) return;
      setSelection({
        ...selection,
        url,
        progress,
        startAt: resume && !progress.completed ? progress.seconds : 0,
        writer: new ProgressWriter(api, id, progress.revision),
      });
    } catch (e) {
      if (requestVersion === loadVersion.current) failure(e);
    } finally {
      if (requestVersion === loadVersion.current) setBusy(false);
    }
  }
  async function back() {
    const video = document.querySelector('video');
    if (selection && video && Number.isFinite(video.currentTime)) {
      video.pause();
      try {
        await selection.writer.save(video.currentTime, video.ended);
      } catch (e) {
        setError(errorMessage(e));
      }
    }
    ++loadVersion.current;
    setSelection(undefined);
    restoreList.current = true;
    await load();
  }
  async function signOut() {
    ++loadVersion.current;
    clearTimeout(searchTimer.current);
    setBusy(true);
    try {
      let progressWarning = '';
      const video = document.querySelector('video');
      if (selection && video && Number.isFinite(video.currentTime)) {
        video.pause();
        try {
          await selection.writer.save(video.currentTime, video.ended);
        } catch {
          progressWarning =
            'Your last playback position could not be saved. Earlier saved progress remains on the original server.';
        }
      }
      await logout();
      setError(progressWarning);
    } catch (e) {
      setError(
        (e && typeof e === 'object' && 'code' in e && e.code === 'VAULT'
          ? 'Saved credentials could not be removed. Remove this entry in the operating system credential store and revoke the session in the web interface. '
          : 'Local session cleared; remote revocation may not have completed. ') +
          errorMessage(e),
      );
    } finally {
      setUser(undefined);
      setSelection(undefined);
      setItems([]);
      setWatching([]);
      setContinueItems([]);
      setSessions([]);
      setConnected(false);
      setSearch('');
      setOffset(0);
      setView('library');
      setBusy(false);
    }
  }
  async function settings() {
    const requestVersion = loadVersion.current;
    setView('settings');
    setSelection(undefined);
    setError('');
    try {
      const result = await api.sessions();
      if (!Array.isArray(result)) throw new Error('Invalid session response.');
      if (requestVersion === loadVersion.current) setSessions(result);
    } catch (e) {
      if (requestVersion === loadVersion.current) failure(e);
    }
  }
  if (!connected)
    return (
      <main className="auth-shell">
        <section className="panel auth-form">
          <p className="eyebrow">MEDIA SERVER · WINDOWS</p>
          <h1>Connect your collection.</h1>
          <p className="muted">
            Connect to the same server from Windows, Linux or another host. The
            desktop application does not host your media.
          </p>
          {savedServers.length > 0 && (
            <section aria-label="Saved servers">
              <h2>Saved servers</h2>
              {savedServers.map((server) => (
                <div key={server.address}>
                  <p>
                    <strong>{server.name}</strong>
                    <br />
                    <span className="muted">{server.address}</span>
                  </p>
                  <button
                    type="button"
                    className="secondary"
                    disabled={busy}
                    onClick={() => {
                      setAddress(server.address);
                      setServerName(server.name);
                      setError('');
                    }}
                    aria-label={`Use ${server.name}`}
                  >
                    Use server
                  </button>{' '}
                  <button
                    type="button"
                    className="text-button"
                    disabled={busy}
                    aria-label={`Forget ${server.name}`}
                    onClick={async () => {
                      setBusy(true);
                      try {
                        await forgetSavedSession(server.address);
                        await savePreferences('', theme);
                        if (address === server.address) {
                          setAddress('https://');
                          setServerName('');
                        }
                        const remaining = savedServers.filter(
                          (s) => s.address !== server.address,
                        );
                        writeSavedServers(remaining);
                        setSavedServers(remaining);
                      } catch {
                        setError(
                          'Could not remove the saved server or its local credentials. Retry or clear the session through the operating system credential store.',
                        );
                      } finally {
                        setBusy(false);
                      }
                    }}
                  >
                    Forget
                  </button>
                </div>
              ))}
            </section>
          )}
          <form onSubmit={attach}>
            <label>
              Server name (optional)
              <input
                value={serverName}
                onChange={(e) => setServerName(e.target.value)}
                maxLength={80}
                disabled={busy}
                autoComplete="off"
              />
            </label>
            <label>
              Server address
              <input
                aria-label="Server address"
                disabled={busy}
                value={address}
                onChange={(e) => setAddress(e.target.value)}
                autoComplete="url"
                required
                maxLength={512}
              />
            </label>
            <label className="check">
              <input
                type="checkbox"
                disabled={busy || !persistentSessions}
                checked={remember}
                onChange={(e) => setRemember(e.target.checked)}
              />{' '}
              {persistentSessions
                ? 'Remember this session in Windows Credential Manager'
                : 'Sessions stay in memory on this platform; sign in after restarting'}
            </label>
            <p className="muted">
              HTTPS is required for remote servers. Local HTTP sends credentials
              without encryption and is allowed only on this computer.
            </p>
            <p className="muted">
              Successful connections save only the name and address on this
              device. Passwords are never saved.
            </p>
            <button disabled={busy}>{busy ? 'Connecting…' : 'Connect'}</button>
          </form>
          {error && (
            <p role="alert" className="error">
              {error}
            </p>
          )}
        </section>
      </main>
    );
  if (!user)
    return (
      <main className="auth-shell">
        <section className="panel auth-form">
          <p className="eyebrow">CONNECTED · API v1</p>
          <h1>Welcome back.</h1>
          <p className="muted">
            {address} · {version}
          </p>
          <form onSubmit={signIn}>
            <label>
              Username
              <input
                name="name"
                required
                autoComplete="username"
                maxLength={64}
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
                autoComplete="current-password"
              />
            </label>
            <button disabled={busy}>Sign in</button>
          </form>
          <button
            className="secondary"
            disabled={busy}
            onClick={() => void signOut()}
          >
            Change server
          </button>
          {error && (
            <p role="alert" className="error">
              {error}
            </p>
          )}
        </section>
      </main>
    );
  return (
    <div className="app-shell">
      <aside>
        <a
          className="brand"
          href="#"
          onClick={(e) => {
            e.preventDefault();
            void back();
            setView('library');
          }}
        >
          <span className="brand-icon">M</span> MEDIA SERVER
        </a>
        <p className="nav-label">WINDOWS CLIENT</p>
        <nav className="desktop-nav" aria-label="Main navigation">
          <button
            className="secondary"
            aria-current={!selection && view === 'library' ? 'page' : undefined}
            onClick={() => {
              void back();
              setView('library');
            }}
          >
            <Icon name="library" />
            <span>Library</span>
          </button>
          <button
            className="secondary"
            aria-current={
              !selection && view === 'settings' ? 'page' : undefined
            }
            onClick={() => void settings()}
          >
            <Icon name="settings" />
            <span>Settings</span>
          </button>
        </nav>
        <div className="sidebar-bottom">
          {online ? 'Connected' : 'Connection needs attention'}
          <p>{version} · Client 0.1.0-alpha.1</p>
          <button
            className="text-button"
            disabled={busy}
            onClick={() => void signOut()}
          >
            Change server
          </button>
          <button className="text-button" onClick={() => void signOut()}>
            <Icon name="logout" /> Sign out · {user.name}
          </button>
        </div>
      </aside>
      <div className="workspace">
        <header>
          <span className="muted">{address}</span>
          <button
            className="secondary"
            disabled={busy}
            onClick={() => void load()}
          >
            <Icon name="refresh" /> Refresh
          </button>
        </header>
        <main>
          {error && (
            <p role="alert" className="error">
              {error}
            </p>
          )}
          {busy && <p role="status">Loading…</p>}
          {selection ? (
            <>
              <button className="text-button" onClick={() => void back()}>
                <Icon name="back" /> Back to library
              </button>
              {selection.url && <h1>{selection.item.title}</h1>}
              {!selection.url && (
                <TitleDetails
                  item={selection.item}
                  headingRef={detailHeading}
                  resume={
                    selection.progress.completed
                      ? 0
                      : selection.progress.seconds
                  }
                  busy={busy}
                  onPlay={() => void beginPlayback(false)}
                  onResume={() => void beginPlayback(true)}
                />
              )}
              {selection.url && (
                <Player
                  startPlaying
                  key={selection.item.id}
                  item={selection.item}
                  resume={selection.startAt ?? 0}
                  progressRevision={selection.progress.revision}
                  streamUrl={selection.url}
                  playbackTransport={{
                    request: playbackRequest,
                    fileUrl: playbackFileUrl,
                    directUrl: selection.url,
                  }}
                  onSaveProgress={(seconds, completed) =>
                    selection.writer.save(seconds, completed)
                  }
                  onPlaybackError={async () => {
                    await api.report(
                      selection.item.id,
                      'unknown',
                      `${clientPlatform}-${playerEngine.toLowerCase()}`,
                    );
                    await api.me();
                  }}
                />
              )}
              <details className="panel">
                <summary>File and track details</summary>
                <ul>
                  {(Array.isArray(selection.tracks.tracks)
                    ? selection.tracks.tracks
                    : []
                  ).map((t, i) => (
                    <li key={i}>
                      {t.type} · {t.codec} · {t.language || 'Language unknown'}
                    </li>
                  ))}
                </ul>
              </details>
            </>
          ) : view === 'settings' ? (
            <>
              <h1>Application settings</h1>
              <section className="panel">
                <h2>Appearance</h2>
                <label>
                  Theme
                  <select
                    value={theme}
                    onChange={(e) => {
                      setTheme(e.target.value);
                      void savePreferences(address, e.target.value).catch(
                        failure,
                      );
                    }}
                  >
                    <option value="system">System</option>
                    <option value="dark">Dark</option>
                    <option value="light">Light</option>
                  </select>
                </label>
                <p className="muted">
                  Server: {address}. Passwords are never saved. Remembered
                  sessions use Windows Credential Manager. Linux/macOS sessions
                  are kept in memory only.
                </p>
                <button className="danger-ghost" onClick={() => void signOut()}>
                  <Icon name="logout" /> Disconnect and clear saved session
                </button>
              </section>
              <section className="panel">
                <h2>Active sessions</h2>
                {sessions.map((s) => (
                  <div className="library-row" key={s.id}>
                    <div>
                      <strong>{s.deviceName}</strong>
                      <p className="muted">
                        {s.current ? 'This session' : 'Other session'} · Expires{' '}
                        {new Date(s.expiresAt).toLocaleDateString()}
                      </p>
                    </div>
                    <button
                      disabled={s.current}
                      onClick={() =>
                        void api.revoke(s.id).then(settings).catch(failure)
                      }
                    >
                      Revoke
                    </button>
                  </div>
                ))}
              </section>
              <p className="muted">
                Updates are manual in this alpha. Playback support depends on
                your operating system and installed media codecs; native libmpv
                playback is planned.
              </p>
            </>
          ) : (
            <>
              <p className="eyebrow">YOUR MEDIA, ANY SCREEN</p>
              <h1>Your private screening room.</h1>
              <div className="desktop-library-tools">
                <label className="search-label">
                  Search library
                  <Icon name="search" />
                  <input
                    type="search"
                    value={search}
                    maxLength={200}
                    onChange={(e) => {
                      const query = e.target.value;
                      setSearch(query);
                      setOffset(0);
                      clearTimeout(searchTimer.current);
                      searchTimer.current = setTimeout(
                        () => void load(query, 0),
                        250,
                      );
                    }}
                    placeholder="Search filenames…"
                  />
                </label>
              </div>
              {!search && watching.length > 0 && (
                <section>
                  <div className="section-heading">
                    <h2>Continue Watching</h2>
                  </div>
                  <MediaCards list={continueItems} onOpen={open} />
                </section>
              )}
              <div className="section-heading">
                <h2>Library</h2>
                <span className="muted">{items.length} on this page</span>
              </div>
              {!items.length && !busy ? (
                <div className="empty-state">
                  <h2>No matching videos</h2>
                  <p className="muted">
                    Use the web administrator interface to configure and scan
                    libraries.
                  </p>
                </div>
              ) : (
                <MediaCards list={items} onOpen={open} />
              )}
              <div className="pagination">
                <button
                  disabled={offset === 0 || busy}
                  className="secondary"
                  onClick={() => {
                    const next = Math.max(0, offset - 200);
                    setOffset(next);
                    void load(search, next);
                  }}
                >
                  Previous
                </button>
                <button
                  disabled={items.length < 200 || busy}
                  className="secondary"
                  onClick={() => {
                    const next = offset + 200;
                    setOffset(next);
                    void load(search, next);
                  }}
                >
                  Next
                </button>
              </div>
            </>
          )}
        </main>
        <footer>Direct Play alpha · No telemetry · No cloud account</footer>
      </div>
    </div>
  );
}
