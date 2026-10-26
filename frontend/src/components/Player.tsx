import { useRef, useState } from 'react';
import { api, type Media } from '../services/api';
export default function Player({
  item,
  resume,
  progressRevision = 0,
  streamUrl,
  onSaveProgress,
  onPlaybackError,
}: {
  item: Media;
  resume: number;
  progressRevision?: number;
  streamUrl?: string;
  onSaveProgress?: (seconds: number, completed: boolean) => Promise<void>;
  onPlaybackError?: () => Promise<void>;
}) {
  const lastSaved = useRef(0);
  const revision = useRef(progressRevision);
  const writes = useRef(Promise.resolve());
  const blocked = useRef(false);
  const [error, setError] = useState('');
  const [saveError, setSaveError] = useState('');
  async function save(
    video: HTMLVideoElement,
    force = false,
    completed = false,
  ) {
    if (
      !Number.isFinite(video.currentTime) ||
      (!force && Date.now() - lastSaved.current < 5000)
    )
      return;
    lastSaved.current = Date.now();
    const seconds = video.currentTime;
    const write = writes.current
      .catch(() => {})
      .then(async () => {
        if (blocked.current) return;
        try {
          if (onSaveProgress) await onSaveProgress(seconds, completed);
          else {
            const result = await api<{ revision: number }>(
              `/v1/media/${item.id}/progress-sync`,
              'PUT',
              { seconds, completed, expectedRevision: revision.current },
            );
            revision.current = result.revision;
          }
          setSaveError('');
        } catch {
          blocked.current = true;
          setSaveError(
            'Progress could not be synchronized. Another device may have changed it, or the server is unavailable. Reopen this video before saving again.',
          );
        }
      });
    writes.current = write;
    await write;
  }

  return (
    <section aria-label="Video player" className="player-wrap">
      <video
        controls
        playsInline
        preload="metadata"
        aria-label={item.title}
        src={streamUrl ?? `/api/media/${item.id}/stream`}
        onLoadedMetadata={(e) => {
          const v = e.currentTarget;
          if (Number.isFinite(v.duration) && resume < v.duration - 2)
            v.currentTime = resume;
        }}
        onTimeUpdate={(e) => {
          void save(e.currentTarget);
        }}
        onPause={(e) => {
          void save(e.currentTarget, true);
        }}
        onEnded={(e) => {
          void save(e.currentTarget, true, true);
        }}
        onError={() => {
          void onPlaybackError?.().catch(() => {});
          setError(
            'This video could not be played. Check that the file is available and its container and codecs are supported by your browser. Transcoding is not available in this alpha.',
          );
        }}
      />
      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
      {saveError && (
        <p role="status" className="error">
          {saveError}
        </p>
      )}
      {item.playback?.warning && (
        <p className="notice">{item.playback.warning}</p>
      )}
    </section>
  );
}
