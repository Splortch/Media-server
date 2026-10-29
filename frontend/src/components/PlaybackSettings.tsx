import { useEffect, useRef, useState, type RefObject } from 'react';
import Hls from 'hls.js';
import { api } from '../services/api';

type Track = {
  index: number;
  type: string;
  codec: string;
  language?: string;
  title?: string;
};
type Options = {
  tracks: Track[];
  externalWebVtt: boolean;
  encoders: string[];
  color?: { hdr: boolean };
  conversionNotice: string;
};
type Session = {
  id: string;
  state: string;
  error?: string;
  encoder: string;
  manifest: string;
  encodeSeconds: number;
};
function validSession(value: Session, expected?: string): Session {
  if (
    !value ||
    (expected !== undefined && value.id !== expected) ||
    !/^[a-f0-9]{32}$/i.test(value.id) ||
    !['preparing', 'ready', 'failed', 'cancelled'].includes(value.state) ||
    !Number.isFinite(value.encodeSeconds) ||
    value.encodeSeconds < 0 ||
    value.manifest !== `/api/v1/playback-sessions/${value.id}/files/master.m3u8`
  )
    throw new Error('Invalid playback response.');
  return value;
}
function validOptions(value: Options): Options {
  if (
    !value ||
    !Array.isArray(value.tracks) ||
    value.tracks.length > 256 ||
    value.tracks.some(
      (t) =>
        !Number.isSafeInteger(t.index) ||
        typeof t.type !== 'string' ||
        typeof t.codec !== 'string' ||
        (t.title != null &&
          (typeof t.title !== 'string' || t.title.length > 512)) ||
        (t.language != null &&
          (typeof t.language !== 'string' || t.language.length > 64)),
    ) ||
    !Array.isArray(value.encoders) ||
    value.encoders.length > 5 ||
    value.encoders.some(
      (e) =>
        ![
          'libx264',
          'h264_nvenc',
          'h264_qsv',
          'h264_vaapi',
          'h264_amf',
        ].includes(e),
    )
  )
    throw new Error('Invalid playback options.');
  return value;
}
export type PlaybackTransport = {
  request: typeof api;
  fileUrl: (session: string, file: string) => Promise<string>;
  directUrl: string;
};
const webFileUrl = async (session: string, file: string) =>
  `/api/v1/playback-sessions/${session}/files/${file}`;
