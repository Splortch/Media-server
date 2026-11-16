import { useEffect, useState } from 'react';
import { formatPlaybackTime } from '../services/playbackTime';
export type PreviewResolver = (id: number, seconds: number) => Promise<string>;
export default function SeekPreview({
  id,
  seconds,
  duration,
  imageUrl,
}: {
  id: number;
  seconds: number;
  duration: number;
  imageUrl?: PreviewResolver;
}) {
  const interval = Math.max(10, Math.ceil(duration / 120));
  const sample = Math.floor(seconds / interval) * interval;
  const [source, setSource] = useState<string>();
  useEffect(() => {
    let active = true;
    const timer = setTimeout(() => {
      const url = imageUrl
        ? imageUrl(id, sample)
        : Promise.resolve(
            `/api/media/${id}/thumbnail?position=${sample.toFixed(1)}`,
          );
      void url
        .then((value) => {
          if (active) setSource(value);
        })
        .catch(() => {
          if (active) setSource(undefined);
        });
    }, 150);
    return () => {
      active = false;
      clearTimeout(timer);
    };
  }, [id, sample, imageUrl]);
  return (
    <div className="seek-preview" aria-hidden="true">
      {source && (
        <img src={source} alt="" onError={() => setSource(undefined)} />
      )}
      <span>{formatPlaybackTime(seconds)}</span>
    </div>
  );
}
