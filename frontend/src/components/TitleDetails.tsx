import { useRef, type Ref } from 'react';
import type { Media } from '../services/api';
import { duration } from '../services/api';
import Icon from './Icon';
import Bookmarks, { type BookmarkRequest } from './Bookmarks';
import { rememberPlaybackArtwork } from './PlaybackEntrance';

export default function TitleDetails({
  item,
  resume,
  busy = false,
  onPlay,
  onResume,
  headingRef,
  bookmarks = false,
  onBookmark,
  bookmarkRequest,
}: {
  item: Media;
  bookmarks?: boolean;
  onBookmark?: (seconds: number) => void;
  bookmarkRequest?: BookmarkRequest;
  resume: number;
  busy?: boolean;
  headingRef?: Ref<HTMLHeadingElement>;
  onPlay: () => void;
  onResume: () => void;
}) {
  const artwork = useRef<HTMLDivElement>(null);
  function begin(action: () => void) {
    rememberPlaybackArtwork(item.id, artwork.current);
    action();
  }
  return (
    <section className="title-overview" aria-label="Title information">
      <div ref={artwork} className="cover title-art tone-0" aria-hidden="true">
        <span className="monogram">{item.title.slice(0, 1).toUpperCase()}</span>
      </div>
      <div className="title-copy">
        <p className="eyebrow">FROM YOUR COLLECTION</p>
        <h1 ref={headingRef} tabIndex={-1}>
          {item.title}
        </h1>
        <p className="muted">
          {duration(item.duration)} ·{' '}
          {item.container.split(',')[0].toUpperCase()}
        </p>
        <p className="muted">
          {item.metadata?.description ||
            'No description is embedded in this file.'}
        </p>
        <div className="action-group title-actions" aria-busy={busy}>
          {resume > 0 && (
            <button disabled={busy} onClick={() => begin(onResume)}>
              <Icon name="play" /> Resume from {duration(resume)}
            </button>
          )}
          <button
            disabled={busy}
            className={resume > 0 ? 'secondary' : undefined}
            onClick={() => begin(onPlay)}
          >
            <Icon name="play" /> Play from beginning
          </button>
        </div>
        <dl className="title-facts">
          {(
            [
              ['Release date', item.metadata?.releaseDate],
              ['Genre', item.metadata?.genre],
              ['Director', item.metadata?.director],
            ] as const
          ).map(([label, value]) =>
            value ? (
              <div key={label}>
                <dt>{label}</dt>
                <dd>{value}</dd>
              </div>
            ) : null,
          )}
          <div>
            <dt>Video</dt>
            <dd>{item.videoCodec.toUpperCase() || 'Unknown'}</dd>
          </div>
          <div>
            <dt>Audio</dt>
            <dd>{item.audioCodec.toUpperCase() || 'No audio metadata'}</dd>
          </div>
          <div>
            <dt>File size</dt>
            <dd>{(item.size / 1024 ** 3).toFixed(2)} GB</dd>
          </div>
        </dl>
      </div>
      {!!item.metadata?.chapters.length && (
        <section aria-label="Chapters">
          <h2>Chapters</h2>
          <ol>
            {item.metadata.chapters.map((chapter, index) => (
              <li key={index}>
                <button
                  disabled={busy || !onBookmark}
                  onClick={() => begin(() => onBookmark?.(chapter.start))}
                >
                  {duration(chapter.start)} · {chapter.title}
                </button>
              </li>
            ))}
          </ol>
        </section>
      )}
      {bookmarks && onBookmark && (
        <Bookmarks
          key={item.id}
          id={item.id}
          duration={item.duration}
          onSeek={onBookmark}
          request={bookmarkRequest}
        />
      )}
    </section>
  );
}
