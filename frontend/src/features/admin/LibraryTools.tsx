import { useState } from 'react';
import { api, type Library } from '../../services/api';
export default function LibraryTools({ libraries }: { libraries: Library[] }) {
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [duplicates, setDuplicates] = useState<
    {
      fingerprint: string;
      items: { id: number; title: string; libraryId: number }[];
    }[]
  >([]);
  return (
    <section className="panel">
      <h2>Library reconciliation</h2>
      <p className="muted">
        Watching debounces file events and reconciles every ten minutes.
        Full-file hashing enables duplicate detection and preserves IDs for
        unambiguous moves; hashing large files uses disk bandwidth. Missing
        entries are hidden, never deleted from disk.
      </p>
      {error && <p role="alert">{error}</p>}
      {message && <p role="status">{message}</p>}
      {libraries.map((library) => (
        <Options
          key={library.id}
          library={library}
          onError={setError}
          onDone={setMessage}
        />
      ))}
      <button
        className="secondary"
        onClick={() => {
          void api<typeof duplicates>('/duplicates')
            .then(setDuplicates)
            .catch((e) => setError(e.message));
        }}
      >
        Find duplicate media
      </button>
      <ul>
        {duplicates.map((group) => (
          <li key={group.fingerprint}>
            Identical file contents:{' '}
            {group.items
              .map(
                (x) => `${x.title} (library #${x.libraryId}, media #${x.id})`,
              )
              .join(', ')}
            . No files were changed.
          </li>
        ))}
      </ul>
    </section>
  );
}
function Options({
  library,
  onError,
  onDone,
}: {
  library: Library;
  onError: (message: string) => void;
  onDone: (message: string) => void;
}) {
  const [kind, setKind] = useState(library.kind || 'movies');
  const [watch, setWatch] = useState(library.watchEnabled || false);
  const [hash, setHash] = useState(library.computeHashes || false);
  const [missing, setMissing] = useState<{ id: number; title: string }[]>([]);
  return (
    <details>
      <summary>{library.name} scanning options</summary>
      <div className="settings-form">
        <label>
          Library type for {library.name}
          <select
            aria-label={`Library type for ${library.name}`}
            value={kind}
            onChange={(e) => setKind(e.target.value)}
          >
            <option value="movies">Movies</option>
            <option value="tv">TV</option>
          </select>
        </label>
        <label>
          <input
            type="checkbox"
            checked={watch}
            onChange={(e) => setWatch(e.target.checked)}
          />
          Watch {library.name} for changes
        </label>
        <label>
          <input
            type="checkbox"
            checked={hash}
            onChange={(e) => setHash(e.target.checked)}
          />
          Hash {library.name} files
        </label>
        <button
          onClick={() => {
            void api(`/libraries/${library.id}/options`, 'PUT', {
              kind,
              watchEnabled: watch,
              computeHashes: hash,
            })
              .then(() =>
                onDone(
                  `${library.name}: options saved. Rescan to refresh hashes and moves.`,
                ),
              )
              .catch((e) => onError(e.message));
          }}
        >
          Save scanning options for {library.name}
        </button>
        <button
          className="secondary"
          onClick={() => {
            void api<typeof missing>(`/libraries/${library.id}/missing`)
              .then(setMissing)
              .catch((e) => onError(e.message));
          }}
        >
          Show missing entries for {library.name}
        </button>
        <ul>
          {missing.map((item) => (
            <li key={item.id}>
              {item.title} (media #{item.id})
            </li>
          ))}
        </ul>
      </div>
    </details>
  );
}
