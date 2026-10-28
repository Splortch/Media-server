import { invoke } from '@tauri-apps/api/core';
import {
  ClientApi,
  serverInfo,
  type Transport,
} from '../../../shared/api/src/client';
export const transport: Transport = {
  request: <T>(method: string, path: string, body?: unknown) =>
    invoke<T>('api_request', { method, path, body: body ?? null }),
  streamUrl: (id: number) => invoke<string>('stream_url', { id }),
};
export const api = new ClientApi(transport);
export async function connect(address: string, remember: boolean) {
  return serverInfo(await invoke('connect', { address, remember }));
}
export async function login(name: string, password: string) {
  await invoke('login', { name, password });
  return api.me();
}
export async function logout() {
  return invoke('logout');
}
export type Preferences = {
  address?: string;
  theme?: 'system' | 'dark' | 'light';
};
export function preferences() {
  return invoke<Preferences>('preferences');
}
export function savePreferences(address: string, theme: string) {
  return invoke('save_preferences', { address, theme });
}
export function errorMessage(e: unknown) {
  if (
    e &&
    typeof e === 'object' &&
    'message' in e &&
    typeof e.message === 'string'
  )
    return e.message.slice(0, 500);
  return 'Operation failed. Check the server connection.';
}
export function expired(e: unknown) {
  return !!e && typeof e === 'object' && 'code' in e && e.code === 'AUTH';
}

export function desktopCapabilities() {
  return invoke<{ os: string; persistentSessions: boolean }>(
    'desktop_capabilities',
  );
}
