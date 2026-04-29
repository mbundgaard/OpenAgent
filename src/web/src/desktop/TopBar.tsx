import { useState, useEffect, useCallback } from 'react';
import type { AppDefinition } from '../apps/types';
import type { WindowState } from '../windows/types';
import { StartMenu } from './StartMenu';
import { PairPhone } from './PairPhone';
import styles from './TopBar.module.css';

interface Props {
  apps: AppDefinition[];
  windows: WindowState[];
  onOpenApp: (app: AppDefinition) => void;
  onFocus: (instanceId: string) => void;
  onMinimize: (instanceId: string) => void;
  onRestore: (instanceId: string) => void;
}

function formatTime(): string {
  return new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
}

export function TopBar({ apps, windows, onOpenApp, onFocus, onMinimize, onRestore }: Props) {
  const [menuOpen, setMenuOpen] = useState(false);
  const [pairPhoneOpen, setPairPhoneOpen] = useState(false);
  const [time, setTime] = useState(formatTime);

  useEffect(() => {
    const interval = setInterval(() => setTime(formatTime()), 30_000);
    return () => clearInterval(interval);
  }, []);

  // Find the top-most non-minimized window
  const focusedId = windows
    .filter(w => !w.isMinimized)
    .sort((a, b) => b.zIndex - a.zIndex)[0]?.instanceId;

  function handleWindowClick(win: WindowState) {
    if (win.isMinimized) {
      onRestore(win.instanceId);
    } else if (win.instanceId === focusedId) {
      onMinimize(win.instanceId);
    } else {
      onFocus(win.instanceId);
    }
  }

  // Look up icon for a window from the app registry
  function getWindowIcon(win: WindowState): string | undefined {
    return apps.find(a => a.id === win.appId)?.icon;
  }

  const handleMenuClose = useCallback(() => setMenuOpen(false), []);

  return (
    <div className={styles.topbar}>
      {/* Start menu button */}
      <button
        className={`${styles.startBtn} ${menuOpen ? styles.startBtnActive : ''}`}
        onClick={() => setMenuOpen(prev => !prev)}
      >
        AgentOS
      </button>

      {/* Open window icons */}
      <div className={styles.windowIcons}>
        {windows.map(win => {
          const icon = getWindowIcon(win);
          return (
            <button
              key={win.instanceId}
              className={`${styles.windowIcon} ${win.instanceId === focusedId ? styles.windowIconActive : ''}`}
              onClick={() => handleWindowClick(win)}
              title={win.title}
            >
              {icon ? (
                <svg className={styles.iconSvg}>
                  <use href={`/icons.svg#${icon}`} />
                </svg>
              ) : (
                <span className={styles.iconFallback}>{win.title[0]}</span>
              )}
            </button>
          );
        })}
      </div>

      {/* Clock */}
      <span className={styles.clock}>{time}</span>

      {/* Start menu dropdown */}
      {menuOpen && (
        <StartMenu apps={apps} onOpen={onOpenApp} onClose={handleMenuClose} onPairPhone={() => setPairPhoneOpen(true)} />
      )}

      {/* Pair phone QR modal */}
      {pairPhoneOpen && <PairPhone onClose={() => setPairPhoneOpen(false)} />}
    </div>
  );
}
