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
  transcodingSupported: boolean;
};
const bytes = (value: number | null) =>
  value === null ? 'Unavailable' : `${(value / 1024 ** 3).toFixed(2)} GiB`;
export default function Monitoring() {
  const [status, setStatus] = useState<Status>();
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const next = await api<Status>('/monitoring');
        if (active) {
          setStatus(next);
          setError('');
        }
      } catch (e) {
        if (active) setError((e as Error).message);
      }
    }
    void load();
    const timer = setInterval(() => void load(), 5000);
    return () => {
      active = false;
      clearInterval(timer);
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
            <dd>{status.resources.gpuStatus}</dd>
            <dt>Transcoding</dt>
            <dd>Unavailable; Direct Play only</dd>
          </dl>
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
