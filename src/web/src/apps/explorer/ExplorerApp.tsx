import { useCallback, useEffect, useState } from 'react';
import type { FileEntry } from './api';
import { listDirectory } from './api';
import { FileViewerApp } from './FileViewerApp';
import { useWindowContext } from '../../windows/WindowContext';
import styles from './ExplorerApp.module.css';

/** Format byte sizes for display. */
function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

export function ExplorerApp() {
  const [currentPath, setCurrentPath] = useState('');
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const { openDynamicWindow } = useWindowContext();

  const navigate = useCallback((path: string) => {
    setCurrentPath(path);
    setLoading(true);
    setError(null);
    listDirectory(path)
      .then(setEntries)
      .catch(err => setError(err.message))
      .finally(() => setLoading(false));
  }, []);

  // Initial load
  useEffect(() => { navigate(''); }, [navigate]);

  // Build breadcrumb segments from the current path
  const segments = currentPath ? currentPath.split('/') : [];

  const handleBreadcrumb = (index: number) => {
    if (index < 0) {
      navigate('');
    } else {
      navigate(segments.slice(0, index + 1).join('/'));
    }
  };

  const handleDoubleClick = (entry: FileEntry) => {
    if (entry.isDirectory) {
      navigate(entry.path);
    } else {
      openDynamicWindow({
        id: `file-viewer-${entry.path}`,
        title: entry.name,
        component: FileViewerApp as unknown as React.ComponentType<Record<string, unknown>>,
        componentProps: { filePath: entry.path },
        defaultSize: { width: 600, height: 500 },
      });
    }
  };

  return (
    <div className={styles.container}>
      {/* Toolbar */}
      <div className={styles.toolbar}>
        <button
          className={styles.toolbarButton}
          onClick={() => {
            if (segments.length > 0) {
              navigate(segments.slice(0, -1).join('/'));
            }
          }}
          disabled={currentPath === ''}
          title="Go up"
        >
          {'\u2191'}
        </button>
        <button
          className={styles.toolbarButton}
          onClick={() => navigate(currentPath)}
          title="Refresh"
        >
          {'\u21BB'}
        </button>
        <div className={styles.breadcrumbs}>
          <button
            className={`${styles.breadcrumb} ${currentPath === '' ? styles.breadcrumbActive : ''}`}
            onClick={() => handleBreadcrumb(-1)}
          >
            /data
          </button>
          {segments.map((segment, i) => (
            <span key={i}>
              <span className={styles.breadcrumbSep}>/</span>
              <button
                className={`${styles.breadcrumb} ${i === segments.length - 1 ? styles.breadcrumbActive : ''}`}
                onClick={() => handleBreadcrumb(i)}
              >
                {segment}
              </button>
            </span>
          ))}
        </div>
      </div>

      {/* File list */}
      <div className={styles.fileList}>
        {/* Column headers */}
        <div className={styles.headerRow}>
          <span className={styles.colName}>Name</span>
          <span className={styles.colSize}>Size</span>
          <span className={styles.colDate}>Modified</span>
        </div>

        {loading && <div className={styles.muted}>Loading...</div>}
        {error && <div className={styles.error}>{error}</div>}
        {!loading && !error && entries.length === 0 && (
          <div className={styles.muted}>Empty directory</div>
        )}

        {!loading && !error && entries.map(entry => (
          <button
            key={entry.path}
            className={styles.fileRow}
            onDoubleClick={() => handleDoubleClick(entry)}
          >
            <span className={styles.colName}>
              <span className={styles.icon}>{entry.isDirectory ? '\uD83D\uDCC1' : '\uD83D\uDCC4'}</span>
              <span className={styles.fileName}>{entry.name}</span>
            </span>
            <span className={styles.colSize}>
              {entry.isDirectory ? '--' : formatSize(entry.size ?? 0)}
            </span>
            <span className={styles.colDate}>
              {new Date(entry.modifiedAt).toLocaleDateString()}
            </span>
          </button>
        ))}
      </div>
    </div>
  );
}
