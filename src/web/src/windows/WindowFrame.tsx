import { Rnd } from 'react-rnd';
import type { WindowState } from './types';
import type { ComponentType } from 'react';
import styles from './WindowFrame.module.css';

interface Props {
  window: WindowState;
  component: ComponentType;
  onFocus: () => void;
  onClose: () => void;
  onMinimize: () => void;
  onMaximize: () => void;
  onRestore: () => void;
  onMove: (x: number, y: number) => void;
  onResize: (width: number, height: number, x: number, y: number) => void;
}

export function WindowFrame({ window: win, component: AppComponent, onFocus, onClose, onMinimize, onMaximize, onRestore, onMove, onResize }: Props) {
  if (win.isMinimized) return null;

  return (
    <Rnd
      position={win.position}
      size={win.size}
      style={{ zIndex: win.zIndex }}
      minWidth={300}
      minHeight={200}
      disableDragging={win.isMaximized}
      enableResizing={!win.isMaximized}
      dragHandleClassName={styles.titleBar}
      onMouseDown={onFocus}
      onDragStop={(_e, d) => onMove(d.x, d.y)}
      onResizeStop={(_e, _dir, ref, _delta, pos) => {
        onResize(ref.offsetWidth, ref.offsetHeight, pos.x, pos.y);
      }}
    >
      <div className={styles.window} style={{ width: '100%', height: '100%' }}>
        <div className={styles.titleBar} onDoubleClick={win.isMaximized ? onRestore : onMaximize}>
          <span className={styles.titleText}>{win.title}</span>
          <div className={styles.titleButtons}>
            <button className={styles.titleBtn} onClick={onMinimize} title="Minimize">&minus;</button>
            <button className={styles.titleBtn} onClick={win.isMaximized ? onRestore : onMaximize} title={win.isMaximized ? 'Restore' : 'Maximize'}>
              {win.isMaximized ? '\u29C9' : '\u25A1'}
            </button>
            <button className={`${styles.titleBtn} ${styles.closeBtn}`} onClick={onClose} title="Close">&times;</button>
          </div>
        </div>
        <div className={styles.content}>
          <AppComponent />
        </div>
      </div>
    </Rnd>
  );
}
