import { useEffect, useRef, useState } from 'react';
import { api } from '../services/api';
import {
  loadFeatures,
  parseFeatures,
  type FeatureState,
} from '../services/features';

export default function Customize({
  state,
  admin,
  onChange,
}: {
  state: FeatureState;
  admin: boolean;
  onChange: (state: FeatureState) => void;
}) {
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  const [pendingHidden, setPendingHidden] = useState<string[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  async function save(path: string, method: string, body?: unknown) {
    setBusy(true);
    setError('');
    try {
      const response = parseFeatures(await api(path, method, body));
      if (mounted.current) onChange(response);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setPendingHidden(null);
      setBusy(false);
    }
  }
  return (
    <section
      className="panel"
      id="settings-customize"
      tabIndex={-1}
      aria-busy={busy}
    >
      <h2>Customize</h2>
      <p className="muted">
        Choose what appears for your account. Library, search, playback, resume
        and security controls always stay available. These preferences follow
        your account in supported clients.
      </p>
      {!state.available ? (
        <>
          <p role="status">
            Optional feature settings are unavailable on this server. Core
            playback still works.
          </p>
          <button
            className="secondary"
            disabled={busy}
            onClick={async () => {
              setBusy(true);
              setError('');
              try {
                const response = await loadFeatures();
                if (mounted.current) onChange(response);
              } catch (e) {
                setError((e as Error).message);
              } finally {
                setBusy(false);
              }
            }}
          >
            Retry customization
          </button>
        </>
      ) : (
        <>
          {state.features.map((feature) => (
            <div className="feature-option" key={feature.key}>
              <label className="check">
                <input
                  type="checkbox"
                  checked={
                    !(pendingHidden
                      ? pendingHidden.includes(feature.key)
                      : feature.hidden)
                  }
                  disabled={busy || !feature.enabled}
                  onChange={(event) => {
                    const hidden = new Set(
                      state.features.filter((f) => f.hidden).map((f) => f.key),
                    );
                    if (event.target.checked) hidden.delete(feature.key);
                    else hidden.add(feature.key);
                    setPendingHidden([...hidden]);
                    void save('/preferences/features', 'PUT', {
                      hidden: [...hidden],
                    });
                  }}
                />
                Show {feature.name}
              </label>
              {!feature.enabled && (
                <p className="muted">
                  Disabled by the administrator. Your personal preference is
                  retained.
                </p>
              )}
            </div>
          ))}
          <button
            className="secondary"
            disabled={busy}
            onClick={() => void save('/preferences/features', 'DELETE')}
          >
            Reset visibility to defaults
          </button>
          {admin && (
            <details className="feature-policy">
              <summary>Server-wide availability</summary>
              <p className="muted">
                Disabling a module blocks new use for everyone. Saved data is
                kept; re-enabling restores it. Personal hiding only changes your
                interface.
              </p>
              {state.features
                .filter((f) => f.serverConfigurable)
                .map((feature) => (
                  <div className="feature-option" key={feature.key}>
                    <span>
                      {feature.name}: {feature.enabled ? 'Enabled' : 'Disabled'}
                    </span>
                    <button
                      className="secondary"
                      disabled={busy}
                      onClick={() =>
                        void save('/features/' + feature.key, 'PUT', {
                          enabled: !feature.enabled,
                        })
                      }
                    >
                      {feature.enabled ? 'Disable' : 'Enable'} {feature.name}{' '}
                      for everyone
                    </button>
                  </div>
                ))}
            </details>
          )}
        </>
      )}
      {error && <p role="alert">{error}</p>}
    </section>
  );
}
