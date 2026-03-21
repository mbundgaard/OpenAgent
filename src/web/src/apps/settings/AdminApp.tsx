import { useEffect, useState } from 'react';
import { listProviders } from './api';
import { ConnectionsForm } from './ConnectionsForm';
import { ProviderForm } from './ProviderForm';
import { SystemPromptForm } from './SystemPromptForm';
import styles from './AdminApp.module.css';

export function AdminApp() {
  const [providers, setProviders] = useState<string[]>([]);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState<'providers' | 'prompt' | 'connections'>('providers');

  useEffect(() => {
    listProviders().then(setProviders).catch(() => {});
  }, []);

  const toggle = (key: string) => {
    setExpanded(prev => prev === key ? null : key);
  };

  return (
    <div className={styles.admin}>
      <div className={styles.tabBar}>
        <button
          className={`${styles.mainTab} ${activeTab === 'providers' ? styles.activeTab : ''}`}
          onClick={() => setActiveTab('providers')}
        >
          Providers
        </button>
        <button
          className={`${styles.mainTab} ${activeTab === 'prompt' ? styles.activeTab : ''}`}
          onClick={() => setActiveTab('prompt')}
        >
          System Prompt
        </button>
        <button
          className={`${styles.mainTab} ${activeTab === 'connections' ? styles.activeTab : ''}`}
          onClick={() => setActiveTab('connections')}
        >
          Connections
        </button>
      </div>

      {activeTab === 'providers' && (
        <div className={styles.providers}>
          {providers.length === 0 && (
            <p className={styles.placeholder}>No providers found.</p>
          )}
          {providers.map(key => (
            <div key={key} className={styles.provider}>
              <button className={styles.providerHeader} onClick={() => toggle(key)}>
                <span className={styles.chevron}>{expanded === key ? '\u25BC' : '\u25B6'}</span>
                <span>{key}</span>
              </button>
              {expanded === key && (
                <div className={styles.providerBody}>
                  <ProviderForm providerKey={key} />
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      {activeTab === 'prompt' && (
        <div className={styles.promptSection}>
          <SystemPromptForm />
        </div>
      )}

      {activeTab === 'connections' && (
        <div className={styles.providers}>
          <ConnectionsForm />
        </div>
      )}
    </div>
  );
}
