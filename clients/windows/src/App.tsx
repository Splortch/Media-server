import { useEffect, useRef, useState, type FormEvent } from 'react';
import {
  api,
  connect,
  login,
  logout,
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
import Player from '../../../frontend/src/components/Player';
import { duration } from '../../../frontend/src/services/api';
type Selection = {
  item: Detail;
  progress: ProgressState;
  url: string;
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
          onClick={() => void onOpen(m)}
        >
          <div className="cover">
            <span className="monogram" aria-hidden="true">
              {m.title[0]}
            </span>
            <span className="play-symbol" aria-hidden="true">
              ▶
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
  const [remember, setRemember] = useState(false);
  const [persistentSessions, setPersistentSessions] = useState(false);
  useEffect(() => {
    void desktopCapabilities()
      .then((c) => setPersistentSessions(c.persistentSessions))
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
      const info = await connect(address, remember);
      if (info.setupRequired)
        throw new Error(
          'This server needs initial setup. Open its web interface in your browser first.',
        );
      setConnected(true);
      setVersion(info.serverVersion);
      setOnline(true);
      await savePreferences(address, theme);
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
      await api.capabilities(supported, []);
    } catch (e) {
      failure(e);
    } finally {
      setBusy(false);
    }
  }
  async function open(item: Media | Detail) {
    const requestVersion = loadVersion.current;
    setBusy(true);
    setError('');
    try {
      const [detail, progress, url, tracks] = await Promise.all([
        api.detail(item.id),
        api.progress(item.id),
        api.transport.streamUrl(item.id),
        api.tracks(item.id),
      ]);
      if (requestVersion !== loadVersion.current) return;
      setSelection({
        item: detail,
        progress,
        url,
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
  asyn…29112 tokens truncated…0, ['MediaServer-v0.2.0-alpha.10-win-x64-setup.exe', 'MediaServer-v0.2.0-alpha.10-win-x64.zip']),
    release('server-linux', 10, ['MediaServer-v0.2.0-alpha.10-linux-x64.tar.gz']),
    release('docker', 10, ['MediaServer-Docker-v0.2.0-alpha.10.zip']),
    release('client-windows', 10, ['MediaServer-Windows-Client-v0.1.0-alpha.10-win-x64-setup.exe', 'MediaServer-Windows-Client-v0.1.0-alpha.10-win-x64.msi']),
    release('client-android', 17, ['MediaServer-Android-v0.1.0-alpha.17-debug.apk']),
    release('server-macos', 10, ['MediaServer-v0.2.0-alpha.10-osx-arm64.pkg', 'MediaServer-v0.2.0-alpha.10-osx-x64.pkg']),
    release('client-linux', 10, ['MediaServer-Desktop-Client-v0.1.0-alpha.10-linux-x64.deb']),
    release('client-macos', 10, ['MediaServer-Desktop-Client-v0.1.0-alpha.10-osx-arm64.dmg', 'MediaServer-Desktop-Client-v0.1.0-alpha.10-osx-x64.dmg']),
    release('client-android', 7, ['MediaServer-Android-v0.1.0-alpha.7-debug.apk']),
  ];
}
module.exports = {fixtures, release};
