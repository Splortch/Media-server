import { useRef, useState } from 'react';
import { api, type Media } from '../services/api';
export default function Player({
  item,
  resume,
}: {
  item: Media;
  resume: number;
}) {
  const lastSaved = useRef(0);
  const [error, setError] = useState('');
  const [saveError, setSaveError] = useState('');
  async function save(video: HTMLVideoElement, force = false) {
    if (
      !Number.isFinite(video.currentTime) ||
      (!force && Date.now() - lastSaved.current < 5000)
    )
      return;
    lastSaved.current = Date.now();
    try {
      await api(`/media/${item.id}/progress`, 'PUT', {
        seconds: video.currentTime,
      });
      setSaveError('');
    } catch {
      setSaveError(
        'Playback position could not be saved. Check your connection.',
      );
    }
  }
  return (
    <section aria-label="Video player" className="player-wrap">
      <video
        controls
        playsInline
        preload="metadata"
        aria-label={item.title}
        src={`/api/media/${item.id}/stream`}
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
          void save(e.currentTarget, true);
        }}
        onError={() =>
          setError(
            'This video could not be played. Check that the file is available and its container and codecs are supported by your browser. Transcoding is not available in this alpha.',
          )
        }
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
