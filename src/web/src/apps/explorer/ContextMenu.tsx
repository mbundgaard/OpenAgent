import { useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import styles from './ContextMenu.module.css';

export interface MenuItem {
  label: string;
  action: () => void;
  danger?: boolean;
}

interface Props {
  x: number;
  y: number;
  items: MenuItem[];
  onClose: () => void;
}

/** Positioned context menu — closes on click outside or Escape. */
export function ContextMenu({ x, y, items, onClose }: Props) {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleClick = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onClose();
    };
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('mousedown', handleClick);
    document.addEventListener('keydown', handleKey);
    return () => {
      document.removeEventListener('mousedown', handleClick);
      document.removeEventListener('keydown', handleKey);
    };
  }, [onClose]);

  return createPortal(
    <div ref={ref} className={styles.menu} style={{ left: x, top: y }}>
      {items.map(item => (
        <button
          key={item.label}
          className={`${styles.item} ${item.danger ? styles.danger : ''}`}
          onClick={() => { item.action(); onClose(); }}
        >
          {item.label}
        </button>
      ))}
    </div>,
    document.body,
  );
}
