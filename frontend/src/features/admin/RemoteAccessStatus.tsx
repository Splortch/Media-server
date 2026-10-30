import { useEffect, useState } from 'react';
import { api } from '../../services/api';
type Readiness = {
  configured: boolean;
  requestIsHttps: boolean;
  secureCookies: boolean;
  trustedProxyCount: number;
  warnings: string[];
};
export default function RemoteAccessStatus() {
  const [state, setState] = useState<Readiness>();
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let active = true;
    api<Readiness>('/remote-readiness')
      .then((value) => {
        if (
          !value ||
          !Array.isArray(value.warnings) ||
          value.warnings.some((x) => typeof x !== 'string') ||
          typeof value.configured !== 'boolean'
        )
          throw new Error('Unsupported remote access status.');
        if (active) {
          setState(value);
          setError('');
        }
      })
      .catch((e) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
  }, [attempt]);
  return (
    <section className="panel" aria-label="Remote access status">
      <h2>Remote access</h2>
      {state && (
        <>
          <p>
            {state.configured
              ? 'HTTPS origin configured'
              : 'Local setup · remote access not configured'}
          </p>
          <p className="muted">
            Current request: {state.requestIsHttps ? 'HTTPS' : 'HTTP'} ·
            Secure-only cookies: {state.secureCookies ? 'on' : 'off'} · Explicit
            trusted proxies: {state.trustedProxyCount}
          </p>
          {state.warnings.length ? (
            <ul>
              {state.warnings.map((warning) => (
                <li key={warning}>{warning}</li>
              ))}
            </ul>
          ) : (
            <p>
              HTTPS and cookie configuration checks passed. This is not a
              security audit.
            </p>
          )}
        </>
      )}
      {error && <p role="alert">Remote access status unavailable: {error}</p>}
      {!state && !error && <p>Checking remote configuration…</p>}
      {error && (
        <button className="secondary" onClick={() => setAttempt((x) => x + 1)}>
          Retry remote status
        </button>
      )}
      <p className="muted">
        Configure a private VPN and trusted TLS proxy using the{' '}
        <a
          href="https://github.com/Splortch/Media-server/blob/main/docs/deployment/REMOTE-ACCESS.md"
          target="_blank"
          rel="noreferrer"
        >
          remote setup guide
        </a>
        . These checks do not open a public listener or configure your router.
      </p>
    </section>
  );
}
