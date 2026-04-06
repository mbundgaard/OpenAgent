import { useCallback, useEffect, useState } from 'react';
import type { ConnectionInfo, ChannelTypeInfo, ConfigField } from './api';
import {
  listConnections,
  deleteConnection,
  startConnection,
  stopConnection,
  getConnectionTypes,
  createConnection,
  updateConnection,
} from './api';
import { QrCodeDisplay } from './QrCodeDisplay';
import styles from './ConnectionsForm.module.css';

// --- New Connection Flow ---

type FlowState =
  | { step: 'idle' }
  | { step: 'pick-type'; types: ChannelTypeInfo[] }
  | { step: 'configure'; channelType: ChannelTypeInfo }
  | { step: 'setup'; connectionId: string; channelType: ChannelTypeInfo }
  | { step: 'done' };

interface NewConnectionFlowProps {
  onCreated: () => void;
}

function NewConnectionFlow({ onCreated }: NewConnectionFlowProps) {
  const [flow, setFlow] = useState<FlowState>({ step: 'idle' });
  const [name, setName] = useState('');
  const [configValues, setConfigValues] = useState<Record<string, string>>({});
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loadingTypes, setLoadingTypes] = useState(false);

  const reset = () => {
    setFlow({ step: 'idle' });
    setName('');
    setConfigValues({});
    setError(null);
    setCreating(false);
  };

  const handleNewClick = async () => {
    setLoadingTypes(true);
    setError(null);
    try {
      const types = await getConnectionTypes();
      setFlow({ step: 'pick-type', types });
    } catch {
      setError('Failed to load connection types');
    }
    setLoadingTypes(false);
  };

  const handlePickType = (channelType: ChannelTypeInfo) => {
    // Initialize config values with defaults
    const initial: Record<string, string> = {};
    for (const field of channelType.configFields) {
      initial[field.key] = field.default_value ?? '';
    }
    setConfigValues(initial);
    setName('');
    setError(null);
    setFlow({ step: 'configure', channelType });
  };

  const handleCreate = async (channelType: ChannelTypeInfo) => {
    if (!name.trim()) {
      setError('Name is required');
      return;
    }
    setCreating(true);
    setError(null);
    try {
      const conn = await createConnection({
        name: name.trim(),
        type: channelType.type,
        enabled: true,
        config: configValues,
      });

      // If setup step exists, go to setup; otherwise we're done
      if (channelType.setupStep) {
        setFlow({ step: 'setup', connectionId: conn.id, channelType });
      } else {
        reset();
        onCreated();
      }
    } catch {
      setError('Failed to create connection');
    }
    setCreating(false);
  };

  const handleSetupDone = () => {
    reset();
    onCreated();
  };

  // Idle — just show the button
  if (flow.step === 'idle') {
    return (
      <button className={styles.newButton} onClick={handleNewClick} disabled={loadingTypes}>
        {loadingTypes ? 'Loading...' : '+ New Connection'}
      </button>
    );
  }

  // Pick type
  if (flow.step === 'pick-type') {
    return (
      <div className={styles.flowSection}>
        <div className={styles.flowHeader}>
          <h3 className={styles.flowTitle}>Choose connection type</h3>
          <button className={styles.cancelButton} onClick={reset}>Cancel</button>
        </div>
        <div className={styles.typePicker}>
          {flow.types.map(t => (
            <button key={t.type} className={styles.typeCard} onClick={() => handlePickType(t)}>
              {t.displayName}
            </button>
          ))}
        </div>
        {error && <p className={styles.flowError}>{error}</p>}
      </div>
    );
  }

  // Configure
  if (flow.step === 'configure') {
    const { channelType } = flow;
    return (
      <div className={styles.flowSection}>
        <div className={styles.flowHeader}>
          <h3 className={styles.flowTitle}>Configure {channelType.displayName}</h3>
          <button className={styles.cancelButton} onClick={reset}>Cancel</button>
        </div>
        <div className={styles.configForm}>
          {/* Name field — always present */}
          <label className={styles.field}>
            <span className={styles.fieldLabel}>
              Name<span className={styles.required}>*</span>
            </span>
            <input
              className={styles.fieldInput}
              type="text"
              value={name}
              placeholder="My connection"
              onChange={e => setName(e.target.value)}
            />
          </label>

          {/* Dynamic config fields */}
          {channelType.configFields.map(field => (
            <ConfigFieldInput
              key={field.key}
              field={field}
              value={configValues[field.key] ?? ''}
              onChange={val => setConfigValues(v => ({ ...v, [field.key]: val }))}
            />
          ))}

          <button
            className={styles.createButton}
            onClick={() => handleCreate(channelType)}
            disabled={creating}
          >
            {creating ? 'Creating...' : 'Create'}
          </button>
          {error && <p className={styles.flowError}>{error}</p>}
        </div>
      </div>
    );
  }

  // Setup (QR code, etc.)
  if (flow.step === 'setup') {
    const { connectionId, channelType } = flow;
    return (
      <div className={styles.flowSection}>
        <div className={styles.flowHeader}>
          <h3 className={styles.flowTitle}>Setup {channelType.displayName}</h3>
          <button className={styles.cancelButton} onClick={handleSetupDone}>Skip</button>
        </div>
        {channelType.setupStep?.type === 'qr-code' ? (
          <QrCodeDisplay connectionId={connectionId} onConnected={handleSetupDone} />
        ) : (
          <p className={styles.muted}>No additional setup required.</p>
        )}
      </div>
    );
  }

  return null;
}

// --- Config field renderer (mirrors ProviderForm pattern) ---

interface ConfigFieldInputProps {
  field: ConfigField;
  value: string;
  onChange: (val: string) => void;
}

function ConfigFieldInput({ field, value, onChange }: ConfigFieldInputProps) {
  return (
    <label className={styles.field}>
      <span className={styles.fieldLabel}>
        {field.label}
        {field.required && <span className={styles.required}>*</span>}
      </span>
      {field.type === 'Enum' && field.options ? (
        <select
          className={styles.fieldInput}
          value={value}
          onChange={e => onChange(e.target.value)}
        >
          {field.options.map(opt => (
            <option key={opt} value={opt}>{opt}</option>
          ))}
        </select>
      ) : (
        <input
          className={styles.fieldInput}
          type={field.type === 'Secret' ? 'password' : 'text'}
          value={value}
          placeholder={field.default_value ?? ''}
          onChange={e => onChange(e.target.value)}
        />
      )}
    </label>
  );
}

// --- Main ConnectionsForm ---

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

  return (
    <div className={styles.list}>
      {/* New connection flow — always at top */}
      <NewConnectionFlow onCreated={refresh} />

      {/* Existing connections */}
      {loading && <p className={styles.muted}>Loading...</p>}
      {!loading && connections.length === 0 && (
        <p className={styles.muted}>No connections configured.</p>
      )}
      {connections.map(conn => (
        <div key={conn.id} className={styles.connection}>
          <div className={styles.header} onClick={() => setExpanded(e => e === conn.id ? null : conn.id)}>
            <div className={styles.headerLeft}>
              <span className={`${styles.status} ${conn.status === 'running' ? styles.running : styles.stopped}`} />
              <span className={styles.name}>{conn.name}</span>
              <span className={styles.type}>{conn.type}</span>
            </div>
            <div className={styles.headerRight}>
              {!conn.allowNewConversations && (
                <button
                  className={styles.actionButton}
                  onClick={async e => { e.stopPropagation(); await updateConnection(conn.id, { allowNewConversations: true }); refresh(); }}
                  title="Allow new conversations from this connection"
                >
                  Unlock
                </button>
              )}
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
