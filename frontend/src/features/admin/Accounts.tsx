import { useEffect, useState, type FormEvent } from 'react';
import { api, type Library } from '../../services/api';
type Account = {
  id: number;
  name: string;
  role: string;
  disabled: boolean;
  allLibraries: boolean;
  maximumRating: number | null;
  libraries: number[];
};
export default function Accounts() {
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [libraries, setLibraries] = useState<Library[]>([]);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  async function load() {
    const [users, roots] = await Promise.all([
      api<Account[]>('/users'),
      api<Library[]>('/libraries'),
    ]);
    setAccounts(users);
    setLibraries(roots);
  }
  useEffect(() => {
    let active = true;
    Promise.all([api<Account[]>('/users'), api<Library[]>('/libraries')])
      .then(([users, roots]) => {
        if (active) {
          setAccounts(users);
          setLibraries(roots);
        }
      })
      .catch((e) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
  }, []);
  async function create(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget,
      data = new FormData(form);
    setBusy(true);
    setError('');
    try {
      await api('/users', 'POST', {
        name: data.get('name'),
        password: data.get('password'),
        role: 'Viewer',
        allLibraries: false,
        libraries: [],
      });
      form.reset();
      await load();
      setMessage(
        'Account created with no library access. Set its permissions below.',
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function save(account: Account) {
    setBusy(true);
    setError('');
    try {
      await api(`/users/${account.id}`, 'PUT', {
        role: account.role,
        disabled: account.disabled,
        allLibraries: account.allLibraries,
        maximumRating: account.maximumRating,
        libraries: account.libraries,
      });
      setMessage(
        `${account.name}: permissions saved; existing sessions were revoked.`,
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const update = (id: number, values: Partial<Account>) =>
    setAccounts((old) =>
      old.map((a) => (a.id === id ? { ...a, ...values } : a)),
    );
  return (
    <section className="panel">
      <h2>Accounts and permissions</h2>
      <p className="muted">
        Administrators have full access. Viewer restrictions apply to library
        grants and ratings. Restricted accounts cannot see unrated media.
        Classify media as General (10), Teen (13), Mature (16) or Adult (18)
        before granting a rating limit.
      </p>
      <form
        className="settings-form"
        onSubmit={(e) => void create(e)}
        aria-busy={busy}
      >
        <label>
          New username
          <input
            name="name"
            required
            maxLength={64}
            pattern="[A-Za-z0-9_.-]+"
            autoComplete="off"
          />
        </label>
        <label>
          Temporary password
          <input
            name="password"
            type="password"
            required
            minLength={12}
            maxLength={256}
            autoComplete="new-password"
          />
        </label>
        <button disabled={busy}>Create viewer account</button>
      </form>
      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
      {message && <p role="status">{message}</p>}
      {accounts.map((account) => (
        <details key={account.id}>
          <summary>
            {account.name} · {account.role}
            {account.disabled ? ' · Disabled' : ''}
          </summary>
          <div className="settings-form">
            <label>
              Role for {account.name}
              <select
                disabled={account.id === 1 || busy}
                value={account.role}
                onChange={(e) => update(account.id, { role: e.target.value })}
              >
                <option>Viewer</option>
                <option>Admin</option>
              </select>
            </label>
            <label>
              <input
                type="checkbox"
                disabled={account.id === 1 || busy}
                checked={account.disabled}
                onChange={(e) =>
                  update(account.id, { disabled: e.target.checked })
                }
              />
              Disable {account.name}
            </label>
            <label>
              <input
                type="checkbox"
                checked={account.allLibraries}
                onChange={(e) =>
                  update(account.id, { allLibraries: e.target.checked })
                }
              />
              All libraries for {account.name}
            </label>
            <fieldset>
              <legend>Allowed libraries for {account.name}</legend>
              {libraries.map((l) => (
                <label key={l.id}>
                  <input
                    type="checkbox"
                    disabled={account.allLibraries}
                    checked={account.libraries.includes(l.id)}
                    onChange={(e) =>
                      update(account.id, {
                        libraries: e.target.checked
                          ? [...account.libraries, l.id]
                          : account.libraries.filter((id) => id !== l.id),
                      })
                    }
                  />
                  {l.name}
                </label>
              ))}
            </fieldset>
            <label>
              Maximum rating for {account.name}
              <select
                value={account.maximumRating ?? ''}
                onChange={(e) =>
                  update(account.id, {
                    maximumRating: e.target.value
                      ? Number(e.target.value)
                      : null,
                  })
                }
              >
                <option value="">Unrestricted</option>
                <option value="10">General (10)</option>
                <option value="13">Teen (13)</option>
                <option value="16">Mature (16)</option>
                <option value="18">Adult (18)</option>
              </select>
            </label>
            <button disabled={busy} onClick={() => void save(account)}>
              Save permissions for {account.name}
            </button>
            <ResetPassword account={account} />
            <button
              className="secondary"
              onClick={() => {
                void api(`/users/${account.id}/sessions`, 'DELETE')
                  .then(() =>
                    setMessage(`${account.name}: all sessions revoked.`),
                  )
                  .catch((e) => setError(e.message));
              }}
            >
              Sign out all devices for {account.name}
            </button>
          </div>
        </details>
      ))}
    </section>
  );
}
function ResetPassword({ account }: { account: Account }) {
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  async function reset(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = e.currentTarget,
      data = new FormData(form);
    setBusy(true);
    setError('');
    try {
      await api(`/users/${account.id}/password`, 'POST', {
        password: data.get('password'),
      });
      form.reset();
      setDone(true);
    } catch (error) {
      setError((error as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <form onSubmit={(e) => void reset(e)}>
      <label>
        Reset password for {account.name}
        <input
          name="password"
          type="password"
          autoComplete="new-password"
          minLength={12}
          maxLength={256}
          required
        />
      </label>
      <button disabled={busy}>Reset password for {account.name}</button>
      {error && <p role="alert">{error}</p>}
      {done && <p role="status">Password reset; sessions revoked.</p>}
    </form>
  );
}
