export type SavedServer = { name: string; address: string };
const key = 'media-server.saved-servers.v1';
export function serverOrigin(input: string): string {
  if (input.length > 512) throw new Error('Server address is too long.');
  const url = new URL(input.trim());
  const local =
    url.hostname === 'localhost' ||
    url.hostname === '[::1]' ||
    /^127\.\d+\.\d+\.\d+$/.test(url.hostname);
  if (
    url.username ||
    url.password ||
    url.search ||
    url.hash ||
    url.pathname !== '/' ||
    (url.protocol !== 'https:' && !(url.protocol === 'http:' && local))
  )
    throw new Error(
      'Use an HTTPS server origin without credentials, path, query or fragment.',
    );
  return url.origin;
}
export function parseSavedServers(text: string | null): SavedServer[] {
  try {
    if (!text || text.length > 32768) return [];
    const values: unknown = JSON.parse(text);
    if (!Array.isArray(values)) return [];
    const found = new Map<string, SavedServer>();
    for (const value of values.slice(0, 20)) {
      try {
        if (
          !value ||
          typeof value.address !== 'string' ||
          typeof value.name !== 'string'
        )
          continue;
        const address = serverOrigin(value.address);
        found.set(address, {
          address,
          name: value.name.trim().slice(0, 80) || new URL(address).host,
        });
      } catch {
        /* Ignore invalid imported/local metadata; never connect automatically. */
      }
    }
    return [...found.values()];
  } catch {
    return [];
  }
}
export function readSavedServers(): SavedServer[] {
  try {
    return parseSavedServers(localStorage.getItem(key));
  } catch {
    return [];
  }
}
export function rememberServer(
  servers: SavedServer[],
  address: string,
  name: string,
): SavedServer[] {
  const origin = serverOrigin(address);
  return [
    { address: origin, name: name.trim().slice(0, 80) || new URL(origin).host },
    ...servers.filter((s) => s.address !== origin),
  ].slice(0, 20);
}
export function writeSavedServers(servers: SavedServer[]) {
  // Serialize only these public metadata fields, never session/response objects.
  localStorage.setItem(
    key,
    JSON.stringify(servers.map(({ name, address }) => ({ name, address }))),
  );
}
