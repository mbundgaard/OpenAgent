const STORAGE_KEY = 'openagent-token';
let token: string | null = null;

/** Extract token from URL query (?token=xxx), hash (#token=xxx), or sessionStorage. */
export function extractToken(): void {
  // Try query string first: ?token=xxx
  const queryParams = new URLSearchParams(window.location.search);
  const queryValue = queryParams.get('token');

  // Then hash fragment: #token=xxx
  const hash = window.location.hash.slice(1);
  const hashParams = new URLSearchParams(hash);
  const hashValue = hashParams.get('token');

  const value = queryValue ?? hashValue;

  if (value) {
    token = value;
    sessionStorage.setItem(STORAGE_KEY, value);
    // Strip hash token from URL but keep query string (needed for PWA)
    if (hashValue && !queryValue) {
      window.history.replaceState(null, '', window.location.pathname + window.location.search);
    }
  } else {
    // Restore from sessionStorage if no token in URL
    token = sessionStorage.getItem(STORAGE_KEY);
  }
}

export function getToken(): string | null {
  return token;
}
