import { useEffect, useState, type FormEvent } from 'react';
import { api } from '../../services/api';
export type CatalogChoice = {
  provider: string;
  id: string;
  title: string;
  url: string;
  bytes: number;
  license: string;
  licenseUrl: string;
  sourcePage: string;
  attribution: string;
  rightsEvidence: string;
  approvalToken: string;
};
export function CatalogEvidence({ item }: { item: CatalogChoice }) {
  return (
    <div className="notice">
      <p>{item.attribution}</p>
      <p>
        <a href={item.sourcePage} target="_blank" rel="noreferrer">
          Source record
        </a>
        {' · '}
        <a href={item.licenseUrl} target="_blank" rel="noreferrer">
          {item.license}
        </a>
        {' · '}
        <a href={item.rightsEvidence} target="_blank" rel="noreferrer">
          Operator-reviewed rights evidence
        </a>
      </p>
      <p>
        Review source rights and destination before approving. Selection alone
        never starts a download.
      </p>
    </div>
  );
}
export default function CatalogPicker({
  onSelect,
}: {
  onSelect: (item: CatalogChoice) => void;
}) {
  const [enabled, setEnabled] = useState(false);
  const [query, setQuery] = useState('');
  const [items, setItems] = useState<CatalogChoice[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    void api<{ enabled: boolean }>('/downloads/catalog/capabilities')
      .then((value) => {
        if (active) setEnabled(value.enabled === true);
      })
      .catch(() => {});
    return () => {
      active = false;
    };
  }, []);
  if (!enabled) return null;
  async function search(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError('');
    try {
      setItems(
        await api<CatalogChoice[]>(
          '/downloads/catalog?query=' + encodeURIComponent(query),
        ),
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="panel" aria-label="Reviewed CC0 catalog">
      <h2>Reviewed CC0 catalog</h2>
      <p>
        Wikimedia Commons files reviewed by your operator. Search is limited to
        that approved selection; it does not certify other uploads.
      </p>
      <form onSubmit={search} aria-busy={busy}>
        <label>
          Search reviewed catalog
          <input
            maxLength={80}
            value={query}
            onChange={(event) => setQuery(event.target.value)}
          />
        </label>
        <button disabled={busy}>
          {busy ? 'Searching…' : 'Search catalog'}
        </button>
      </form>
      {error && <p role="alert">{error}</p>}
      {items.map((item) => (
        <article key={item.provider + item.id}>
          <h3>{item.title}</h3>
          <CatalogEvidence item={item} />
          <p>{(item.bytes / 1024 / 1024).toFixed(1)} MiB</p>
          <button onClick={() => onSelect(item)} type="button">
            Use this source for approval
          </button>
        </article>
      ))}
    </section>
  );
}