export default function PlaybackSettings({
  id,
  video,
  source,
  onSource,
  onCaption,
  transport,
}: {
  id: number;
  video: RefObject<HTMLVideoElement | null>;
  source: string;
  onSource: (value: string) => void;
  onCaption: (value: string) => void;
  transport?: PlaybackTransport;
}) {
  const desktop = !!transport;
  const request = transport?.request ?? api;
  const fileUrl = transport?.fileUrl ?? webFileUrl;
  const [opened, setOpened] = useState(false);
  const [options, setOptions] = useState<Options>();
  const [audio, setAudio] = useState('');
  const [subtitle, setSubtitle] = useState('');
  const [encoder, setEncoder] = useState('libx264');
  const [error, setError] = useState('');
  const [session, setSession] = useState<Session>();
  const [quality, setQuality] = useState(-1);
  const [levels, setLevels] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [adaptive, setAdaptive] = useState({
    height: 0,
    switches: 0,
    stalls: 0,
    stallSeconds: 0,
    estimatedKbps: 0,
  });
  const hls = useRef<Hls | null>(null);
  const current = useRef<string | undefined>(undefined);
  const mounted = useRef(true);
  useEffect(() => {
    if (!opened) return;
    let active = true;
    request<Options>(`/v1/media/${id}/playback-options`)
      .then((value) => {
        if (active) setOptions(validOptions(value));
      })
      .catch((e) => {
        if (active) setError((e as Error).message);
      });
    return () => {
      active = false;
    };
  }, [id, opened, request]);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      if (current.current)
        void request(
          `/v1/playback-sessions/${current.current}`,
          'DELETE',
        ).catch(() => {});
    };
  }, [id, request]);
  const resume = useRef(0);
  useEffect(() => {
    const element = video.current;
    if (!element || !source.endsWith('.m3u8')) return;
    const position = resume.current;
    const playing = !element.paused;
    if (Hls.isSupported()) {
      const engine = new Hls({
        enableWorker: false,
        maxBufferLength: 12,
        maxMaxBufferLength: 24,
        maxBufferSize: 8 * 1024 * 1024,
        xhrSetup: (xhr) => {
          xhr.withCredentials = true;
        },
      });
      hls.current = engine;
      engine.loadSource(source);
      engine.attachMedia(element);
      let played = false;
      let waitingAt: number | undefined;
      let previousLevel = -1;
      const metrics = {
        height: 0,
        switches: 0,
        stalls: 0,
        stallSeconds: 0,
        estimatedKbps: 0,
      };
      const publish = () => {
        metrics.estimatedKbps = Number.isFinite(engine.bandwidthEstimate)
          ? Math.round(engine.bandwidthEstimate / 1000)
          : 0;
        setAdaptive({ ...metrics });
      };
      const finishWait = () => {
        if (waitingAt !== undefined)
          metrics.stallSeconds += (performance.now() - waitingAt) / 1000;
        waitingAt = undefined;
        publish();
      };
      const onPlaying = () => {
        played = true;
        finishWait();
      };
      const onWaiting = () => {
        if (
          played &&
          !element.paused &&
          !element.seeking &&
          !element.ended &&
          waitingAt === undefined
        ) {
          waitingAt = performance.now();
          metrics.stalls++;
          publish();
        }
      };
      const onSeeking = () => {
        waitingAt = undefined;
      };
      element.addEventListener('playing', onPlaying);
      element.addEventListener('waiting', onWaiting);
      element.addEventListener('pause', finishWait);
      element.addEventListener('seeking', onSeeking);
      engine.on(Hls.Events.LEVEL_SWITCHED, (_, data) => {
        if (previousLevel >= 0 && previousLevel !== data.level)
          metrics.switches++;
        previousLevel = data.level;
        metrics.height = engine.levels[data.level]?.height ?? 0;
        publish();
      });
      engine.on(Hls.Events.FRAG_LOADED, publish);
      engine.on(Hls.Events.MANIFEST_PARSED, () => {
        setLevels(
          engine.levels.map(
            (level) => level.name || `${Math.round(level.bitrate / 1000)} kbps`,
          ),
        );
        element.currentTime = position;
        if (playing) void element.play().catch(() => {});
      });
      engine.on(Hls.Events.ERROR, (_, data) => {
        if (data.fatal) {
          setError(
            'HLS playback failed. Check the server connection and sign in again if your session expired.',
          );
          void request('/v1/auth/me').catch(() => {});
          void request(`/v1/media/${id}/playback-errors`, 'POST', {
            code: 'decode',
            client: desktop ? 'desktop-hls' : 'web-hls',
          }).catch(() => {});
        }
      });
      return () => {
        element.removeEventListener('playing', onPlaying);
        element.removeEventListener('waiting', onWaiting);
        element.removeEventListener('pause', finishWait);
        element.removeEventListener('seeking', onSeeking);
        engine.destroy();
        hls.current = null;
      };
    }
    if (element.canPlayType('application/vnd.apple.mpegurl')) {
      element.setAttribute('src', source);
      const restore = () =>
        element.fastSeek
          ? element.fastSeek(position)
          : (element.currentTime = position);
      element.addEventListener('loadedmetadata', restore, { once: true });
      return () => element.removeEventListener('loadedmetadata', restore);
    }
  }, [source, video, request, id, desktop]);
  async function convert() {
    if (
      !Hls.isSupported() &&
      !video.current?.canPlayType('application/vnd.apple.mpegurl')
    ) {
      setError(
        'This player does not support HLS. Use Direct Play or a compatible browser.',
      );
      return;
    }
    video.current?.pause();
    setBusy(true);
    setError('');
    activated.current = undefined;
    selectedCaption.current = subtitle !== '';
    try {
      if (current.current)
        await request(`/v1/playback-sessions/${current.current}`, 'DELETE');
      const created = await request<Session>(
        `/v1/media/${id}/playback-sessions`,
        'POST',
        {
          audioTrack: audio === '' ? null : Number(audio),
          subtitleTrack: subtitle === '' ? null : Number(subtitle),
          encoder,
        },
      );
      validSession(created);
      current.current = created.id;
      if (!mounted.current) {
        await request(`/v1/playback-sessions/${created.id}`, 'DELETE');
        return;
      }
      setSession(validSession(created));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  useEffect(() => {
    if (session?.state !== 'preparing') return;
    const timer = setTimeout(() => {
      request<Session>(`/v1/playback-sessions/${session.id}`)
        .then(async (next) => {
          setSession(validSession(next, session.id));
          if (next.state === 'failed')
            setError(next.error || 'Conversion failed.');
        })
        .catch((e) => {
          setError((e as Error).message);
          if (mounted.current) setSession({ ...session });
        });
    }, 1500);
    return () => clearTimeout(timer);
  }, [session, request]);
  const activated = useRef<string | undefined>(undefined);
  const selectedCaption = useRef(false);
  useEffect(() => {
    if (session?.state !== 'ready' || activated.current === session.id) return;
    let active = true;
    activated.current = session.id;
    void Promise.all([
      fileUrl(session.id, 'master.m3u8'),
      selectedCaption.current
        ? fileUrl(session.id, 'captions.vtt')
        : Promise.resolve(''),
    ])
      .then(([manifest, caption]) => {
        if (!active) return;
        resume.current = video.current?.currentTime ?? 0;
        onSource(manifest);
        onCaption(caption);
      })
      .catch((e) => setError((e as Error).message));
    return () => {
      active = false;
    };
  }, [session, fileUrl, onSource, onCaption, video]);
  async function direct() {
    setSession(undefined);
    const previous = current.current;
    current.current = undefined;
    hls.current?.destroy();
    hls.current = null;
    onSource(transport?.directUrl ?? `/api/media/${id}/stream`);
    onCaption('');
    setLevels([]);
    setQuality(-1);
    if (previous)
      await request(`/v1/playback-sessions/${previous}`, 'DELETE').catch((e) =>
        setError((e as Error).message),
      );
  }
  return (
    <details
      className="panel"
      onToggle={(e) => setOpened(e.currentTarget.open)}
    >
      <summary>Quality, audio and subtitles</summary>
      <p>
        {options?.conversionNotice ||
          'Direct Play uses the original file. HLS conversion requires FFmpeg on the server.'}
      </p>
      {options?.color?.hdr && (
        <p role="status">
          HDR source. HLS converts to SDR using Hable tone mapping. Direct Play
          requires a compatible HDR display and decoder.
        </p>
      )}
      {levels.length > 0 && (
        <p
          aria-label="Adaptive playback diagnostics"
          data-height={adaptive.height}
          data-switches={adaptive.switches}
          data-stalls={adaptive.stalls}
          data-stall-seconds={adaptive.stallSeconds.toFixed(3)}
          data-estimated-kbps={adaptive.estimatedKbps}
        >
          Playing {adaptive.height || 'unknown'}p · {adaptive.switches} quality
          switches · {adaptive.stalls} rebuffer events (
          {adaptive.stallSeconds.toFixed(1)} seconds) · estimated bandwidth{' '}
          {adaptive.estimatedKbps} kbps
        </p>
      )}
      <label>
        Audio track
        <select
          value={audio}
          onChange={(e) => setAudio(e.target.value)}
          disabled={busy || session?.state === 'preparing'}
        >
          <option value="">Default audio</option>
          {options?.tracks
            ?.filter((t) => t.type === 'audio')
            .map((t) => (
              <option key={t.index} value={t.index}>
                {t.language || 'Unknown language'} · {t.title || t.codec} ·
                track {t.index}
              </option>
            ))}
        </select>
      </label>
      <label>
        Subtitles
        <select
          aria-label="Subtitles"
          value={subtitle}
          onChange={(e) => setSubtitle(e.target.value)}
          disabled={busy || session?.state === 'preparing'}
        >
          <option value="">Off</option>
          {options?.externalWebVtt &&
            !options.tracks.some((t) => t.index === -1) && (
              <option value="-1">External WebVTT</option>
            )}
          {options?.tracks
            ?.filter((t) => t.type === 'subtitle')
            .map((t) => (
              <option
                key={t.index}
                value={t.index}
                disabled={
                  ![
                    'subrip',
                    'ass',
                    'ssa',
                    'webvtt',
                    'mov_text',
                    'text',
                  ].includes(t.codec)
                }
              >
                {t.language || 'Unknown language'} · {t.title || t.codec} ·
                track {t.index}
              </option>
            ))}
        </select>
      </label>
      <label>
        Encoder preference
        <select
          value={encoder}
          onChange={(e) => setEncoder(e.target.value)}
          disabled={busy || session?.state === 'preparing'}
        >
          {(options?.encoders ?? ['libx264']).map((value) => (
            <option key={value} value={value}>
              {{
                libx264: 'Software (default)',
                h264_nvenc: 'NVIDIA NVENC',
                h264_qsv: 'Intel QSV',
                h264_vaapi: 'Linux VAAPI (Intel/AMD)',
                h264_amf: 'AMD AMF',
              }[value] || value}
            </option>
          ))}
        </select>
      </label>
      <button
        disabled={busy || session?.state === 'preparing'}
        onClick={() => void convert()}
      >
        Prepare HLS playback
      </button>
      <button className="secondary" onClick={() => void direct()}>
        Direct Play
      </button>
      {session && (
        <p role="status">
          Conversion: {session.state} · {session.encoder}
          {session.state === 'ready'
            ? ` · ${session.encodeSeconds.toFixed(1)} seconds`
            : ''}
        </p>
      )}
      {!!levels.length && (
        <label>
          Playback quality
          <select
            value={quality}
            onChange={(e) => {
              const value = Number(e.target.value);
              setQuality(value);
              if (hls.current) hls.current.nextLevel = value;
            }}
          >
            <option value="-1">Auto (network aware)</option>
            {levels.map((label, index) => (
              <option key={index} value={index}>
                {label}
              </option>
            ))}
          </select>
        </label>
      )}
      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
    </details>
  );
}
