import { useEffect, useRef } from 'react';

let origin: { id: number; rect: DOMRect; at: number } | undefined;
export function rememberPlaybackArtwork(id: number, element: Element | null) {
  origin = element
    ? { id, rect: element.getBoundingClientRect(), at: Date.now() }
    : undefined;
}

/** Decorative only: never blocks playback, controls, or error recovery. */
export default function PlaybackEntrance({
  id,
  title,
  ready,
}: {
  id: number;
  title: string;
  ready: boolean;
}) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const element = ref.current;
    const from = origin;
    origin = undefined;
    if (
      !element ||
      window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ||
      !element.animate
    )
      return;
    const target = element.getBoundingClientRect();
    if (
      from?.id !== id ||
      Date.now() - from.at > 15000 ||
      !target.width ||
      !target.height
    )
      return;
    const animation = element.animate(
      [
        {
          transform: `translate(${from.rect.left - target.left}px, ${from.rect.top - target.top}px) scale(${from.rect.width / target.width}, ${from.rect.height / target.height})`,
          borderRadius: '22px',
        },
        { transform: 'none', borderRadius: '0px' },
      ],
      { duration: 650, easing: 'cubic-bezier(.22,1,.36,1)' },
    );
    return () => animation.cancel();
  }, [id]);
  return (
    <div
      ref={ref}
      aria-hidden="true"
      className={`playback-entrance tone-0${ready ? ' playback-entrance-ready' : ''}`}
    >
      <span className="monogram">{title.slice(0, 1).toUpperCase()}</span>
    </div>
  );
}
