import { useRef, useState } from 'react';
import { api } from '../services/api';
export type BookmarkRequest = <T>(
  path: string,
  method?: string,
  body?: unknown,
) => Promise<T>;
type Bookmark = {
  id: string;
  seconds: number;
  name: string;
  note: string;
  revision: number;
};
export default function Bookmarks({
  id,
  duration,
  onSeek,
  currentPosition,
  request = api,
}: {
  id: number;
  duration: number;
  onSeek: (seconds: number) => void;
  currentPosition?: () => number;
  request?: BookmarkRequest;
}) {
  const [rows, setRows] = useState<Bookmark[]>([]);
  const [editing, setEditing] = useState<Bookmark>();
  const [name, setName] = useState('');
  const [note, setNote] = useState('');
  const [seconds, setSeconds] = useState('0');
  const [portable, setPortable] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const nameField = useRef<HTMLInputElement>(null);
  const summary = useRef<HTMLElement>(null);
  const path = `/media/${id}/bookmarks`;
  async function load() {
    const values = await request<Bookmark[]>(path);
    if (
      !Array.isArray(values) ||
      values.length > 15 ||
      values.some(
        (b) =>
          !b ||
          typeof b.id !== 'string' ||
          !/^[a-f0-9]{32}$/i.test(b.id) ||
          !Number.isFinite(b.seconds) ||
          b.seconds < 0 ||
          b.seconds > duration ||
          typeof b.name !== 'string' ||
          b.name.length > 80 ||
          typeof b.note !== 'string' ||
          b.note.length > 500 ||
          !Number.isSafeInteger(b.revision),
      )
    )
      throw new Error('Invalid bookmark response.');
    setRows(values);
  }
  async function action(run: () => Promise<void>) {
    if (busy) return;
    setBusy(true);
    setError('');
    try {
      await run();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Bookmark operation failed.');
    } finally {
      setBusy(false);
    }
  }
  function reset() {
    setEditing(undefined);
    setName('');
    setNote('');
    setSeconds('0');
  }
  return (
    <details
      className="panel bookmarks"
      onToggle={(e) => {
        if (e.currentTarget.open) void action(load);
      }}
    >
      <summary ref={summary}>Private bookmarks and notes</summary>
      <p className="muted">
        Only you can see these. Times use the current playback timeline. Use
        zero for a general note. Originals are never changed.
      </p>
      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
      <button type="button" disabled={busy} onClick={() => void action(load)}>
        Reload bookmarks
      </button>
      <ul aria-label="Your bookmarks">
        {rows.map((b) => (
          <li key={b.id}>
            <button
              type="button"
              disabled={busy}
              onClick={() => onSeek(b.seconds)}
            >
              Jump to {b.name} at {b.seconds} seconds
            </button>
            {b.note && <p style={{ whiteSpace: 'pre-wrap' }}>{b.note}</p>}
            <button
              type="button"
              disabled={busy}
              aria-label={`Edit ${b.name}`}
              onClick={() => {
                setEditing(b);
                setName(b.name);
                setNote(b.note);
                setSeconds(String(b.seconds));
                nameField.current?.focus();
              }}
            >
              Edit
            </button>{' '}
            <button
              type="button"
              disabled={busy}
              aria-label={`Delete ${b.name}`}
              onClick={() =>
                void action(async () => {
                  await request(`${path}/${b.id}/${b.revision}`, 'DELETE');
                  if (editing?.id === b.id) reset();
                  await load();
                  summary.current?.focus();
                })
              }
            >
              Delete
            </button>
          </li>
        ))}
      </ul>
      <form
        aria-label={editing ? 'Edit bookmark' : 'Create bookmark'}
        onSubmit={(e) => {
          e.preventDefault();
          void action(async () => {
            await request(
              editing ? `${path}/${editing.id}` : path,
              editing ? 'PUT' : 'POST',
              {
                name,
                note,
                seconds: Number(seconds),
                expectedRevision: editing?.revision ?? 0,
              },
            );
            reset();
            await load();
            nameField.current?.focus();
          });
        }}
      >
        <label>
          Bookmark name
          <input
            ref={nameField}
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={80}
            required
            disabled={busy}
          />
        </label>
        <label>
          Timestamp in seconds
          <input
            type="number"
            min={0}
            max={duration}
            step="any"
            value={seconds}
            onChange={(e) => setSeconds(e.target.value)}
            required
            disabled={busy}
          />
        </label>
        {currentPosition && (
          <button
            type="button"
            disabled={busy}
            onClick={() =>
              setSeconds(
                String(Math.max(0, Math.min(duration, currentPosition()))),
              )
            }
          >
            Use current position
          </button>
        )}
        <label>
          Private note
          <textarea
            value={note}
            onChange={(e) => setNote(e.target.value)}
            maxLength={500}
            disabled={busy}
          />
        </label>
        <button disabled={busy || !Number.isFinite(duration) || duration <= 0}>
          {editing ? 'Save bookmark' : 'Add bookmark'}
        </button>
        {editing && (
          <button type="button" disabled={busy} onClick={reset}>
            Cancel editing
          </button>
        )}
      </form>
      <details>
        <summary>Portable export and import</summary>
        <p>
          Export requires library content hashing. Paste the JSON for this exact
          media file on another server. Import adds bookmarks without
          overwriting existing notes.
        </p>
        <button
          disabled={busy}
          onClick={() =>
            void action(async () =>
              setPortable(
                JSON.stringify(await request(path + '/export'), null, 2),
              ),
            )
          }
        >
          Export bookmarks
        </button>
        <label>
          Portable bookmark JSON
          <textarea
            value={portable}
            maxLength={64000}
            onChange={(e) => setPortable(e.target.value)}
            disabled={busy}
            spellCheck={false}
          />
        </label>
        <button
          disabled={busy || !portable}
          onClick={() =>
            void action(async () => {
              const data: unknown = JSON.parse(portable);
              if (new TextEncoder().encode(JSON.stringify(data)).length > 64000)
                throw new Error('Import must be under 64 KB.');
              await request(path + '/import', 'POST', data);
              await load();
            })
          }
        >
          Import bookmarks
        </button>
      </details>
    </details>
  );
}
