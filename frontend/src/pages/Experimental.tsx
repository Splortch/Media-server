import { useEffect, useState } from 'react';
import { api, type Library } from '../services/api';

type Policy = { enabled: boolean; libraries: number[] };
export default function Experimental({ admin }: { admin: boolean }) {
  const [policy, setPolicy] = useState<Policy>();
  const [libraries, setLibraries] = useState<Library[]>([]);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (!admin) return;
    let active = true;
    Promise.all([
      api<Policy>('/experiments/guests'),
      api<Library[]>('/libraries'),
    ])
      .then(([value, roots]) => {
        if (active) {
          setPolicy(value);
          setLibraries(roots);
        }
      })
      .catch((e) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
  }, [admin]);
  if (!admin)
    return (
      <>
        <h1>Experimental</h1>
        <p>Only administrators can change experimental settings.</p>
      </>
    );
  async function save() {
    if (!policy) return;
    setBusy(true);
    setError('');
    setMessage('');
    try {
      const result = await api<Policy>('/experiments/guests', 'PUT', policy);
      setPolicy(result);
      setMessage(
        'Guest policy saved. Previous guest sessions have been revoked.',
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      <p className="eyebrow">TRY SOMETHING NEW</p>
      <h1>Experimental</h1>
      <p className="notice">
        Early features may change. Every experiment is opt-in and disabled by
        default. Review access carefully before enabling it.
      </p>
      <section className="panel">
        <h2>Guest access</h2>
        <p>
          Anyone who can reach this server can browse and Direct Play the
          selected libraries without a password. Use only on a trusted network.
          Guest sessions last four hours, with no saved progress, history,
          collections, or administration access.
        </p>
        <p className="muted">
          Saving revokes existing guest sessions. Disabling prevents new media
          requests; video data already delivered cannot be recalled. Remote
          access still requires trusted HTTPS.
        </p>
        {policy && (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              void save();
            }}
            aria-busy={busy}
          >
            <fieldset disabled={busy}>
              <legend>Guest permissions</legend>
              <label>
                <input
                  type="checkbox"
                  checked={policy.enabled}
                  onChange={(e) =>
                    setPolicy({ ...policy, enabled: e.target.checked })
                  }
                />{' '}
                Enable guest access
              </label>
              <p>
                Select libraries to share. New libraries are never added
                automatically.
              </p>
              {libraries.map((library) => (
                <label key={library.id}>
                  <input
                    type="checkbox"
                    checked={policy.libraries.includes(library.id)}
                    onChange={(e) =>
                      setPolicy({
                        ...policy,
                        libraries: e.target.checked
                          ? [...policy.libraries, library.id]
                          : policy.libraries.filter((id) => id !== library.id),
                      })
                    }
                  />{' '}
                  {library.name}
                </label>
              ))}
              {!libraries.length && (
                <p>Add a media library in Settings first.</p>
              )}
              <button disabled={policy.enabled && !policy.libraries.length}>
                Save guest policy
              </button>
            </fieldset>
          </form>
        )}
        {!policy && !error && (
          <p role="status">Loading experimental settings…</p>
        )}
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}
        {message && <p role="status">{message}</p>}
      </section>
    </>
  );
}
