import { useEffect, useState } from 'react';
import { listProviders } from './api';
import { AgentConfigForm } from './AgentConfigForm';
import { ConnectionsForm } from './ConnectionsForm';
import { ProviderForm } from './ProviderForm';
import { SystemPromptForm } from './SystemPromptForm';
import styles from './SettingsApp.module.css';

export function SettingsApp() {
  const [providers, setProviders] = useState<string[]>([]);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState<'agent' | 'providers' | 'prompt' | 'connections'>('agent');

  useEffect(() => {
    listProviders().then(setProviders).catch(() => {});
  }, []);

  const toggle = (key: string) => {
    setExpanded(prev => prev === key ? null : key);
  };

  // Filter out "agent" from provider list — it has its own tab
  const providerKeys = providers.filter(k => k !== 'agent');

  return (
    <div className={styles.settings}>
      <div className={styles.tabBar}>
        <button
          className={`${styles.mainTab} ${activeTab === 'agent' ? styles.activeTab : ''}`}
          onClick={() => setActiveTab('agent')}
        >
          Agent
        </button>
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

      {activeTab === 'agent' && (
        <div className={styles.providers}>
          <AgentConfigForm />
        </div>
      )}

      {activeTab === 'providers' && (
        <div className={styles.providers}>
          {providerKeys.length === 0 && (
            <p className={styles.placeholder}>No providers found.</p>
          )}
          {providerKeys.map(key => (
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
