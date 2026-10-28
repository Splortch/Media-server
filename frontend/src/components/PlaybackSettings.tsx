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
export default function PlaybackSettings({
  id,
  video,
  source,
  onSource,
  onCaption,
}: {
  id: number;
  video: RefObject<HTMLVideoElement | null>;
  source: string;
  onSource: (value: string) => void;
  onCaption: (value: string) => void;
}) {
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
  const hls = useRef<Hls | null>(null);
  const current = useRef<string | undefined>(undefined);
  const mounted = useRef(true);
  useEffect(() => {
    if (!opened) return;
    let active = true;
    api<Options>(`/v1/media/${id}/playback-options`)
      .then((value) => {
        if (active) setOptions(value);
      })
      .catch(() => {});
    return () => {
      active = false;
    };
  }, [id, opened]);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      if (current.current)
        void api(`/v1/playback-sessions/${current.current}`, 'DELETE').catch(
          () => {},
        );
    };
  }, [id]);
  const resume = useRef(0);
  useEffect(() => {
    const element = video.current;
    if (!element || !source.endsWith('.m3u8')) return;
    const position = resume.current;
    const playing = !element.paused;
    if (Hls.isSupported()) {
      const engine = new Hls({
        enableWorker: false,
        xhrSetup: (xhr) => {
          xhr.withCredentials = true;
        },
      });
      hls.current = engine;
      engine.loadSource(source);
      engine.attachMedia(element);
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
        if (data.fatal)
          setError(
            'HLS playback failed. Check the server connection and sign in again if your session expired.',
          );
      });
      return () => {
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
  }, [source, video]);
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
    setBusy(true);
    setError('');
    try {
      if (current.current)
        await api(`/v1/playback-sessions/${current.current}`, 'DELETE');
      const created = await api<Session>(
        `/v1/media/${id}/playback-sessions`,
        'POST',
        {
          audioTrack: audio === '' ? null : Number(audio),
          subtitleTrack: subtitle === '' ? null : Number(subtitle),
          encoder,
        },
      );
      current.current = created.id;
      if (!mounted.current) {
        await api(`/v1/playback-sessions/${created.id}`, 'DELETE');
        return;
      }
      setSession(created);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  useEffect(() => {
    if (session?.state !== 'preparing') return;
    const timer = setTimeout(() => {
      api<Session>(`/v1/playback-sessions/${session.id}`)
        .then((next) => {
          setSession(next);
          if (next.state === 'ready') {
            resume.current = video.current?.currentTime ?? 0;
            onSource(next.manifest);
            onCaption(
              subtitle === ''
                ? ''
                : `/api/v1/playback-sessions/${next.id}/files/captions.vtt`,
            );
          }
          if (next.state === 'failed')
            setError(next.error || 'Conversion failed.');
        })
        .catch((e) => setError((e as Error).message));
    }, 1500);
    return () => clearTimeout(timer);
  }, [session, onSource, onCaption, subtitle, video]);
  async function direct() {
    setSession(undefined);
    const previous = current.current;
    current.current = undefined;
    hls.current?.destroy();
    hls.current = null;
    onSource(`/api/media/${id}/stream`);
    onCaption('');
    setLevels([]);
    setQuality(-1);
    if (previous)
      await api(`/v1/playback-sessions/${previous}`, 'DELETE').catch((e) =>
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
          value={subtitle}
          onChange={(e) => setSubtitle(e.target.value)}
          disabled={busy || session?.state === 'preparing'}
        >
          <option value="">Off</option>
          {options?.externalWebVtt && (
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
              if (hls.current) hls.current.currentLevel = value;
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
