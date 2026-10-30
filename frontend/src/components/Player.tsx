import { useCallback, useEffect, useRef, useState } from 'react';
import Icon from './Icon';
import PlaybackSettings, { type PlaybackTransport } from './PlaybackSettings';
import { api, type Media } from '../services/api';
export default function Player({
  item,
  resume,
  startPlaying = false,
  guest = false,
  progressRevision = 0,
  streamUrl,
  onSaveProgress,
  onPlaybackError,
  playbackTransport,
}: {
  item: Media;
  resume: number;
  startPlaying?: boolean;
  guest?: boolean;
  progressRevision?: number;
  streamUrl?: string;
  onSaveProgress?: (seconds: number, completed: boolean) => Promise<void>;
  onPlaybackError?: () => Promise<void>;
  playbackTransport?: PlaybackTransport;
}) {
  const [source, setSource] = useState(
    streamUrl ?? `/api/media/${item.id}/stream`,
  );
  const [caption, setCaption] = useState('');
  const videoRef = useRef<HTMLVideoElement>(null);
  const explicitStart = useRef(startPlaying);
  const nextPosition = useRef<number | undefined>(undefined);
  const switching = useRef(false);
  const changeSource = useCallback(
    (value: string) => {
      if (value === source) return;
      nextPosition.current = videoRef.current?.currentTime ?? 0;
      switching.current = true;
      setSource(value);
    },
    [source],
  );
  const surfaceRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (startPlaying) surfaceRef.current?.focus();
  }, [startPlaying]);
  const [paused, setPaused] = useState(true);
  const [position, setPosition] = useState(0);
  const [duration, setDuration] = useState(0);
  const [volume, setVolume] = useState(1);
  const [muted, setMuted] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  useEffect(() => {
    const changed = () =>
      setFullscreen(document.fullscreenElement === surfaceRef.current);
    document.addEventListener('fullscreenchange', changed);
    return () => document.removeEventListener('fullscreenchange', changed);
  }, []);
  async function togglePlay() {
    const video = videoRef.current;
    if (!video) return;
    if (!video.paused) video.pause();
    else {
      try {
        await video.play();
        setError('');
      } catch {
        setError(
          'Playback could not start. Try again or check the playback diagnostics.',
        );
      }
    }
  }
  function seek(seconds: number) {
    const video = videoRef.current;
    if (!video || !Number.isFinite(video.duration) || video.duration <= 0)
      return;
    video.currentTime = Math.max(0, Math.min(video.duration, seconds));
    setPosition(video.currentTime);
  }
  async function toggleFullscreen() {
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else if (surfaceRef.current?.requestFullscreen)
        await surfaceRef.current.requestFullscreen();
      else setError('Fullscreen is unavailable in this browser.');
    } catch {
      setError('Fullscreen could not be opened.');
    }
  }
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
      switching.current ||
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
      {!guest && (!streamUrl || playbackTransport) && (
        <PlaybackSettings
          transport={playbackTransport}
          id={item.id}
          video={videoRef}
          source={source}
          onSource={changeSource}
          onCaption={setCaption}
        />
      )}
      <div
        className="player-surface"
        role="group"
        ref={surfaceRef}
        tabIndex={0}
        aria-label="Playback controls"
        onKeyDown={(event) => {
          if (
            event.ctrlKey ||
            event.altKey ||
            event.metaKey ||
            (event.target instanceof HTMLElement &&
              event.target.closest('button,input,select,textarea'))
          )
            return;
          const key = event.key.toLowerCase();
          if (![' ', 'k', 'f', 'm', 'arrowleft', 'arrowright'].includes(key))
            return;
          event.preventDefault();
          event.stopPropagation();
          if (key === ' ' || key === 'k') void togglePlay();
          if (key === 'f') void toggleFullscreen();
          if (key === 'm' && videoRef.current)
            videoRef.current.muted = !videoRef.current.muted;
          if (key === 'arrowleft' || key === 'arrowright')
            seek(
              (videoRef.current?.currentTime ?? 0) +
                (key === 'arrowright' ? 10 : -10),
            );
        }}
      >
        <video
          ref={videoRef}
          crossOrigin={playbackTransport ? 'anonymous' : undefined}
          playsInline
          preload="metadata"
          aria-label={item.title}
          src={source.endsWith('.m3u8') ? undefined : source}
          onLoadedMetadata={(e) => {
            const v = e.currentTarget;
            setDuration(Number.isFinite(v.duration) ? v.duration : 0);
            measure(v);
            const target = nextPosition.current ?? resume;
            if (Number.isFinite(v.duration) && target < v.duration - 2)
              v.currentTime = target;
            nextPosition.current = undefined;
            switching.current = false;
            if (explicitStart.current) {
              explicitStart.current = false;
              void v
                .play()
                .catch(() =>
                  setError(
                    'Playback could not start automatically after your selection. Press Play in the player to retry.',
                  ),
                );
            }
          }}
          onTimeUpdate={(e) => {
            setPosition(e.currentTarget.currentTime);
            void save(e.currentTarget);
            measure(e.currentTarget);
          }}
          onPlay={() => setPaused(false)}
          onVolumeChange={(e) => {
            setVolume(e.currentTarget.volume);
            setMuted(e.currentTarget.muted);
          }}
          onDurationChange={(e) =>
            setDuration(
              Number.isFinite(e.currentTarget.duration)
                ? e.currentTarget.duration
                : 0,
            )
          }
          onPause={(e) => {
            setPaused(true);
            void save(e.currentTarget, true);
          }}
          onEnded={(e) => {
            setPaused(true);
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
        >
          {caption && (
            <track
              key={caption}
              src={caption}
              kind="subtitles"
              label="Selected subtitles"
              default
            />
          )}
        </video>
        <div className="player-controls">
          <div className="player-timeline">
            <span>{formatPlaybackTime(position)}</span>
            <input
              aria-label="Seek"
              type="range"
              min="0"
              max={duration || 1}
              step="0.1"
              value={Math.min(position, duration || 0)}
              disabled={!duration}
              aria-valuetext={`${formatPlaybackTime(position)} of ${formatPlaybackTime(duration)}`}
              onChange={(e) => seek(Number(e.target.value))}
            />
            <span>{formatPlaybackTime(duration)}</span>
          </div>
          <div className="player-buttons">
            <div
              className="player-transport"
              role="group"
              aria-label="Play and seek"
            >
              <button
                type="button"
                className="player-play"
                onClick={() => void togglePlay()}
                aria-label={paused ? 'Play' : 'Pause'}
              >
                <Icon name={paused ? 'play' : 'pause'} />{' '}
                <span>{paused ? 'Play' : 'Pause'}</span>
              </button>
              <button
                type="button"
                disabled={!duration}
                onClick={() => seek((videoRef.current?.currentTime ?? 0) - 10)}
                aria-label="Rewind 10 seconds"
              >
                <Icon name="rewind" />{' '}
                <span className="control-caption">10 seconds</span>
              </button>
              <button
                type="button"
                disabled={!duration}
                onClick={() => seek((videoRef.current?.currentTime ?? 0) + 10)}
                aria-label="Forward 10 seconds"
              >
                <Icon name="forward" />{' '}
                <span className="control-caption">10 seconds</span>
              </button>
            </div>
            <div
              className="player-volume"
              role="group"
              aria-label="Audio controls"
            >
              <button
                type="button"
                onClick={() => {
                  if (videoRef.current)
                    videoRef.current.muted = !videoRef.current.muted;
                }}
                aria-label={muted ? 'Unmute' : 'Mute'}
                title={muted ? 'Unmute' : 'Mute'}
              >
                <Icon name={muted ? 'muted' : 'volume'} />
              </button>
              <input
                aria-label="Volume"
                type="range"
                min="0"
                max="1"
                step="0.05"
                value={muted ? 0 : volume}
                onChange={(e) => {
                  if (videoRef.current) {
                    videoRef.current.volume = Number(e.target.value);
                    videoRef.current.muted = false;
                  }
                }}
              />
            </div>
            <button
              type="button"
              aria-label={fullscreen ? 'Exit fullscreen' : 'Fullscreen'}
              className="player-fullscreen"
              onClick={() => void toggleFullscreen()}
            >
              <Icon name="fullscreen" />
              <span className="control-caption">
                {fullscreen ? 'Exit fullscreen' : 'Fullscreen'}
              </span>
            </button>
          </div>
        </div>
      </div>
      {guest && (
        <p className="muted">
          Guest playback · Progress is not saved. Direct Play only.
        </p>
      )}
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
    return 'The browser could not decode this video. The file may be damaged or use an unsupported codec. In the web player, try Prepare HLS playback under Quality, audio and subtitles. Native clients currently use Direct Play.';
  if (code === 4)
    return 'This container or codec is not supported by this browser, or the media is unavailable. Try a browser-compatible MP4 or WebM file. In the web player, try Prepare HLS playback under Quality, audio and subtitles. Native clients currently use Direct Play.';
  return 'This video could not be played. Check that the file is available and its container and codecs are supported by your browser. In the web player, try Prepare HLS playback under Quality, audio and subtitles. Native clients currently use Direct Play.';
}

export function formatPlaybackTime(seconds: number) {
  const total = Number.isFinite(seconds) ? Math.max(0, Math.floor(seconds)) : 0;
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  return `${hours ? `${hours}:${String(minutes).padStart(2, '0')}` : minutes}:${String(total % 60).padStart(2, '0')}`;
}
