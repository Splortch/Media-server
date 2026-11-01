import { useEffect, useState, type FormEvent } from 'react';
import { api } from '../../services/api';
import type { components } from '../../../../shared/api/src/generated';
type Plan = components['schemas']['DownloadImportPlan'];
type Job = components['schemas']['DownloadView'];
type Library = { id: number; name: string; kind: string };
export function ImportFields({
  libraries,
  initial,
  requestLibrary,
}: {
  libraries: Library[];
  initial?: Partial<Plan>;
  requestLibrary?: number;
}) {
  const [library, setLibrary] = useState(
    requestLibrary ?? initial?.libraryId ?? libraries[0]?.id ?? 0,
  );
  const kind =
    libraries.find((x) => x.id === library)?.kind ??
    initial?.classification ??
    'movies';
  return (
    <>
      <label>
        Destination library
        <select
          name="libraryId"
          required
          value={library}
          onChange={(e) => setLibrary(Number(e.target.value))}
        >
          <option value="0" disabled>
            Choose a library
          </option>
          {libraries
            .filter((x) => requestLibrary == null || x.id === requestLibrary)
            .map((x) => (
              <option value={x.id} key={x.id}>
                {x.name} · {x.kind === 'tv' ? 'TV Shows' : 'Movies'}
              </option>
            ))}
        </select>
      </label>
      <input name="classification" type="hidden" value={kind} />
      <label>
        Confirmed title
        <input
          name="title"
          required
          maxLength={120}
          defaultValue={initial?.title ?? ''}
        />
      </label>
      <label>
        Release year (optional)
        <input
          name="year"
          type="number"
          min={1888}
          max={2100}
          defaultValue={initial?.year ?? ''}
        />
      </label>
      <label>
        Approved content rating
        <select name="rating" defaultValue={initial?.rating ?? 0}>
          <option value="0">Unrated (restricted members cannot receive)</option>
          <option value="10">General (10)</option>
          <option value="13">Teen (13)</option>
          <option value="16">Mature (16)</option>
          <option value="18">Adult (18)</option>
        </select>
      </label>
      <label>
        Actual media container
        <select name="extension" defaultValue={initial?.extension ?? '.mp4'}>
          <option value=".mp4">MP4</option>
          <option value=".webm">WebM</option>
          <option value=".mkv">Matroska</option>
          <option value=".m4v">M4V</option>
          <option value=".mov">MOV</option>
          <option value=".avi">AVI</option>
        </select>
      </label>
      {kind === 'tv' && (
        <>
          <p className="muted">
            Explicit episode match only. Leave all three fields blank to keep
            the completed download in an administrator inbox.
          </p>
          <label>
            Confirmed series
            <input
              name="series"
              maxLength={120}
              defaultValue={initial?.series ?? ''}
            />
          </label>
          <label>
            Season
            <input
              name="season"
              type="number"
              min={0}
              max={99}
              defaultValue={initial?.season ?? ''}
            />
          </label>
          <label>
            Episode
            <input
              name="episode"
              type="number"
              min={1}
              max={999}
              defaultValue={initial?.episode ?? ''}
            />
          </label>
        </>
      )}
      <p className="muted">
        Creates new files under a separate MediaServer Imports folder. Existing
        media is never overwritten or reorganized. Unknown metadata stays
        unknown; no online providers are enabled.
      </p>
    </>
  );
}
export function importPlan(data: FormData): Plan {
  const optional = (name: string) =>
    data.get(name) ? Number(data.get(name)) : null;
  return {
    libraryId: Number(data.get('libraryId')),
    classification: String(data.get('classification')),
    title: String(data.get('title')),
    year: optional('year'),
    rating: Number(data.get('rating')),
    extension: String(data.get('extension')),
    series: data.get('series') ? String(data.get('series')) : null,
    season: optional('season'),
    episode: optional('episode'),
  };
}
function useLibraries() {
  const [libraries, setLibraries] = useState<Library[]>([]);
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    void api<Library[]>('/browse-libraries')
      .then((rows) => {
        if (active) setLibraries(rows);
      })
      .catch((e) => {
        if (active) setError(e.message);
      });
    return () => {
      active = false;
    };
  }, []);
  return { libraries, error };
}
export function ApproveDownload({
  request,
  onApproved,
}: {
  request: {
    id: number;
    revision: number;
    title: string;
    year: number | null;
    libraryId: number;
  };
  onApproved: () => void;
}) {
  const { libraries, error: libraryError } = useLibraries();
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function approve(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    setBusy(true);
    setError('');
    try {
      await api(`/requests/${request.id}/download`, 'POST', {
        requestRevision: request.revision,
        source: {
          name: request.title.slice(0, 80),
          url: data.get('url'),
          budgetBytes: Math.floor(Number(data.get('budget')) * 1024 * 1024),
          sha256: data.get('checksum') || null,
          priority: 0,
        },
        plan: importPlan(data),
      });
      form.reset();
      onApproved();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <details className="download-approval">
      <summary>Approve authorized download for {request.title}</summary>
      <p>
        Approval starts a real server download. Confirm source rights,
        destination, classification, rating and size before continuing.
      </p>
      {(error || libraryError) && <p role="alert">{error || libraryError}</p>}
      {libraries.length > 0 && (
        <form onSubmit={approve} className="download-form">
          <fieldset disabled={busy}>
            <label>
              Authorized HTTPS source
              <input
                name="url"
                type="password"
                autoComplete="off"
                required
                maxLength={4096}
              />
            </label>
            <label>
              Download size budget (MiB)
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
              Publisher SHA-256 (optional)
              <input name="checksum" maxLength={64} pattern="[a-fA-F0-9]{64}" />
            </label>
            <ImportFields
              libraries={libraries}
              requestLibrary={request.libraryId}
              initial={{ title: request.title, year: request.year }}
            />
            <button>
              {busy ? 'Approving…' : 'Approve download and delivery'}
            </button>
          </fieldset>
        </form>
      )}
      <form
        onSubmit={(e) => {
          e.preventDefault();
          const data = new FormData(e.currentTarget);
          setBusy(true);
          setError('');
          void api(`/requests/${request.id}/download/decline`, 'POST', {
            revision: request.revision,
            reason: data.get('reason'),
          })
            .then(onApproved)
            .catch((e) => setError(e.message))
            .finally(() => setBusy(false));
        }}
      >
        <label>
          Reason for declining
          <input name="reason" required maxLength={500} />
        </label>
        <button disabled={busy} className="secondary">
          Decline download request
        </button>
      </form>
    </details>
  );
}
export default function ImportForm({
  job,
  busy,
  mutate,
}: {
  job: Job;
  busy: boolean;
  mutate: (operation: () => Promise<unknown>) => Promise<void>;
}) {
  const { libraries, error } = useLibraries();
  return (
    <details>
      <summary>Match and organize {job.name}</summary>
      {error && <p role="alert">{error}</p>}
      {libraries.length > 0 && (
        <form
          className="download-form"
          onSubmit={(e) => {
            e.preventDefault();
            const data = new FormData(e.currentTarget);
            void mutate(() =>
              api(`/downloads/${job.id}/import-plan`, 'PUT', {
                revision: job.revision,
                plan: importPlan(data),
              }),
            );
          }}
        >
          <fieldset disabled={busy}>
            <ImportFields
              libraries={libraries}
              requestLibrary={
                job.import?.requestId ? job.import.libraryId : undefined
              }
              initial={
                job.import
                  ? {
                      libraryId: job.import.libraryId,
                      classification: job.import.classification,
                      title: job.import.title,
                      year: job.import.year,
                      series: job.import.series,
                      season: job.import.season,
                      episode: job.import.episode,
                      rating: job.import.rating,
                      extension: job.import.extension,
                    }
                  : { title: job.name }
              }
            />
            <button>Confirm match and import</button>
          </fieldset>
        </form>
      )}
    </details>
  );
}
