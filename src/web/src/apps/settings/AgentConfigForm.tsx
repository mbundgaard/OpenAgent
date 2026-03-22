import { useEffect, useState } from 'react';
import { getProviderValues, getProviderModels, saveProviderConfig, listProviders } from './api';
import styles from './ProviderForm.module.css';

/** Agent config form with model dropdowns populated from provider model lists. */
export function AgentConfigForm() {
  const [values, setValues] = useState<Record<string, string>>({
    textProvider: '', textModel: '',
    voiceProvider: '', voiceModel: '',
    compactionProvider: '', compactionModel: '',
  });
  const [providers, setProviders] = useState<string[]>([]);
  const [modelsByProvider, setModelsByProvider] = useState<Record<string, string[]>>({});
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  // Load providers, their models, and current agent config
  useEffect(() => {
    let cancelled = false;

    Promise.all([
      listProviders(),
      getProviderValues('agent'),
    ]).then(async ([providerKeys, agentValues]) => {
      if (cancelled) return;

      setProviders(providerKeys);

      // Load current values
      const vals: Record<string, string> = {};
      for (const key of ['textProvider', 'textModel', 'voiceProvider', 'voiceModel', 'compactionProvider', 'compactionModel']) {
        const v = agentValues[key];
        vals[key] = typeof v === 'string' ? v : '';
      }
      setValues(vals);

      // Fetch models for each provider that has them
      const modelsMap: Record<string, string[]> = {};
      for (const pk of providerKeys) {
        try {
          const models = await getProviderModels(pk);
          if (models.length > 0) modelsMap[pk] = models;
        } catch { /* provider has no models */ }
      }
      if (!cancelled) setModelsByProvider(modelsMap);
      if (!cancelled) setLoading(false);
    });

    return () => { cancelled = true; };
  }, []);

  const handleSave = async () => {
    setSaving(true);
    setStatus(null);
    try {
      await saveProviderConfig('agent', values);
      setStatus('Saved');
      setTimeout(() => setStatus(null), 2000);
    } catch {
      setStatus('Error saving');
    }
    setSaving(false);
  };

  if (loading) return <p className={styles.loading}>Loading...</p>;

  // Provider keys that have models (for dropdowns)
  const textProviders = providers.filter(p => modelsByProvider[p]?.length);
  const voiceProviders = providers.filter(p => modelsByProvider[p]?.length);

  const renderSlot = (label: string, providerKey: string, modelKey: string, providerOptions: string[]) => {
    const selectedProvider = values[providerKey];
    const availableModels = modelsByProvider[selectedProvider] ?? [];

    return (
      <div className={styles.form} style={{ marginBottom: 12 }}>
        <label className={styles.field}>
          <span className={styles.label}>{label} Provider</span>
          <select
            className={styles.input}
            value={selectedProvider}
            onChange={e => setValues(v => ({ ...v, [providerKey]: e.target.value, [modelKey]: '' }))}
          >
            <option value="">-- select --</option>
            {providerOptions.map(p => (
              <option key={p} value={p}>{p}</option>
            ))}
          </select>
        </label>
        <label className={styles.field}>
          <span className={styles.label}>{label} Model</span>
          <select
            className={styles.input}
            value={values[modelKey]}
            onChange={e => setValues(v => ({ ...v, [modelKey]: e.target.value }))}
            disabled={availableModels.length === 0}
          >
            <option value="">-- select --</option>
            {availableModels.map(m => (
              <option key={m} value={m}>{m}</option>
            ))}
          </select>
        </label>
      </div>
    );
  };

  return (
    <div>
      {renderSlot('Text', 'textProvider', 'textModel', textProviders)}
      {renderSlot('Voice', 'voiceProvider', 'voiceModel', voiceProviders)}
      {renderSlot('Compaction', 'compactionProvider', 'compactionModel', textProviders)}
      <div className={styles.actions}>
        <button className={styles.saveButton} onClick={handleSave} disabled={saving}>
          {saving ? 'Saving...' : 'Save'}
        </button>
        {status && <span className={styles.status}>{status}</span>}
      </div>
    </div>
  );
}
