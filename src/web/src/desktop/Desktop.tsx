import apps from '../apps/registry';
import { useWindowManager } from '../hooks/useWindowManager';
import { WindowFrame } from '../windows/WindowFrame';
import { AppGrid } from './AppGrid';
import { Taskbar } from './Taskbar';
import styles from './Desktop.module.css';

export function Desktop() {
  const wm = useWindowManager();

  return (
    <div className={styles.desktop}>
      <div className={styles.windowArea}>
        <AppGrid apps={apps} onOpen={wm.openWindow} />
        {wm.windows.map(win => {
          const app = apps.find(a => a.id === win.appId);
          if (!app) return null;
          return (
            <WindowFrame
              key={win.instanceId}
              window={win}
              component={app.component}
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
      <Taskbar windows={wm.windows} onFocus={wm.focusWindow} onMinimize={wm.minimizeWindow} onRestore={wm.restoreWindow} />
    </div>
  );
}
