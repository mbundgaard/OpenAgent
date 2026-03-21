import { useEffect, useState } from 'react';
import { getSystemPrompt, saveSystemPrompt } from './api';
import styles from './SystemPromptForm.module.css';

const PROMPT_KEYS = [
  { key: 'agents', label: 'Agents' },
  { key: 'soul', label: 'Soul' },
  { key: 'identity', label: 'Identity' },
  { key: 'user', label: 'User' },
  { key: 'tools', label: 'Tools' },
  { key: 'voice', label: 'Voice' },
] as const;

export function SystemPromptForm() {
  const [values, setValues] = useState<Record<string, string>>({});
  const [original, setOriginal] = useState<Record<string, string>>({});
  const [activeKey, setActiveKey] = useState<string>('agents');
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    getSystemPrompt().then(data => {
      if (cancelled) return;
      const vals: Record<string, string> = {};
      for (const { key } of PROMPT_KEYS) {
        vals[key] = data[key] ?? '';
      }
      setValues(vals);
      setOriginal(vals);
      setLoading(false);
    });
    return () => { cancelled = true; };
  }, []);

  const handleSave = async () => {
    setSaving(true);
    setStatus(null);
    try {
      // Only send changed keys
      const changed: Record<string, string> = {};
      for (const { key } of PROMPT_KEYS) {
        if (values[key] !== original[key]) {
          changed[key] = values[key];
        }
      }
      if (Object.keys(changed).length > 0) {
        await saveSystemPrompt(changed);
        setOriginal({ ...values });
      }
      setStatus('Saved');
      setTimeout(() => setStatus(null), 2000);
    } catch {
      setStatus('Error saving');
    }
    setSaving(false);
  };

  if (loading) return <p className={styles.loading}>Loading...</p>;

  const hasChanges = PROMPT_KEYS.some(({ key }) => values[key] !== original[key]);

  return (
    <div className={styles.container}>
      <div className={styles.tabs}>
        {PROMPT_KEYS.map(({ key, label }) => (
          <button
            key={key}
            className={`${styles.tab} ${activeKey === key ? styles.active : ''} ${values[key] !== original[key] ? styles.modified : ''}`}
            onClick={() => setActiveKey(key)}
          >
            {label}
          </button>
        ))}
      </div>
      <textarea
        className={styles.editor}
        value={values[activeKey] ?? ''}
        onChange={e => setValues(v => ({ ...v, [activeKey]: e.target.value }))}
        spellCheck={false}
      />
      <div className={styles.actions}>
        <button className={styles.saveButton} onClick={handleSave} disabled={saving || !hasChanges}>
          {saving ? 'Saving...' : 'Save'}
        </button>
        {status && <span className={styles.status}>{status}</span>}
      </div>
    </div>
  );
}
