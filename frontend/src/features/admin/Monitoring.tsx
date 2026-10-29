import { useEffect, useState } from 'react';
import { api } from '../../services/api';
type Status = {
  resources: {
    processCpuPercent: number | null;
    processMemoryBytes: number;
    managedMemoryBytes: number;
    uptimeSeconds: number;
    dataDiskAvailableBytes: number | null;
    dataDiskTotalBytes: number | null;
    gpuStatus: string;
  };
  activeHttpStreams: {
    id: string;
    userId: number;
    mediaId: number;
    bytesRead: number;
  }[];
  queuedScans: number;
  runningScans: number;
  gpu: {
    status: string;
    devices: {
      adapter: number;
      utilizationPercent: number | null;
      memoryUsedBytes: number | null;
      memoryTotalBytes: number | null;
    }[];
  };
  transcodingSupported: boolean;
  transcodingQueueCount: number;
  transcoding: {
    capacity: number;
    preparing: number;
    encoding: number;
    ready: number;
    failed: number;
    jobs: {
      mediaId: number;
      state: string;
      phase: string;
      encoder: string;
      rendition: string | null;
      completedRenditions: number;
      elapsedSeconds: number;
      progress: {
        frames: number;
        framesPerSecond: number | null;
        outputSeconds: number | null;
        speed: number | null;
      } | null;
    }[];
  };
};
const bytes = (value: number | null) =>
  value === null ? 'Unavailable' : `${(value / 1024 ** 3).toFixed(2)} GiB`;
export default function Monitoring() {
  const [status, setStatus] = useState<Status>();
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    let timer: ReturnType<typeof setTimeout>;
    async function load() {
      try {
        const next = await api<Status>('/monitoring');
        if (active) {
          setStatus(next);
          setError('');
        }
      } catch (e) {
        if (active) {
          setStatus(undefined);
          setError((e as Error).message);
        }
      } finally {
        if (active) timer = setTimeout(() => void load(), 5000);
      }
    }
    void load();
    return () => {
      active = false;
      clearTimeout(timer);
    };
  }, []);
  return (
    <section className="panel diagnostics">
      <h2>Server monitoring</h2>
      {error && <p role="alert">{error}</p>}
      {status ? (
        <>
          <dl>
            <dt>Server process CPU</dt>
            <dd>
              {status.resources.processCpuPercent === null
                ? 'Sampling…'
                : `${status.resources.processCpuPercent.toFixed(1)}%`}{' '}
              (normalized across available processors)
            </dd>
            <dt>Process / managed memory</dt>
            <dd>
              {bytes(status.resources.processMemoryBytes)} /{' '}
              {bytes(status.resources.managedMemoryBytes)}
            </dd>
            <dt>Data volume available / total</dt>
            <dd>
              {bytes(status.resources.dataDiskAvailableBytes)} /{' '}
              {bytes(status.resources.dataDiskTotalBytes)}
            </dd>
            <dt>Uptime</dt>
            <dd>{Math.floor(status.resources.uptimeSeconds / 60)} minutes</dd>
            <dt>Scans queued / running</dt>
            <dd>
              {status.queuedScans} / {status.runningScans}
            </dd>
            <dt>GPU</dt>
            <dd>{status.gpu.status}</dd>
            <dt>Conversion slots preparing / capacity</dt>
            <dd>
              {status.transcoding.preparing} / {status.transcoding.capacity}
            </dd>
            <dt>Encoding / ready / failed</dt>
            <dd>
              {status.transcoding.encoding} / {status.transcoding.ready} /{' '}
              {status.transcoding.failed}
            </dd>
            <dt>Waiting conversion queue</dt>
            <dd>
              {status.transcodingQueueCount} · excess jobs are rejected; no
              waiting queue
            </dd>
          </dl>
          {status.gpu.devices.map((device) => (
            <p key={device.adapter}>
              NVIDIA adapter #{device.adapter} ·{' '}
              {device.utilizationPercent === null
                ? 'Utilization unavailable'
                : `${device.utilizationPercent}% busy`}{' '}
              · VRAM {bytes(device.memoryUsedBytes)} /{' '}
              {bytes(device.memoryTotalBytes)}
            </p>
          ))}
          <h3>Conversion activity</h3>
          <p className="muted">
            Live FPS and speed come from the current FFmpeg rendition, not
            playback throughput. GPU samples are host-wide. Completed conversion
            cache entries are not active viewers.
          </p>
          {status.transcoding.jobs.length === 0 ? (
            <p>No conversion jobs.</p>
          ) : (
            <ul>
              {status.transcoding.jobs.map((job, index) => (
                <li key={`${job.mediaId}-${index}`}>
                  Media #{job.mediaId} · {job.phase} · {job.encoder} ·{' '}
                  {job.rendition || 'No rendition yet'} ·{' '}
                  {job.completedRenditions}/3 renditions verified ·{' '}
                  {job.elapsedSeconds.toFixed(1)} seconds
                  {job.progress && (
                    <>
                      {' '}
                      · {job.progress.frames} frames ·{' '}
                      {job.progress.framesPerSecond === null
                        ? 'FPS unavailable'
                        : `${job.progress.framesPerSecond.toFixed(1)} FPS`}{' '}
                      ·{' '}
                      {job.progress.speed === null
                        ? 'Speed unavailable'
                        : `${job.progress.speed.toFixed(2)}×`}
                    </>
                  )}
                </li>
              ))}
            </ul>
          )}
          <h3>Active HTTP streams</h3>
          <p className="muted">
            {status.activeHttpStreams.length} open transfer requests. A fully
            buffered video may keep playing after its HTTP transfer ends.
          </p>
          <ul>
            {status.activeHttpStreams.map((stream) => (
              <li key={stream.id}>
                User #{stream.userId} · Media #{stream.mediaId} ·{' '}
                {(stream.bytesRead / 1024 ** 2).toFixed(1)} MiB read
              </li>
            ))}
          </ul>
        </>
      ) : (
        <p>Loading server metrics…</p>
      )}
    </section>
  );
}
