import { useState, type FormEvent } from 'react';
import type { components } from '../../../shared/api/src/generated';

type Matches = components['schemas']['TimeBudgetPage'];

export default function TimeBudgetPicker({
  request,
  onOpen,
}: {
  request: (path: string) => Promise<Matches>;
  onOpen: (id: number) => void;
}) {
  const [choice, setChoice] = useState('30');
  const [custom, setCustom] = useState('');
  const [speed, setSpeed] = useState('1');
  const [includeStarted, setIncludeStarted] = useState(true);
  const [matches, setMatches] = useState<Matches>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(false);

  async function find(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (loading) return;
    const minutes = Number(choice === 'custom' ? custom : choice);
    if (!Number.isInteger(minutes) || minutes < 1 || minutes > 720) return;
    setLoading(true);
    setMatches(undefined);
    setError(false);
    const query = new URLSearchParams({
      minutes: String(minutes),
      speed,
      includeStarted: String(includeStarted),
    });
    try {
      setMatches(await request('/time-budget?' + query));
    } catch {
      setError(true);
    } finally {
      setLoading(false);
    }
  }

  return (
    <section className="panel">
      <h2>I have…</h2>
      <form onSubmit={find} aria-busy={loading}>
        <div className="time-budget-fields">
          <label>
            Time available
            <select
              value={choice}
              onChange={(event) => setChoice(event.target.value)}
              disabled={loading}
            >
              <option value="30">30 minutes</option>
              <option value="45">45 minutes</option>
              <option value="60">1 hour</option>
              <option value="90">1 hour 30 minutes</option>
              <option value="custom">Custom</option>
            </select>
          </label>
          {choice === 'custom' && (
            <label>
              Minutes
              <input
                type="number"
                min={1}
                max={720}
                required
                value={custom}
                onChange={(event) => setCustom(event.target.value)}
                disabled={loading}
              />
            </label>
          )}
          <label>
            Playback speed estimate
            <select
              value={speed}
              onChange={(event) => setSpeed(event.target.value)}
              disabled={loading}
            >
              {[0.5, 0.75, 1, 1.25, 1.5, 1.75, 2].map((rate) => (
                <option key={rate} value={rate}>
                  {rate}×
                </option>
              ))}
            </select>
          </label>
        </div>
        <label>
          <input
            type="checkbox"
            checked={includeStarted}
            onChange={(event) => setIncludeStarted(event.target.checked)}
            disabled={loading}
          />
          Include titles I’ve started
        </label>
        <button type="submit" disabled={loading}>
          Find something to watch
        </button>
      </form>
      {loading && <p role="status">Finding titles…</p>}
      {error && <p role="alert">Could not find titles. Try again.</p>}
      {matches && (
        <>
          <p role="status">
            {matches.items.length === 0
              ? 'No unwatched titles fit the selected time.'
              : matches.hasMore
                ? `Showing the ${matches.items.length} closest fits.`
                : `${matches.items.length} ${matches.items.length === 1 ? 'title fits' : 'titles fit'}.`}
          </p>
          {matches.items.length > 0 && (
            <ul className="time-budget-results">
              {matches.items.map((item) => (
                <li key={item.id}>
                  <button
                    type="button"
                    className="secondary"
                    onClick={() => onOpen(item.id)}
                  >
                    {item.series
                      ? `${item.series} · S${item.season} E${item.episode} · ${item.title}`
                      : item.title}
                  </button>
                  <span className="muted">
                    About {Math.ceil(item.remainingSeconds / 60)} min left
                    {item.started ? ' · In progress' : ''}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  );
}
