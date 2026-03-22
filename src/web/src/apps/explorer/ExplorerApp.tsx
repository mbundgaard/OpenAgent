import { useCallback, useEffect, useRef, useState } from 'react';
import type { FileEntry } from './api';
import { listDirectory, downloadFile, renameFile, deleteFile, uploadFiles } from './api';
import { FileViewerApp } from './FileViewerApp';
import { ContextMenu } from './ContextMenu';
import type { MenuItem } from './ContextMenu';
import { useWindowContext } from '../../windows/WindowContext';
import styles from './ExplorerApp.module.css';

/** Format byte sizes for display. */
function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

interface ContextMenuState {
  x: number;
  y: number;
  entry: FileEntry;
}

export function ExplorerApp() {
  const [currentPath, setCurrentPath] = useState('');
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [contextMenu, setContextMenu] = useState<ContextMenuState | null>(null);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const { openDynamicWindow } = useWindowContext();
  const fileInputRef = useRef<HTMLInputElement>(null);

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

  const openViewer = (entry: FileEntry) => {
    openDynamicWindow({
      id: `file-viewer-${entry.path}`,
      title: entry.name,
      component: FileViewerApp as unknown as React.ComponentType<Record<string, unknown>>,
      componentProps: { filePath: entry.path },
      defaultSize: { width: 600, height: 500 },
    });
  };

  const handleDoubleClick = (entry: FileEntry) => {
    if (entry.isDirectory) {
      navigate(entry.path);
    } else {
      openViewer(entry);
    }
  };

  const handleContextMenu = (e: React.MouseEvent, entry: FileEntry) => {
    e.preventDefault();
    e.stopPropagation();
    setContextMenu({ x: e.clientX, y: e.clientY, entry });
  };

  const handleView = (entry: FileEntry) => {
    if (entry.isDirectory) {
      navigate(entry.path);
    } else {
      openViewer(entry);
    }
  };

  const handleDownload = async (entry: FileEntry) => {
    try {
      await downloadFile(entry.path);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Download failed');
    }
  };

  const startRename = (entry: FileEntry) => {
    setRenaming(entry.path);
    setRenameValue(entry.name);
  };

  const submitRename = async (entry: FileEntry) => {
    const newName = renameValue.trim();
    setRenaming(null);
    if (!newName || newName === entry.name) return;

    try {
      await renameFile(entry.path, newName);
      navigate(currentPath);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Rename failed');
    }
  };

  const handleDelete = async (entry: FileEntry) => {
    const label = entry.isDirectory ? 'directory' : 'file';
    if (!confirm(`Delete ${label} "${entry.name}"?`)) return;

    try {
      await deleteFile(entry.path);
      navigate(currentPath);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Delete failed');
    }
  };

  const handleUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files;
    if (!files || files.length === 0) return;
    try {
      await uploadFiles(currentPath, files);
      navigate(currentPath);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Upload failed');
    }
    // Reset input so the same file can be re-uploaded
    e.target.value = '';
  };

  const getMenuItems = (entry: FileEntry): MenuItem[] => {
    const items: MenuItem[] = [];

    items.push({
      label: entry.isDirectory ? 'Open' : 'View',
      action: () => handleView(entry),
    });

    if (!entry.isDirectory) {
      items.push({
        label: 'Download',
        action: () => handleDownload(entry),
      });
    }

    items.push({
      label: 'Rename',
      action: () => startRename(entry),
    });

    items.push({
      label: 'Delete',
      action: () => handleDelete(entry),
      danger: true,
    });

    return items;
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
        <button
          className={styles.toolbarButton}
          onClick={() => fileInputRef.current?.click()}
          title="Upload"
        >
          {'\u2B06'}
        </button>
        <input
          ref={fileInputRef}
          type="file"
          multiple
          style={{ display: 'none' }}
          onChange={handleUpload}
        />
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
            onContextMenu={(e) => handleContextMenu(e, entry)}
          >
            <span className={styles.colName}>
              <span className={styles.icon}>{entry.isDirectory ? '\uD83D\uDCC1' : '\uD83D\uDCC4'}</span>
              {renaming === entry.path ? (
                <input
                  className={styles.renameInput}
                  value={renameValue}
                  onChange={e => setRenameValue(e.target.value)}
                  onBlur={() => submitRename(entry)}
                  onKeyDown={e => {
                    if (e.key === 'Enter') submitRename(entry);
                    if (e.key === 'Escape') setRenaming(null);
                  }}
                  onClick={e => e.stopPropagation()}
                  onDoubleClick={e => e.stopPropagation()}
                  autoFocus
                />
              ) : (
                <span className={styles.fileName}>{entry.name}</span>
              )}
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

      {/* Context menu */}
      {contextMenu && (
        <ContextMenu
          x={contextMenu.x}
          y={contextMenu.y}
          items={getMenuItems(contextMenu.entry)}
          onClose={() => setContextMenu(null)}
        />
      )}
    </div>
  );
}
