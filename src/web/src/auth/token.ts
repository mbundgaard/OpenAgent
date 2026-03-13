let token: string | null = null;

/** Extract token from URL fragment (#token=xxx) and strip it from the URL. */
export function extractToken(): void {
  const hash = window.location.hash.slice(1);
  const params = new URLSearchParams(hash);
  const value = params.get('token');

  if (value) {
    token = value;
    // Strip token from URL to avoid leaking in shared links / screenshots
    window.history.replaceState(null, '', window.location.pathname + window.location.search);
  }
}

export function getToken(): string | null {
  return token;
}
