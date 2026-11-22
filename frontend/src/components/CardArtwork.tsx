import { useEffect, useState } from 'react';
import { api } from '../services/api';
export type ArtworkKind = 'poster' | 'backdrop' | 'thumbnail';
export type ImageResolver = (id: number, kind: ArtworkKind) => Promise<string>;
export default function CardArtwork({
  id,
  request = api,
  imageUrl,
}: {
  id: number;
  request?: typeof api;
  imageUrl?: ImageResolver;
}) {
  const [source, setSource] = useState<string>();
  const [fallback, setFallback] = useState(false);
  useEffect(() => {
    let active = true;
    void (async () => {
      try {
        const options = await request<{
          posterUrl: string | null;
          thumbnailUrl: string | null;
          spoilerHidden?: boolean;
        }>(`/media/${id}/images`);
        if (
          options.spoilerHidden ||
          (!options.posterUrl && !options.thumbnailUrl)
        ) {
          if (active) setSource(undefined);
          return;
        }
        const kind = !fallback && options?.posterUrl ? 'poster' : 'thumbnail';
        const url = imageUrl
          ? await imageUrl(id, kind)
          : kind === 'poster'
            ? options.posterUrl!
            : options.thumbnailUrl;
        if (active) setSource(url ?? undefined);
      } catch {
        /* The letter remains a usable fallback. */
      }
    })();
    return () => {
      active = false;
    };
  }, [id, request, imageUrl, fallback]);
  return source ? (
    <img
      className="card-artwork"
      src={source}
      alt=""
      loading="lazy"
      decoding="async"
      onError={() => {
        setSource(undefined);
        if (!fallback) setFallback(true);
      }}
    />
  ) : null;
}
