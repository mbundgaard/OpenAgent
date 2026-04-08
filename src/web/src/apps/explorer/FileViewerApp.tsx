import { useCallback, useEffect, useState } from 'react';
import Markdown from 'react-markdown';
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

  const load = useCallback(() => {
    setLoading(true);
    setError(null);
    readFile(filePath)
      .then(file => setContent(file.content))
      .catch(err => setError(err.message))
      .finally(() => setLoading(false));
  }, [filePath]);

  useEffect(() => { load(); }, [load]);

  return (
    <div className={styles.container}>
      <div className={styles.toolbar}>
        <span className={styles.path}>{filePath}</span>
        <button className={styles.refreshButton} onClick={load} title="Refresh">{'\u21BB'}</button>
      </div>
      {loading && <div className={styles.status}>Loading...</div>}
      {error && <div className={styles.error}>{error}</div>}
      {!loading && !error && (
        filePath.endsWith('.md') ? (
          <div className={styles.markdown}><Markdown>{content ?? ''}</Markdown></div>
        ) : (
          <div className={styles.codeView}>
            <pre className={styles.lineNumbers} aria-hidden="true">
              {content?.split('\n').map((_, i) => <span key={i}>{i + 1}</span>)}
            </pre>
            <pre className={styles.content}>{content}</pre>
          </div>
        )
      )}
    </div>
  );
}
