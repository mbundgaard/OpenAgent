import { useEffect, useRef } from 'react';
import type { AppDefinition } from '../apps/types';
import styles from './StartMenu.module.css';

interface Props {
  apps: AppDefinition[];
  onOpen: (app: AppDefinition) => void;
  onClose: () => void;
}

export function StartMenu({ apps, onOpen, onClose }: Props) {
  const ref = useRef<HTMLDivElement>(null);

  // Close when clicking outside the menu
  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        onClose();
      }
    }
    // Defer listener so the opening click doesn't immediately close
    const timer = setTimeout(() => document.addEventListener('mousedown', handleClick), 0);
    return () => {
      clearTimeout(timer);
      document.removeEventListener('mousedown', handleClick);
    };
  }, [onClose]);

  function handleAppClick(app: AppDefinition) {
    onOpen(app);
    onClose();
  }

  return (
    <div ref={ref} className={styles.menu}>
      {apps.map(app => (
        <button key={app.id} className={styles.item} onClick={() => handleAppClick(app)}>
          <svg className={styles.icon}>
            <use href={`/icons.svg#${app.icon}`} />
          </svg>
          <span className={styles.label}>{app.title}</span>
        </button>
      ))}
    </div>
  );
}
