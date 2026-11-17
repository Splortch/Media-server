import { useState, type ChangeEvent } from 'react';
import { api } from '../services/api';
export default function ArtworkManager({
  id,
  revision,
  onChanged,
}: {
  id: number;
  revision: number;
  onChanged: () => void;
}) {
  const [kind, setKind] = useState('poster');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [candidates, setCandidates] = useState<{ key: string; url: string }[]>(
    [],
  );
  async function act(path: string, body: object) {
    setBusy(true);
    setMessage('');
    try {
      await api(`/media/${id}/artwork/${path}`, 'POST', {
        ...body,
        expectedRevision: revision,
      });
      setMessage('Artwork updated.');
      setCandidates([]);
      onChanged();
    } catch {
      setMessage(
        'Artwork could not be updated. Reload if another administrator changed this title.',
      );
    } finally {
      setBusy(false);
    }
  }
  async function upload(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0];
    event.currentTarget.value = '';
    if (!file) return;
    if (
      file.size > 4 * 1024 * 1024 ||
      !['image/jpeg', 'image/png', 'image/webp'].includes(file.type)
    ) {
      setMessage('Choose a JPEG, PNG or WebP image up to 4 MiB.');
      return;
    }
    const bytes = new Uint8Array(await file.arrayBuffer());
    let binary = '';
    for (let offset = 0; offset < bytes.length; offset += 8192)
      binary += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
    await act('upload', { kind, data: btoa(binary) });
  }
  return (
    <details className="panel artwork-manager">
      <summary>Manage artwork</summary>
      <p>
        Upload a replacement, use a video frame, or choose an image already
        available for this title. Original media files are kept.
      </p>
      <label>
        Artwork type
        <select
          value={kind}
          onChange={(e) => setKind(e.target.value)}
          disabled={busy}
        >
          <option value="poster">Poster</option>
          <option value="backdrop">Backdrop</option>
        </select>
      </label>
      <label>
        Upload artwork
        <input
          type="file"
          accept="image/jpeg,image/png,image/webp"
          disabled={busy}
          onChange={(e) => void upload(e)}
        />
      </label>
      <div className="action-group">
        <button
          disabled={busy}
          onClick={() => void act('choice', { kind, source: 'thumbnail' })}
        >
          Use generated video frame
        </button>
        <button
          disabled={busy}
          className="secondary"
          onClick={() => void act('choice', { kind, source: 'remove' })}
        >
          Remove selected artwork
        </button>
        <button
          disabled={busy}
          className="secondary"
          onClick={() => {
            setBusy(true);
            void api<{ key: string; url: string }[]>(
              `/media/${id}/artwork/candidates`,
            )
              .then(setCandidates)
              .catch(() => setMessage('Available artwork could not be loaded.'))
              .finally(() => setBusy(false));
          }}
        >
          Show available artwork
        </button>
      </div>
      <div className="artwork-options">
        {candidates.map((c, index) => (
          <button
            key={c.key}
            disabled={busy}
            onClick={() =>
              void act('choice', { kind, source: 'existing', key: c.key })
            }
          >
            <img
              src={c.url}
              alt={`Use artwork option ${index + 1} as ${kind}`}
            />
          </button>
        ))}
      </div>
      {message && <p role="status">{message}</p>}
    </details>
  );
}
