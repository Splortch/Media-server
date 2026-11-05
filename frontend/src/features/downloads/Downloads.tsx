import ImportForm, {
  ImportFields,
  importPlan,
  useLibraries,
} from './ImportForm';
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
function inCategory(job: Job, filter: string) {
  if (filter === 'paused') return ['paused', 'failed'].includes(job.state);
  if (filter === 'imported') return job.import?.state === 'imported';
  if (filter === 'review')
    return (
      job.state === 'completed' &&
      (!job.import || ['inbox', 'held', 'discarded'].includes(job.import.state))
    );
  if (filter === 'active')
    return (
      ['queued', 'running', 'finalizing'].includes(job.state) ||
      ['waiting', 'queued', 'copying', 'scanning'].includes(
        job.import?.state ?? '',
      )
    );
  return true;
}
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
        Optional administrator tool for authorized public HTTPS URLs. Choose
        automatic library delivery when adding a download, or keep files in
        separate staging storage until an administrator approves a library
        match. Family approvals and managed Movies/TV imports are available;
        catalogs and external clients are planned.
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
export default function Downloads({ admin = false }: { admin?: boolean }) {
  return admin ? (
    <AdminDownloads />
  ) : (
    <p>You need an administrator account to open the download center.</p>
  );
}
function AdminDownloads() {
  const { libraries, error: libraryError } = useLibraries();
  const [delivery, setDelivery] = useState('staging');
  const [downloadName, setDownloadName] = useState('');
  const [filter, setFilter] = useState('all');
  const [transferClient, setTransferClient] = useState('builtin');
  const [externalAvailable, setExternalAvailable] = useState(false);
  useEffect(() => {
    let active = true;
    void api<{ id: string; configured: boolean }[]>('/downloads/clients')
      .then((rows) => {
        if (active && Array.isArray(rows))
          setExternalAvailable(
            rows.some((row) => row.id === 'aria2' && row.configured),
          );
      })
      .catch(() => {});
    return () => {
      active = false;
    };
  }, []);
  const [search, setSearch] = useState('');
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
        transferClient,
        priority: Number(input.get('priority')),
        ...(delivery === 'library' ? { importPlan: importPlan(input) } : {}),
      });
      form.reset();
      setDelivery('staging');
      setDownloadName('');
    });
  }
  const matches = (job: Job) =>
    inCategory(job, filter) &&
    [
      job.name,
      job.import?.title,
      libraries.find((library) => library.id === job.import?.libraryId)?.name,
    ].some((value) =>
      value?.toLowerCase().includes(search.trim().toLowerCase()),
    );
  return (
    <>
      <p className="eyebrow">ADMINISTRATION · DOWNLOADS</p>
      <h1>Download center</h1>
      <p className="muted">
        Download only content you are authorized to obtain. Choose a destination
        library to organize and scan completed media automatically, or keep
        files in staging and confirm a library match later.
      </p>
      <a href="#settings/downloads" className="back-link">
        Download settings
      </a>
      {error && (
        <p role="alert" className="error">
          {error} Refresh the page if settings changed.
        </p>
      )}
      <section className="panel">
        <h2>Library delivery</h2>
        <p>
          Choose a library when adding a URL to download, organize and scan the
          title in one step. Completed staged files can be matched from the
          queue.
        </p>
        <div className="download-actions">
          <a className="button secondary" href="#settings/libraries">
            Manage media libraries
          </a>
          <a className="button secondary" href="#requests">
            Review family requests
          </a>
        </div>
      </section>
      <section className="panel">
        <h2>Add an authorized URL</h2>
        <form className="download-form" onSubmit={create} aria-busy={busy}>
          <label>
            Download name
            <input
              name="name"
              required
              maxLength={80}
              value={downloadName}
              onChange={(e) => setDownloadName(e.target.value)}
            />
          </label>
          <label>
            After downloading
            <select
              value={delivery}
              onChange={(e) => setDelivery(e.target.value)}
            >
              <option value="staging">Keep in download staging</option>
              <option value="library" disabled={!libraries.length}>
                Add to a media library automatically
              </option>
            </select>
          </label>
          {!libraries.length && (
            <p className="muted">
              {libraryError ||
                'Add a library to enable automatic organization.'}{' '}
              <a href="#settings/libraries">Manage libraries</a>
            </p>
          )}
          {delivery === 'library' && (
            <fieldset>
              <legend>Library destination and movie information</legend>
              <ImportFields
                libraries={libraries}
                initial={{ title: downloadName }}
              />
            </fieldset>
          )}
          {externalAvailable && (
            <label>
              Transfer client
              <select
                value={transferClient}
                onChange={(event) => setTransferClient(event.target.value)}
              >
                <option value="builtin">Built-in downloader</option>
                <option value="aria2">
                  aria2 (SHA-256 required; resume re-fetches)
                </option>
              </select>
            </label>
          )}
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
          <details>
            <summary>Advanced download options</summary>
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
              SHA-256 checksum{' '}
              {transferClient === 'aria2'
                ? '(required for aria2)'
                : '(optional)'}
              <input
                name="checksum"
                required={transferClient === 'aria2'}
                onInvalid={(event) =>
                  event.currentTarget
                    .closest('details')
                    ?.setAttribute('open', '')
                }
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
          </details>
          <button disabled={busy || !loaded}>
            {busy ? 'Working…' : 'Add download'}
          </button>
        </form>
      </section>
      <section aria-label="Download queue">
        <h2>Queue</h2>
        <label>
          Search downloads
          <input
            type="search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Title or library name"
          />
        </label>
        <label>
          Show downloads
          <select value={filter} onChange={(e) => setFilter(e.target.value)}>
            <option value="all">All downloads</option>
            <option value="active">Downloading and organizing</option>
            <option value="review">Needs library attention</option>
            <option value="paused">Paused or failed</option>
            <option value="imported">Added to library</option>
          </select>
        </label>
        {!loaded ? (
          <p role="status">Loading downloads…</p>
        ) : (
          !jobs.length && <p>No downloads yet.</p>
        )}
        {loaded && jobs.length > 0 && !jobs.some(matches) && (
          <p>No downloads match your search and category.</p>
        )}
        <div className="download-queue">
          {jobs.filter(matches).map((job) => (
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
              {!job.import && (
                <p className="muted">
                  Destination: staging ·{' '}
                  {job.state === 'completed'
                    ? 'Ready to match and add to a library'
                    : 'Choose a library after downloading'}
                </p>
              )}
              {job.import && (
                <div className="import-status">
                  <p>
                    Destination:{' '}
                    {libraries.find(
                      (library) => library.id === job.import?.libraryId,
                    )?.name ?? 'Library unavailable'}{' '}
                    · {job.import.title}
                    {job.import.year ? ` (${job.import.year})` : ''}
                  </p>
                  <p>
                    Library import: {job.import.state} ·{' '}
                    {job.import.classification === 'tv' ? 'TV Shows' : 'Movies'}
                  </p>
                  {job.import.error && (
                    <p role="status">{job.import.error.replaceAll('-', ' ')}</p>
                  )}
                  {job.import.mediaItemId != null && (
                    <a
                      className="button"
                      href={'#media/' + job.import.mediaItemId}
                    >
                      Open imported title
                    </a>
                  )}
                </div>
              )}
              {job.state === 'completed' &&
                (!job.import ||
                  ['inbox', 'discarded'].includes(job.import.state)) && (
                  <ImportForm job={job} busy={busy} mutate={mutate} />
                )}
              <div className="download-actions">
                {job.import &&
                  ['waiting', 'queued', 'copying', 'scanning'].includes(
                    job.import.state,
                  ) && (
                    <button
                      disabled={busy}
                      className="secondary"
                      onClick={() =>
                        void mutate(() =>
                          api(`/downloads/${job.id}/import`, 'PUT', {
                            revision: job.revision,
                            action: 'hold',
                          }),
                        )
                      }
                    >
                      Hold library import for {job.name}
                    </button>
                  )}
                {job.import?.state === 'held' && job.state === 'completed' && (
                  <>
                    <button
                      disabled={busy}
                      onClick={() =>
                        void mutate(() =>
                          api(`/downloads/${job.id}/import`, 'PUT', {
                            revision: job.revision,
                            action: 'retry',
                          }),
                        )
                      }
                    >
                      Retry library import for {job.name}
                    </button>
                    <button
                      disabled={busy}
                      className="secondary"
                      onClick={() =>
                        void mutate(() =>
                          api(`/downloads/${job.id}/import`, 'PUT', {
                            revision: job.revision,
                            action: 'discard',
                          }),
                        )
                      }
                    >
                      Discard import files, keep staged {job.name}
                    </button>
                  </>
                )}

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
