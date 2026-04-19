import { useEffect, useState } from 'react';
import { getProviderValues, getProviderModels, saveProviderConfig, listProviders, type ProviderInfo } from './api';
import { listConversations, type ConversationSummary } from '../conversations/api';
import styles from './ProviderForm.module.css';

function formatConversationLabel(conv: ConversationSummary): string {
  if (conv.display_name) return conv.display_name;
  if (conv.channel_type && conv.channel_chat_id) {
    return `${conv.channel_type}: ${conv.channel_chat_id}`;
  }
  return `${conv.source} (${conv.id.slice(0, 8)})`;
}

/** Agent config form with model dropdowns populated from provider model lists. */
export function AgentConfigForm() {
  const [values, setValues] = useState<Record<string, string>>({
    textProvider: '', textModel: '',
    voiceProvider: '', voiceModel: '',
    compactionProvider: '', compactionModel: '',
    memoryDays: '3',
    mainConversationId: '',
    embeddingProvider: '',
    indexRunAtHour: '2',
  });
  const [providers, setProviders] = useState<ProviderInfo[]>([]);
  const [modelsByProvider, setModelsByProvider] = useState<Record<string, string[]>>({});
  const [conversations, setConversations] = useState<ConversationSummary[]>([]);
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  // Load providers, their models, and current agent config
  useEffect(() => {
    let cancelled = false;

    Promise.all([
      listProviders(),
      getProviderValues('agent'),
      listConversations(),
    ]).then(async ([providerInfos, agentValues, convs]) => {
      if (cancelled) return;

      setProviders(providerInfos);
      setConversations(convs.sort((a, b) =>
        (b.last_activity ?? b.created_at).localeCompare(a.last_activity ?? a.created_at)));

      // Load current values
      const vals: Record<string, string> = {};
      for (const key of ['textProvider', 'textModel', 'voiceProvider', 'voiceModel', 'compactionProvider', 'compactionModel', 'memoryDays', 'mainConversationId', 'embeddingProvider', 'indexRunAtHour']) {
        const v = agentValues[key];
        vals[key] = typeof v === 'string' ? v : '';
      }
      setValues(vals);

      // Fetch models for each provider that has them
      const modelsMap: Record<string, string[]> = {};
      for (const p of providerInfos) {
        try {
          const models = await getProviderModels(p.key);
          if (models.length > 0) modelsMap[p.key] = models;
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

  // Text providers must have at least one model (you select which one to use).
  // Voice providers appear regardless of model list — some providers (e.g. Grok) don't expose client-side model selection.
  const textProviders = providers.filter(p => p.capabilities.includes('text') && modelsByProvider[p.key]?.length).map(p => p.key);
  const voiceProviders = providers.filter(p => p.capabilities.includes('voice')).map(p => p.key);

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
      <div className={styles.form} style={{ marginBottom: 12 }}>
        <label className={styles.field}>
          <span className={styles.label}>Memory Days</span>
          <input
            type="number"
            className={styles.input}
            min={0}
            max={30}
            value={values.memoryDays}
            onChange={e => setValues(v => ({ ...v, memoryDays: e.target.value }))}
          />
        </label>
      </div>
      <div className={styles.form} style={{ marginBottom: 12 }}>
        <label className={styles.field}>
          <span className={styles.label}>Main Conversation</span>
          <select
            className={styles.input}
            value={values.mainConversationId}
            onChange={e => setValues(v => ({ ...v, mainConversationId: e.target.value }))}
          >
            <option value="">(none — silent fallback for scheduled tasks)</option>
            {conversations.map(c => (
              <option key={c.id} value={c.id}>{formatConversationLabel(c)}</option>
            ))}
          </select>
        </label>
      </div>
      <div className={styles.form} style={{ marginBottom: 12 }}>
        <label className={styles.field}>
          <span className={styles.label}>Embedding Provider</span>
          <select
            className={styles.input}
            value={values.embeddingProvider}
            onChange={e => setValues(v => ({ ...v, embeddingProvider: e.target.value }))}
          >
            <option value="">(disabled — memory index job off, search tools hidden)</option>
            <option value="onnx">onnx (multilingual-e5-base, local)</option>
          </select>
        </label>
        <label className={styles.field}>
          <span className={styles.label}>Index Run Hour (Europe/Copenhagen)</span>
          <input
            type="number"
            className={styles.input}
            min={0}
            max={23}
            value={values.indexRunAtHour}
            onChange={e => setValues(v => ({ ...v, indexRunAtHour: e.target.value }))}
          />
        </label>
      </div>
      <div className={styles.actions}>
        <button className={styles.saveButton} onClick={handleSave} disabled={saving}>
          {saving ? 'Saving...' : 'Save'}
        </button>
        {status && <span className={styles.status}>{status}</span>}
      </div>
    </div>
  );
}
