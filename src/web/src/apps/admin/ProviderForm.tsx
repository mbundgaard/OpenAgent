import { useEffect, useState } from 'react';
import type { ConfigField } from './api';
import { getProviderConfig, getProviderValues, saveProviderConfig } from './api';
import styles from './ProviderForm.module.css';

interface Props {
  providerKey: string;
}

export function ProviderForm({ providerKey }: Props) {
  const [fields, setFields] = useState<ConfigField[]>([]);
  const [values, setValues] = useState<Record<string, string>>({});
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);

    Promise.all([getProviderConfig(providerKey), getProviderValues(providerKey)])
      .then(([configFields, savedValues]) => {
        if (cancelled) return;
        setFields(configFields);

        // Initialize form values from saved values or defaults
        const initial: Record<string, string> = {};
        for (const field of configFields) {
          const saved = savedValues[field.key];
          if (typeof saved === 'string' && saved !== '***') {
            initial[field.key] = saved;
          } else {
            initial[field.key] = field.default_value ?? '';
          }
        }
        setValues(initial);
        setLoading(false);
      })
      .catch(() => {
        if (!cancelled) setLoading(false);
      });

    return () => { cancelled = true; };
  }, [providerKey]);

  const handleSave = async () => {
    setSaving(true);
    setStatus(null);
    try {
      await saveProviderConfig(providerKey, values);
      setStatus('Saved');
      setTimeout(() => setStatus(null), 2000);
    } catch {
      setStatus('Error saving');
    }
    setSaving(false);
  };

  if (loading) return <p className={styles.loading}>Loading...</p>;
  if (fields.length === 0) return <p className={styles.empty}>No configuration fields.</p>;

  return (
    <div className={styles.form}>
      {fields.map(field => (
        <label key={field.key} className={styles.field}>
          <span className={styles.label}>
            {field.label}
            {field.required && <span className={styles.required}>*</span>}
          </span>
          {field.type === 'Enum' && field.options ? (
            <select
              className={styles.input}
              value={values[field.key] ?? ''}
              onChange={e => setValues(v => ({ ...v, [field.key]: e.target.value }))}
            >
              {field.options.map(opt => (
                <option key={opt} value={opt}>{opt}</option>
              ))}
            </select>
          ) : (
            <input
              className={styles.input}
              type={field.type === 'Secret' ? 'password' : 'text'}
              value={values[field.key] ?? ''}
              placeholder={field.default_value ?? ''}
              onChange={e => setValues(v => ({ ...v, [field.key]: e.target.value }))}
            />
          )}
        </label>
      ))}
      <div className={styles.actions}>
        <button className={styles.saveButton} onClick={handleSave} disabled={saving}>
          {saving ? 'Saving...' : 'Save'}
        </button>
        {status && <span className={styles.status}>{status}</span>}
      </div>
    </div>
  );
}
