import { useEffect, useState } from 'react';
import { readFile } from './api';
import styles from './FileViewerApp.module.css';

interface Props {
  filePath: string;
}

/** Read-only text file viewer — opened in its own window from the Explorer. */
export function FileViewerApp({ filePath }: Props) {
  const [content, setContent] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    setError(null);
    readFile(filePath)
      .then(file => setContent(file.content))
      .catch(err => setError(err.message))
      .finally(() => setLoading(false));
  }, [filePath]);

  if (loading) return <div className={styles.status}>Loading...</div>;
  if (error) return <div className={styles.error}>{error}</div>;

  return (
    <div className={styles.container}>
      <pre className={styles.content}>{content}</pre>
    </div>
  );
}
