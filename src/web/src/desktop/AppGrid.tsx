import type { AppDefinition } from '../apps/types';
import styles from './AppGrid.module.css';

interface Props {
  apps: AppDefinition[];
  onOpen: (app: AppDefinition) => void;
}

export function AppGrid({ apps, onOpen }: Props) {
  return (
    <div className={styles.grid}>
      {apps.map(app => (
        <button key={app.id} className={styles.appIcon} onDoubleClick={() => onOpen(app)}>
          <span className={styles.iconEmoji}>{app.icon}</span>
          <span className={styles.label}>{app.title}</span>
        </button>
      ))}
    </div>
  );
}
