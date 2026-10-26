import { useEffect, useState, type FormEvent } from 'react';
import { api, type Library } from '../services/api';
type Job = { id: number; state: string; discovered: number; failed: number };
export default function Settings() {
  const [libraries, setLibraries] = useState<Library[]>([]);
  const [name, setName] = useState('');
  const [path, setPath] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [jobs, setJobs] = useState<Record<number, Job>>({});
  useEffect(() => {
    let active = true;
    api<Library[]>('/libraries')
      .then((x) => {
        if (active) setLibraries(x);
      })
      .catch((e) => {
        if (active) setError(String(e.message));
      });
    return () => {
      active = false;
    };
  }, []);
  useEffect(() => {
    const pending = Object.values(jobs).filter((j) =>
      ['queued', 'running'].includes(j.state),
    );
    if (!pending.length) return;
    const t = setTimeout(() => {
      for (const j of pending)
        api<Job>(`/scans/${j.id}`)
          .then((x) => setJobs((old) => ({ ...old, [j.id]: x })))
          .catch((e) => setError(String(e.message)));
    }, 1500);
    return () => clearTimeout(t);
  }, [jobs]);
  async function add(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      const library = await api<Library>('/libraries', 'POST', { name, path });
      setLibraries((old) => [...old, library]);
      setName('');
      setPath('');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function scan(id: number) {
    setError('');
    try {
      const job = await api<{ id: number }>(`/libraries/${id}/scan`, 'POST');
      setJobs((old) => ({
        ...old,
        [job.id]: { ...job, state: 'queued', discovered: 0, failed: 0 },
      }));
    } catch (e) {
      setError((e as Error).message);
    }
  }
  return (
    <>
      <p className="eyebrow">MAKE YOURSELF AT HOME</p>
      <h1>Server settings</h1>
      <p className="muted">
        Your files stay yours. Scanning reads media and never changes the
        originals.
      </p>
      <section className="panel">
        <h2>Add a media directory</h2>
        <p className="muted">
          Use an absolute directory inside the allowed roots configured by the
          server operator.
        </p>
        <form onSubmit={add} className="settings-form">
          <label>
            Library name
            <input
              required
              maxLength={100}
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Movies"
            />
          </label>
          <label>
            Directory on the server
            <input
              required
              value={path}
              onChange={(e) => setPath(e.target.value)}
              placeholder="/media/movies or D:\Movies"
            />
          </label>
          <button disabled={busy}>{busy ? 'Adding…' : 'Add library'}</button>
        </form>
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}
      </section>
      <section className="panel">
        <h2>Media libraries</h2>
        {!libraries.length && (
          <p className="muted">
            No directories configured. Add your first library above.
          </p>
        )}
        {libraries.map((l) => (
          <div className="library-row" key={l.id}>
            <div>
              <strong>{l.name}</strong>
              <p className="muted path">{l.path}</p>
            </div>
            <button className="secondary" onClick={() => void scan(l.id)}>
              Scan library
            </button>
          </div>
        ))}
        {Object.values(jobs).map((j) => (
          <p role="status" key={j.id}>
            Scan #{j.id}: {j.state} · {j.discovered} identified · {j.failed}{' '}
            failed
          </p>
        ))}
      </section>
      <section className="panel">
        <h2>Alpha capabilities</h2>
        <p className="muted">
          Direct Play · Local administrator account · SQLite · No telemetry
        </p>
        <p className="muted">
          Transcoding, metadata artwork, multiple users and remote access are
          planned.
        </p>
      </section>
    </>
  );
}
