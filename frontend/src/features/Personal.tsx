import { useEffect, useState, type FormEvent } from 'react';
import { api, duration } from '../services/api';
export type Collection = {
  id: number;
  name: string;
  prefix: string;
  libraryId: number | null;
  videoCodec: string | null;
};
export function Collections() {
  const [values, setValues] = useState<Collection[]>([]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    void api<Collection[]>('/collections')
      .then(setValues)
      .catch((e) => setError(e.message));
  }, []);
  async function create(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = e.currentTarget,
      data = new FormData(form);
    setBusy(true);
    setError('');
    try {
      const collection = await api<Collection>('/collections', 'POST', {
        name: data.get('name'),
        prefix: data.get('prefix') || '',
        videoCodec: data.get('codec') || null,
      });
      setValues((old) => [...old, collection]);
      form.reset();
    } catch (error) {
      setError((error as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="panel">
      <h2>Smart collections</h2>
      <p className="muted">
        Save title-prefix and codec rules. Collections always respect your
        current library and rating permissions.
      </p>
      <form
        className="settings-form"
        onSubmit={(e) => void create(e)}
        aria-busy={busy}
      >
        <label>
          Collection name
          <input name="name" required maxLength={80} />
        </label>
        <label>
          Title starts with
          <input name="prefix" maxLength={128} />
        </label>
        <label>
          Video codec
          <select aria-label="Video codec" name="codec">
            <option value="">Any codec</option>
            <option value="h264">H.264</option>
            <option value="vp9">VP9</option>
            <option value="av1">AV1</option>
            <option value="hevc">HEVC</option>
          </select>
        </label>
        <button disabled={busy}>Save smart collection</button>
      </form>
      {error && <p role="alert">{error}</p>}
      <ul>
        {values.map((value) => (
          <li key={value.id}>
            {value.name} · {value.prefix || 'Any title'} ·{' '}
            {value.videoCodec || 'Any codec'}{' '}
            <button
              className="secondary"
              onClick={() => {
                void api(`/collections/${value.id}`, 'DELETE')
                  .then(() =>
                    setValues((old) => old.filter((x) => x.id !== value.id)),
                  )
                  .catch((e) => setError(e.message));
              }}
            >
              Delete collection {value.name}
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}
type Session = {
  id: string;
  deviceName: string;
  createdAt: string;
  expiresAt: string;
  current: boolean;
};
export function Sessions() {
  const [values, setValues] = useState<Session[]>([]);
  const [error, setError] = useState('');
  useEffect(() => {
    void api<Session[]>('/v1/sessions')
      .then(setValues)
      .catch((e) => setError(e.message));
  }, []);
  return (
    <section className="panel">
      <h2>Your devices and sessions</h2>
      {error && <p role="alert">{error}</p>}
      <ul>
        {values.map((session) => (
          <li key={session.id}>
            {session.deviceName}
            {session.current ? ' (this session)' : ''} · Expires{' '}
            {new Date(session.expiresAt).toLocaleString()}{' '}
            <button
              className="secondary"
              onClick={() => {
                void api(`/v1/sessions/${session.id}`, 'DELETE')
                  .then(() => {
                    setValues((old) => old.filter((x) => x.id !== session.id));
                    if (session.current)
                      window.dispatchEvent(new Event('session-expired'));
                  })
                  .catch((e) => setError(e.message));
              }}
            >
              Sign out {session.deviceName}
              {session.current ? ' (this session)' : ''}
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}
type HistoryEntry = {
  mediaItemId: number;
  title: string;
  seconds: number;
  lastPlayedAt: string;
  completedAt: string | null;
};
export function History() {
  const [values, setValues] = useState<HistoryEntry[]>([]);
  const [error, setError] = useState('');
  useEffect(() => {
    void api<HistoryEntry[]>('/history')
      .then(setValues)
      .catch((e) => setError(e.message));
  }, []);
  return (
    <section>
      <h1>Watch history</h1>
      <p className="muted">
        Your last 200 watched titles. History is stored separately from resume
        positions.
      </p>
      {error && <p role="alert">{error}</p>}
      <ul>
        {values.map((item) => (
          <li key={item.mediaItemId}>
            <a className="back-link" href={`#media/${item.mediaItemId}`}>
              {item.title}
            </a>
            {duration(item.seconds)} ·{' '}
            {new Date(item.lastPlayedAt).toLocaleString()}
            {item.completedAt ? ' · Previously completed' : ''}
          </li>
        ))}
      </ul>
    </section>
  );
}
