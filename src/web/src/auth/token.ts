const STORAGE_KEY = 'openagent-token';
let token: string | null = null;

/** Extract token from URL fragment (#token=xxx), persist to sessionStorage, and strip from URL. */
export function extractToken(): void {
  const hash = window.location.hash.slice(1);
  const params = new URLSearchParams(hash);
  const value = params.get('token');

  if (value) {
    token = value;
    sessionStorage.setItem(STORAGE_KEY, value);
    // Strip token from URL to avoid leaking in shared links / screenshots
    window.history.replaceState(null, '', window.location.pathname + window.location.search);
  } else {
    // Restore from sessionStorage if no hash token
    token = sessionStorage.getItem(STORAGE_KEY);
  }
}

export function getToken(): string | null {
  return token;
}
