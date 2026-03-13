import styles from './AdminApp.module.css';

export function AdminApp() {
  return (
    <div className={styles.admin}>
      <h2>Settings</h2>
      <div className={styles.section}>
        <h3>General</h3>
        <p className={styles.placeholder}>Settings will appear here.</p>
      </div>
      <div className={styles.section}>
        <h3>Providers</h3>
        <p className={styles.placeholder}>LLM provider configuration.</p>
      </div>
      <div className={styles.section}>
        <h3>Authentication</h3>
        <p className={styles.placeholder}>Auth settings and API keys.</p>
      </div>
    </div>
  );
}
