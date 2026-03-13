import { useEffect, useState } from 'react';
import { extractToken, getToken } from './auth/token';
import { Desktop } from './desktop/Desktop';

export default function App() {
  const [ready, setReady] = useState(false);

  useEffect(() => {
    extractToken();
    setReady(true);
  }, []);

  if (!ready) return null;

  if (!getToken()) {
    return (
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100vh', color: 'var(--color-text-muted)' }}>
        <p>Access denied — token required. Use <code>#token=your-api-key</code> in the URL.</p>
      </div>
    );
  }

  return <Desktop />;
}
