export type Media = {
  id: number;
  title: string;
  duration: number;
  container: string;
  videoCodec: string;
  audioCodec: string;
  size: number;
  playback?: { mode: string; mimeType: string; warning?: string };
};
export type Progress = {
  mediaItemId: number;
  seconds: number;
  updatedAt: string;
};
export type Library = { id: number; name: string; path: string };
let csrf: string | undefined;
export function resetCsrf() {
  csrf = undefined;
}
export async function api<T>(
  path: string,
  method = 'GET',
  body?: unknown,
): Promise<T> {
  const headers: Record<string, string> = {};
  if (method !== 'GET') {
    if (!csrf) {
      const response = await fetch('/api/auth/csrf', {
        credentials: 'same-origin',
      });
      if (!response.ok) throw new Error('Cannot establish a secure session.');
      csrf = ((await response.json()) as { token: string }).token;
    }
    headers['X-CSRF-TOKEN'] = csrf!;
    headers['Content-Type'] = 'application/json';
  }
  const response = await fetch('/api' + path, {
    method,
    credentials: 'same-origin',
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    const problem = (await response.json().catch(() => ({}))) as {
      detail?: string;
      message?: string;
    };
    if (response.status === 401 && path !== '/auth/login')
      window.dispatchEvent(new Event('session-expired'));
    throw new Error(
      problem.detail ||
        problem.message ||
        {
          401: 'Sign-in failed or your session expired.',
          403: 'You do not have permission.',
          429: 'Too many attempts. Wait a minute and retry.',
        }[response.status] ||
        `Server returned ${response.status}.`,
    );
  }
  return response.status === 204
    ? (undefined as T)
    : (response.json() as Promise<T>);
}
export function duration(seconds: number) {
  if (!seconds) return 'Duration unknown';
  if (seconds < 60) return `${Math.floor(seconds)} sec`;
  const m = Math.floor(seconds / 60);
  return m >= 60 ? `${Math.floor(m / 60)}h ${m % 60}m` : `${m} min`;
}
