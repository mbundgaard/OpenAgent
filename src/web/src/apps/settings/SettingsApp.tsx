import { useEffect, useState } from 'react';
import { listProviders } from './api';
import { AgentConfigForm } from './AgentConfigForm';
import { ConnectionsForm } from './ConnectionsForm';
import { ProviderForm } from './ProviderForm';
import { SystemPromptForm } from './SystemPromptForm';
import styles from './SettingsApp.module.css';

type Section = 'agent' | 'providers' | 'prompt' | 'connections';

const sidebarItems: { key: Section; label: string }[] = [
  { key: 'agent', label: 'Agent' },
  { key: 'providers', label: 'Providers' },
  { key: 'prompt', label: 'System Prompt' },
  { key: 'connections', label: 'Connections' },
];

export function SettingsApp() {
  const [providers, setProviders] = useState<string[]>([]);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState<Section>('agent');

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
      <div className={styles.sidebar}>
        {sidebarItems.map(item => (
          <button
            key={item.key}
            className={`${styles.sidebarItem} ${activeTab === item.key ? styles.sidebarItemActive : ''}`}
            onClick={() => setActiveTab(item.key)}
          >
            {item.label}
          </button>
        ))}
      </div>

      <div className={styles.content}>
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
    </div>
  );
}
