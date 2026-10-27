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
  const videoRef = useRef<HTMLVideoElement>(null);
  const sample = useRef<{ time: number; end: number } | undefined>(undefined);
  const [buffering, setBuffering] = useState(false);
  const [metrics, setMetrics] = useState<{
    ahead: number;
    dropped?: number;
    total?: number;
    fill?: number;
  }>({ ahead: 0 });
  const [support, setSupport] = useState('Not checked yet');
  const [reference, setReference] = useState('');
  function measure(video: HTMLVideoElement) {
    const now = performance.now();
    let end = video.currentTime;
    for (let i = 0; i < video.buffered.length; i++)
      if (
        video.buffered.start(i) <= video.currentTime &&
        video.buffered.end(i) >= video.currentTime
      )
        end = video.buffered.end(i);
    const previous = sample.current;
    if (previous && now - previous.time < 1000) return;
    const quality = video.getVideoPlaybackQuality?.();
    const fill =
      previous &&
      item.duration > 0 &&
      now > previous.time &&
      end >= previous.end
        ? ((end - previous.end) * ((item.size * 8) / item.duration)) /
          (now - previous.time) /
          1000
        : undefined;
    sample.current = { time: now, end };
    setMetrics({
      ahead: Math.max(0, end - video.currentTime),
      dropped: quality?.droppedVideoFrames,
      total: quality?.totalVideoFrames,
      fill,
    });
    setSupport(
      video.canPlayType(
        item.playback?.mimeType ||
          (item.container.includes('webm') ? 'video/webm' : 'video/mp4'),
      ) || 'Not reported as supported',
    );
  }
  async function playbackFailed(video: HTMLVideoElement) {
    const code = video.error?.code;
    const category =
      code === 2
        ? 'network'
        : code === 3
          ? 'decode'
          : code === 4
            ? 'unsupported'
            : 'unknown';
    setError(playbackErrorMessage(code));
    setBuffering(false);
    if (onPlaybackError) {
      await onPlaybackError().catch(() => {});
      return;
    }
    void api(`/v1/media/${item.id}/playback-errors`, 'POST', {
      code: category,
      client: 'web',
    }).catch(() => {});
    try {
      const info = await api<{ available: boolean; correlationId: string }>(
        `/media/${item.id}/playback-info`,
      );
      setReference(info.correlationId);
      if (!info.available)
        setError(
          'The server cannot read this media file. It may have been moved, removed, or become inaccessible. Ask the administrator to check the library.',
        );
    } catch {
      /* Keep the browser error useful when diagnostics cannot reach the server. */
    }
  }
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
        ref={videoRef}
        controls
        playsInline
        preload="metadata"
        aria-label={item.title}
        src={streamUrl ?? `/api/media/${item.id}/stream`}
        onLoadedMetadata={(e) => {
          const v = e.currentTarget;
          measure(v);
          if (Number.isFinite(v.duration) && resume < v.duration - 2)
            v.currentTime = resume;
        }}
        onTimeUpdate={(e) => {
          void save(e.currentTarget);
          measure(e.currentTarget);
        }}
        onPause={(e) => {
          void save(e.currentTarget, true);
        }}
        onEnded={(e) => {
          void save(e.currentTarget, true, true);
        }}
        onProgress={(e) => measure(e.currentTarget)}
        onWaiting={() => setBuffering(true)}
        onPlaying={() => setBuffering(false)}
        onCanPlay={() => setBuffering(false)}
        onSeeking={() => {
          sample.current = undefined;
        }}
        onSeeked={(e) => {
          sample.current = undefined;
          measure(e.currentTarget);
        }}
        onError={(e) => {
          void playbackFailed(e.currentTarget);
        }}
      />
      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
      {reference && <p className="muted">Diagnostic reference: {reference}</p>}
      {buffering && (
        <p role="status">Buffering… Waiting for more video data.</p>
      )}
      <details className="diagnostics">
        <summary>Playback diagnostics</summary>
        <dl>
          <dt>Playback mode</dt>
          <dd>{item.playback?.mode || 'direct'} (no transcoding)</dd>
          <dt>Container / codecs</dt>
          <dd>
            {item.container} · {item.videoCodec || 'unknown'} /{' '}
            {item.audioCodec || 'no audio'}
          </dd>
          <dt>Browser container support</dt>
          <dd>
            {support}; codec support depends on the browser and operating
            system.
          </dd>
          <dt>Buffered ahead</dt>
          <dd>{metrics.ahead.toFixed(1)} seconds</dd>
          <dt>Dropped / total frames</dt>
          <dd>
            {metrics.dropped === undefined
              ? 'Unavailable in this browser'
              : `${metrics.dropped} / ${metrics.total}`}
          </dd>
          <dt>Estimated buffer fill</dt>
          <dd>
            {metrics.fill === undefined
              ? 'Not enough samples'
              : `${metrics.fill.toFixed(2)} Mbit/s`}{' '}
            (file-average estimate, not measured network throughput)
          </dd>
        </dl>
      </details>
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

export function playbackErrorMessage(code?: number) {
  if (code === 1)
    return 'Playback was interrupted. Try opening the video again.';
  if (code === 2)
    return 'The video could not be downloaded. Check your connection and that the server is still running.';
  if (code === 3)
    return 'The browser could not decode this video. The file may be damaged or use an unsupported codec. Transcoding is not available in this alpha.';
  if (code === 4)
    return 'This container or codec is not supported by this browser, or the media is unavailable. Try a browser-compatible MP4 or WebM file. Transcoding is not available in this alpha.';
  return 'This video could not be played. Check that the file is available and its container and codecs are supported by your browser. Transcoding is not available in this alpha.';
}
