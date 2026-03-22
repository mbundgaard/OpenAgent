import { useMemo } from 'react';
import apps from '../apps/registry';
import { useWindowManager } from '../hooks/useWindowManager';
import { WindowFrame } from '../windows/WindowFrame';
import { WindowContext } from '../windows/WindowContext';
import { TopBar } from './TopBar';
import styles from './Desktop.module.css';

export function Desktop() {
  const wm = useWindowManager();

  const windowCtx = useMemo(() => ({
    openDynamicWindow: wm.openDynamicWindow,
  }), [wm.openDynamicWindow]);

  return (
    <WindowContext.Provider value={windowCtx}>
      <div className={styles.desktop}>
        <TopBar
          apps={apps}
          windows={wm.windows}
          onOpenApp={wm.openWindow}
          onFocus={wm.focusWindow}
          onMinimize={wm.minimizeWindow}
          onRestore={wm.restoreWindow}
        />
        <div className={styles.windowArea}>
          {wm.windows.map(win => {
            // Dynamic windows carry their own component; registry windows look it up
            const app = apps.find(a => a.id === win.appId);
            const component = win.component ?? app?.component;
            if (!component) return null;
            return (
              <WindowFrame
                key={win.instanceId}
                window={win}
                component={component}
                componentProps={win.componentProps}
                onFocus={() => wm.focusWindow(win.instanceId)}
                onClose={() => wm.closeWindow(win.instanceId)}
                onMinimize={() => wm.minimizeWindow(win.instanceId)}
                onMaximize={() => wm.maximizeWindow(win.instanceId)}
                onRestore={() => wm.restoreWindow(win.instanceId)}
                onMove={(x, y) => wm.moveWindow(win.instanceId, x, y)}
                onResize={(w, h, x, y) => wm.resizeWindow(win.instanceId, w, h, x, y)}
              />
            );
          })}
        </div>
      </div>
    </WindowContext.Provider>
  );
}
