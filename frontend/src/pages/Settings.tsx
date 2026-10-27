import { useEffect, useState, type FormEvent } from 'react';
import { api, type Library } from '../services/api';
type ScanIssue = { fileName: string; code: string; correlationId: string };
type Job = { id: number; state: string; discovered: number; failed: number };
export default function Settings() {
  const [issues, setIssues] = useState<Record<number, ScanIssue[]>>({});
  const [passwordError, setPasswordError] = useState('');
  const [passwordBusy, setPasswordBusy] = useState(false);
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
          .then(async (x) => {
            setJobs((old) => ({ ...old, [j.id]: x }));
            if (!['queued', 'running'].includes(x.state)) {
              const failures = await api<ScanIssue[]>(`/scans/${j.id}/issues`);
              setIssues((old) => ({ ...old, [j.id]: failures }));
            }
          })
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
  async function changePassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const form = event.currentTarget;
    setPasswordBusy(true);
    setPasswordError('');
    try {
      await api('/auth/password', 'POST', {
        currentPassword: data.get('currentPassword'),
        newPassword: data.get('newPassword'),
      });
      form.reset();
      window.dispatchEvent(new Event('session-expired'));
    } catch (error) {
      setPasswordError((error as Error).message);
    } finally {
      setPasswordBusy(false);
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
        <form onSubmit={add} className="settings-form" aria-busy={busy}>
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
            <button
              className="secondary"
              onClick={() => void scan(l.id)}
              aria-label={`Scan library ${l.name}`}
            >
              Scan library
            </button>
          </div>
        ))}
        {Object.values(jobs).map((j) => (
          <div key={j.id}>
            <p role="status">
              Scan #{j.id}: {j.state} · {j.discovered} identified · {j.failed}{' '}
              failed
            </p>
            {!!issues[j.id]?.length && (
              <details>
                <summary>
                  Scan #{j.id} problems ({issues[j.id].length}; up to 100 shown)
                </summary>
                <ul>
                  {issues[j.id].map((issue) => (
                    <li key={issue.correlationId}>
                      {issue.fileName}: {scanReason(issue.code)} · Reference{' '}
                      {issue.correlationId}
                    </li>
                  ))}
                </ul>
              </details>
            )}
          </div>
        ))}
      </section>
      <section className="panel">
        <h2>Change password</h2>
        <p className="muted">
          Changing your password signs out every device, including this one.
          Sign in again with your new password.
        </p>
        <form
          className="settings-form"
          onSubmit={(e) => void changePassword(e)}
          aria-busy={passwordBusy}
        >
          <label>
            Current password
            <input
              name="currentPassword"
              type="password"
              autoComplete="current-password"
              required
              maxLength={256}
            />
          </label>
          <label>
            New password
            <input
              name="newPassword"
              type="password"
              autoComplete="new-password"
              required
              minLength={12}
              maxLength={256}
              aria-describedby="password-help"
            />
          </label>
          <p id="password-help" className="muted">
            Use 12–256 characters.
          </p>
          {passwordError && (
            <p role="alert" className="error">
              {passwordError}
            </p>
          )}
          <button disabled={passwordBusy}>
            {passwordBusy
              ? 'Changing password…'
              : 'Change password and sign out'}
          </button>
        </form>
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

function scanReason(code: string) {
  return (
    (
      {
        probe_unavailable: 'FFprobe is not installed or could not start.',
        probe_timeout: 'Identification timed out.',
        invalid_media: 'FFprobe could not read this media file.',
        no_video: 'No video track was found.',
        file_missing: 'The file or library is unavailable.',
        access_denied: 'The server cannot read this file.',
        unsafe_path: 'A symbolic link or unsafe path was rejected.',
        probe_failed:
          'Media identification failed. Check the server log using this reference.',
      } as Record<string, string>
    )[code] || 'Media identification failed.'
  );
}
