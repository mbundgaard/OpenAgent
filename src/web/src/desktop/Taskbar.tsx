import { useState, useEffect } from 'react';
import type { WindowState } from '../windows/types';
import styles from './Taskbar.module.css';

interface Props {
  windows: WindowState[];
  onFocus: (instanceId: string) => void;
  onMinimize: (instanceId: string) => void;
  onRestore: (instanceId: string) => void;
}

function formatTime(): string {
  return new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function Taskbar({ windows, onFocus, onMinimize, onRestore }: Props) {
  const [time, setTime] = useState(formatTime);

  useEffect(() => {
    const interval = setInterval(() => setTime(formatTime()), 30_000);
    return () => clearInterval(interval);
  }, []);

  // Find the top-most non-minimized window
  const focusedId = windows
    .filter(w => !w.isMinimized)
    .sort((a, b) => b.zIndex - a.zIndex)[0]?.instanceId;

  function handleClick(win: WindowState) {
    if (win.isMinimized) {
      onRestore(win.instanceId);
    } else if (win.instanceId === focusedId) {
      onMinimize(win.instanceId);
    } else {
      onFocus(win.instanceId);
    }
  }

  return (
    <div className={styles.taskbar}>
      <span className={styles.brand}>AgentOS</span>
      <div className={styles.windowButtons}>
        {windows.map(win => (
          <button
            key={win.instanceId}
            className={`${styles.windowBtn} ${win.instanceId === focusedId ? styles.windowBtnActive : ''}`}
            onClick={() => handleClick(win)}
          >
            {win.title}
          </button>
        ))}
      </div>
      <span className={styles.clock}>{time}</span>
    </div>
  );
}
