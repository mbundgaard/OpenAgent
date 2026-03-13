import { getToken } from './token';

/** Fetch wrapper that injects the X-Api-Key header. */
export async function apiFetch(path: string, options: RequestInit = {}): Promise<Response> {
  const token = getToken();
  const headers = new Headers(options.headers);

  if (token) {
    headers.set('X-Api-Key', token);
  }

  return fetch(path, { ...options, headers });
}
