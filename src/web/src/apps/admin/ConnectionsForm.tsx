import { useCallback, useEffect, useState } from 'react';
import type { ConnectionInfo } from './api';
import { listConnections, deleteConnection, startConnection, stopConnection } from './api';
import styles from './ConnectionsForm.module.css';

export function ConnectionsForm() {
  const [connections, setConnections] = useState<ConnectionInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [expanded, setExpanded] = useState<string | null>(null);

  const refresh = useCallback(() => {
    setLoading(true);
    listConnections()
      .then(setConnections)
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  const handleToggle = async (conn: ConnectionInfo) => {
    if (conn.status === 'running') {
      await stopConnection(conn.id);
    } else {
      await startConnection(conn.id);
    }
    refresh();
  };

  const handleDelete = async (id: string) => {
    await deleteConnection(id);
    if (expanded === id) setExpanded(null);
    refresh();
  };

  if (loading) return <p className={styles.muted}>Loading...</p>;
  if (connections.length === 0) return <p className={styles.muted}>No connections configured.</p>;

  return (
    <div className={styles.list}>
      {connections.map(conn => (
        <div key={conn.id} className={styles.connection}>
          <div className={styles.header} onClick={() => setExpanded(e => e === conn.id ? null : conn.id)}>
            <div className={styles.headerLeft}>
              <span className={`${styles.status} ${conn.status === 'running' ? styles.running : styles.stopped}`} />
              <span className={styles.name}>{conn.name}</span>
              <span className={styles.type}>{conn.type}</span>
            </div>
            <div className={styles.headerRight}>
              <button
                className={`${styles.actionButton} ${conn.status === 'running' ? styles.stopButton : styles.startButton}`}
                onClick={e => { e.stopPropagation(); handleToggle(conn); }}
              >
                {conn.status === 'running' ? 'Stop' : 'Start'}
              </button>
              <button
                className={styles.deleteButton}
                onClick={e => { e.stopPropagation(); handleDelete(conn.id); }}
              >
                Delete
              </button>
            </div>
          </div>
          {expanded === conn.id && (
            <div className={styles.details}>
              <div className={styles.detailRow}>
                <span className={styles.detailLabel}>ID</span>
                <span className={styles.detailValue}>{conn.id}</span>
              </div>
              <div className={styles.detailRow}>
                <span className={styles.detailLabel}>Conversation</span>
                <span className={styles.detailValue}>{conn.conversationId}</span>
              </div>
              <div className={styles.detailRow}>
                <span className={styles.detailLabel}>Enabled</span>
                <span className={styles.detailValue}>{conn.enabled ? 'Yes' : 'No'}</span>
              </div>
              <div className={styles.detailRow}>
                <span className={styles.detailLabel}>Config</span>
                <pre className={styles.configPre}>{JSON.stringify(conn.config, null, 2)}</pre>
              </div>
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
