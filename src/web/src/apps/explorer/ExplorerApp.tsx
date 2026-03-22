import { useCallback, useEffect, useState } from 'react';
import type { FileEntry, FileContent } from './api';
import { listDirectory, readFile } from './api';
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

  // Viewer state
  const [viewerFile, setViewerFile] = useState<FileContent | null>(null);
  const [viewerLoading, setViewerLoading] = useState(false);
  const [viewerError, setViewerError] = useState<string | null>(null);

  const navigate = useCallback((path: string) => {
    setCurrentPath(path);
    setLoading(true);
    setError(null);
    listDirectory(path)
      .then(setEntries)
      .catch(err => setError(err.message))
      .finally(() => setLoading(false));
  }, []);

  const openFile = useCallback((path: string) => {
    setViewerLoading(true);
    setViewerError(null);
    readFile(path)
      .then(setViewerFile)
      .catch(err => {
        setViewerError(err.message);
        setViewerFile(null);
      })
      .finally(() => setViewerLoading(false));
  }, []);

  const closeViewer = useCallback(() => {
    setViewerFile(null);
    setViewerError(null);
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
      openFile(entry.path);
    }
  };

  const showViewer = viewerFile !== null || viewerLoading || viewerError !== null;

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

      {/* Main area — file list + optional viewer */}
      <div className={styles.mainArea}>
        {/* File list */}
        <div className={`${styles.fileList} ${showViewer ? styles.fileListNarrow : ''}`}>
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
              className={`${styles.fileRow} ${viewerFile?.path === entry.path ? styles.fileRowSelected : ''}`}
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

        {/* Viewer panel */}
        {showViewer && (
          <div className={styles.viewer}>
            <div className={styles.viewerHeader}>
              <span className={styles.viewerTitle}>{viewerFile?.name ?? 'Loading...'}</span>
              <button className={styles.viewerClose} onClick={closeViewer} title="Close">{'\u2715'}</button>
            </div>
            <div className={styles.viewerContent}>
              {viewerLoading && <div className={styles.muted}>Loading...</div>}
              {viewerError && <div className={styles.error}>{viewerError}</div>}
              {viewerFile && <pre className={styles.viewerText}>{viewerFile.content}</pre>}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
