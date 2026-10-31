import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from 'react';
import { api } from '../../services/api';
import { loadFeatures, type FeatureState } from '../../services/features';
import type { components } from '../../../../shared/api/src/generated';
type Job = components['schemas']['DownloadView'];
type Settings = components['schemas']['DownloadSettingsView'];
const mib = 1024 * 1024;
const bytes = (value: number) =>
  value >= mib * 1024
    ? `${(value / mib / 1024).toFixed(1)} GiB`
    : `${(value / mib).toFixed(1)} MiB`;
export function DownloadSettings({
  onChange,
}: {
  onChange: (state: FeatureState) => void;
}) {
  const [settings, setSettings] = useState<Settings>();
  const [hosts, setHosts] = useState('');
  const [enabled, setEnabled] = useState(false);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    let active = true;
    void api<Settings>('/downloads/settings')
      .then((value) => {
        if (!active) return;
        setSettings(value);
        setHosts(value.hosts.join('\n'));
        setEnabled(value.enabled);
      })
      .catch((e) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
  }, []);
  async function save(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError('');
    try {
      await api('/downloads/settings', 'PUT', {
        enabled,
        hosts: hosts.split(/\s+/).filter(Boolean),
      });
      setSettings(await api<Settings>('/downloads/settings'));
      onChange(await loadFeatures());
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <section
      id="settings-downloads"
      className="panel settings-target"
      tabIndex={-1}
    >
      <h2>Download center</h2>
      <p className="muted">
        Optional administrator tool for authorized public HTTPS URLs. Files stay
        in separate staging storage. Library import, family approvals, catalogs
        and external clients are planned.
      </p>
      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
      {settings && (
        <>
          <p>
            {bytes(settings.availableBytes)} available ·{' '}
            {bytes(settings.reservedBytes)} reserved ·{' '}
            {bytes(settings.safetyMarginBytes)} safety margin. Staging limit:{' '}
            {bytes(settings.stagingLimitBytes)}.
          </p>
          <form onSubmit={save} className="settings-form" aria-busy={busy}>
            <label>
              <input
                type="checkbox"
                checked={enabled}
                onChange={(e) => setEnabled(e.target.checked)}
              />{' '}
              Enable download center
            </label>
            <label>
              Approved source hosts
              <textarea
                value={hosts}
                onChange={(e) => setHosts(e.target.value)}
                placeholder="media.example.org"
                maxLength={5100}
                rows={3}
              />
            </label>
            <p className="muted">
              One exact public hostname per line, including redirect hosts.
              HTTPS port 443 only. Private networks and certificate bypasses are
              never allowed. Saving pauses active and queued jobs; resume them
              explicitly.
            </p>
            <button disabled={busy}>
              {busy ? 'Saving…' : 'Save download settings'}
            </button>
          </form>
          {settings.enabled && (
            <a className="button secondary" href="#downloads">
              Open download center
            </a>
          )}
        </>
      )}
    </section>
  );
}
export default function Downloads() {
  const [jobs, setJobs] = useState<Job[]>([]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const busyRef = useRef(false);
  const active = useRef(false);
  const sequence = useRef(0);
  const refresh = useCallback(async () => {
    const generation = ++sequence.current;
    const rows = await api<Job[]>('/downloads');
    if (active.current && generation === sequence.current) {
      setJobs(rows);
      setLoaded(true);
    }
  }, []);
  useEffect(() => {
    active.current = true;
    const poll = () => {
      if (!busyRef.current)
        void refresh().catch((e) => {
          if (active.current) setError(e.message);
        });
    };
    poll();
    const timer = window.setInterval(poll, 1500);
    return () => {
      active.current = false;
      window.clearInterval(timer);
    };
  }, [refresh]);
  async function mutate(operation: () => Promise<unknown>) {
    if (busyRef.current) return;
    busyRef.current = true;
    sequence.current++;
    setBusy(true);
    setError('');
    try {
      await operation();
      await refresh();
    } catch (e) {
      if (active.current) setError((e as Error).message);
    } finally {
      busyRef.current = false;
      if (active.current) setBusy(false);
    }
  }
  function action(job: Job, operation: string, priority?: number) {
    void mutate(() =>
      api(`/downloads/${job.id}`, 'PUT', {
        action: operation,
        expectedRevision: job.revision,
        priority,
      }),
    );
  }
  function create(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const input = new FormData(form);
    void mutate(async () => {
      await api('/downloads', 'POST', {
        name: input.get('name'),
        url: input.get('url'),
        budgetBytes: Math.floor(Number(input.get('budget')) * mib),
        sha256: input.get('checksum') || null,
        priority: Number(input.get('priority')),
      });
      form.reset();
    });
  }
  return (
    <>
      <p className="eyebrow">YOUR SOURCES. YOUR CONTROL.</p>
      <h1>Download center</h1>
      <p className="muted">
        Download only content you are authorized to obtain. Completed files are
        staged for administrator retrieval; they do not appear in your library
        automatically.
      </p>
      <a href="#settings" className="back-link">
        Download settings
      </a>
      {error && (
        <p role="alert" className="error">
          {error} Refresh the page if settings changed.
        </p>
      )}
      <section className="panel">
        <h2>Add an authorized URL</h2>
        <form className="download-form" onSubmit={create} aria-busy={busy}>
          <label>
            Download name
            <input name="name" required maxLength={80} />
          </label>
          <label>
            HTTPS source URL
            <input
              name="url"
              type="password"
              autoComplete="off"
              required
              maxLength={4096}
              placeholder="https://approved-host/video.mp4"
            />
          </label>
          <label>
            Maximum size (MiB)
            <input
              name="budget"
              type="number"
              required
              min={1}
              max={102400}
              defaultValue={1024}
            />
          </label>
          <label>
            Priority
            <input
              name="priority"
              type="number"
              min={-10}
              max={10}
              defaultValue={0}
              required
            />
          </label>
          <label>
            SHA-256 checksum (optional)
            <input
              name="checksum"
              pattern="[a-fA-F0-9]{64}"
              maxLength={64}
              autoComplete="off"
            />
          </label>
          <p className="muted">
            Higher priority runs first after the current transfer. The size
            budget reserves space before starting. URL tokens are encrypted on
            the server.
          </p>
          <button disabled={busy || !loaded}>
            {busy ? 'Working…' : 'Add download'}
          </button>
        </form>
      </section>
      <section aria-label="Download queue">
        <h2>Queue</h2>
        {!loaded ? (
          <p role="status">Loading downloads…</p>
        ) : (
          !jobs.length && <p>No downloads yet.</p>
        )}
        <div className="download-queue">
          {jobs.map((job) => (
            <article className="panel download-job" key={job.id}>
              <h3>{job.name}</h3>
              <p className="muted">
                {job.sourceHost} ·{' '}
                {job.state === 'running' && job.totalBytes === job.receivedBytes
                  ? 'verifying'
                  : job.state}{' '}
                · priority {job.priority}
              </p>
              <progress
                aria-label={`${job.name} download progress`}
                max={job.totalBytes || job.budgetBytes}
                value={job.receivedBytes}
              />
              <p>
                {bytes(job.receivedBytes)} /{' '}
                {job.totalBytes === null
                  ? `${bytes(job.budgetBytes)} budget (size unknown)`
                  : bytes(job.totalBytes)}
              </p>
              {job.error && (
                <p className="muted" role="status">
                  {job.error.replaceAll('-', ' ')}
                </p>
              )}
              {job.contentHash && (
                <details>
                  <summary>Staged SHA-256</summary>
                  <code className="download-hash">{job.contentHash}</code>
                </details>
              )}
              <div className="download-actions">
                {['queued', 'running'].includes(job.state) && (
                  <button
                    className="secondary"
                    disabled={busy || job.desired !== 'run'}
                    onClick={() => action(job, 'pause')}
                  >
                    Pause {job.name}
                  </button>
                )}
                {['paused', 'failed'].includes(job.state) && (
                  <>
                    {job.canResume && (
                      <button
                        disabled={busy}
                        onClick={() => action(job, 'resume')}
                      >
                        Resume {job.name}
                      </button>
                    )}
                    <button
                      className="secondary"
                      disabled={busy}
                      onClick={() => action(job, 'restart')}
                    >
                      Discard partial &amp; restart {job.name}
                    </button>
                  </>
                )}
                {['queued', 'paused', 'failed'].includes(job.state) && (
                  <label>
                    Priority for {job.name}
                    <input
                      type="number"
                      min={-10}
                      max={10}
                      defaultValue={job.priority}
                      key={`${job.id}-${job.priority}`}
                      onBlur={(e) => {
                        const n = e.currentTarget.valueAsNumber;
                        if (
                          Number.isInteger(n) &&
                          n !== job.priority &&
                          n >= -10 &&
                          n <= 10
                        )
                          action(job, 'priority', n);
                      }}
                      disabled={busy}
                    />
                  </label>
                )}
                {!['completed', 'cancelled', 'finalizing'].includes(
                  job.state,
                ) && (
                  <button
                    className="secondary"
                    disabled={busy || job.desired === 'cancel'}
                    onClick={() => action(job, 'cancel')}
                  >
                    Cancel &amp; discard {job.name}
                  </button>
                )}
                {job.state === 'completed' && (
                  <a
                    className="button"
                    href={`/api/downloads/${job.id}/file`}
                    download
                  >
                    Save {job.name}
                  </a>
                )}
                {['completed', 'cancelled', 'paused', 'failed'].includes(
                  job.state,
                ) && (
                  <button
                    className="secondary"
                    disabled={busy}
                    onClick={() =>
                      void mutate(() =>
                        api(`/downloads/${job.id}/${job.revision}`, 'DELETE'),
                      )
                    }
                  >
                    Remove {job.name} &amp; staged file
                  </button>
                )}
              </div>
            </article>
          ))}
        </div>
      </section>
    </>
  );
}
