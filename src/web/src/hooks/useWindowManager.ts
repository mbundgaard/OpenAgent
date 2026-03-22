import { useReducer, useCallback } from 'react';
import type { ComponentType } from 'react';
import type { WindowState } from '../windows/types';
import type { AppDefinition } from '../apps/types';

const TASKBAR_HEIGHT = 48;
const CASCADE_OFFSET = 30;

/** Options for opening a dynamic (non-registry) window. */
export interface DynamicWindowOptions {
  id: string;
  title: string;
  component: ComponentType<Record<string, unknown>>;
  componentProps?: Record<string, unknown>;
  defaultSize: { width: number; height: number };
}

type Action =
  | { type: 'OPEN'; app: AppDefinition }
  | { type: 'OPEN_DYNAMIC'; options: DynamicWindowOptions }
  | { type: 'CLOSE'; instanceId: string }
  | { type: 'FOCUS'; instanceId: string }
  | { type: 'MINIMIZE'; instanceId: string }
  | { type: 'MAXIMIZE'; instanceId: string }
  | { type: 'RESTORE'; instanceId: string }
  | { type: 'MOVE'; instanceId: string; x: number; y: number }
  | { type: 'RESIZE'; instanceId: string; width: number; height: number; x: number; y: number };

interface State {
  windows: WindowState[];
  nextZIndex: number;
}

function reducer(state: State, action: Action): State {
  switch (action.type) {
    case 'OPEN': {
      const count = state.windows.length;
      const win: WindowState = {
        instanceId: crypto.randomUUID(),
        appId: action.app.id,
        title: action.app.title,
        position: {
          x: Math.max(0, (window.innerWidth - action.app.defaultSize.width) / 2 + count * CASCADE_OFFSET),
          y: Math.max(0, (window.innerHeight - TASKBAR_HEIGHT - action.app.defaultSize.height) / 2 + count * CASCADE_OFFSET),
        },
        size: { ...action.app.defaultSize },
        isMinimized: false,
        isMaximized: false,
        zIndex: state.nextZIndex,
      };
      return { windows: [...state.windows, win], nextZIndex: state.nextZIndex + 1 };
    }

    case 'OPEN_DYNAMIC': {
      const count = state.windows.length;
      const opts = action.options;
      const win: WindowState = {
        instanceId: crypto.randomUUID(),
        appId: opts.id,
        title: opts.title,
        position: {
          x: Math.max(0, (window.innerWidth - opts.defaultSize.width) / 2 + count * CASCADE_OFFSET),
          y: Math.max(0, (window.innerHeight - TASKBAR_HEIGHT - opts.defaultSize.height) / 2 + count * CASCADE_OFFSET),
        },
        size: { ...opts.defaultSize },
        isMinimized: false,
        isMaximized: false,
        zIndex: state.nextZIndex,
        component: opts.component,
        componentProps: opts.componentProps,
      };
      return { windows: [...state.windows, win], nextZIndex: state.nextZIndex + 1 };
    }

    case 'CLOSE':
      return { ...state, windows: state.windows.filter(w => w.instanceId !== action.instanceId) };

    case 'FOCUS':
      return {
        nextZIndex: state.nextZIndex + 1,
        windows: state.windows.map(w =>
          w.instanceId === action.instanceId
            ? { ...w, zIndex: state.nextZIndex, isMinimized: false }
            : w
        ),
      };

    case 'MINIMIZE':
      return {
        ...state,
        windows: state.windows.map(w =>
          w.instanceId === action.instanceId ? { ...w, isMinimized: true } : w
        ),
      };

    case 'MAXIMIZE':
      return {
        nextZIndex: state.nextZIndex + 1,
        windows: state.windows.map(w =>
          w.instanceId === action.instanceId
            ? {
                ...w,
                isMaximized: true,
                isMinimized: false,
                zIndex: state.nextZIndex,
                preMaximizeRect: { ...w.position, ...w.size },
                position: { x: 0, y: 0 },
                size: { width: window.innerWidth, height: window.innerHeight - TASKBAR_HEIGHT },
              }
            : w
        ),
      };

    case 'RESTORE':
      return {
        nextZIndex: state.nextZIndex + 1,
        windows: state.windows.map(w => {
          if (w.instanceId !== action.instanceId) return w;
          const rect = w.preMaximizeRect;
          return {
            ...w,
            isMaximized: false,
            isMinimized: false,
            zIndex: state.nextZIndex,
            position: rect ? { x: rect.x, y: rect.y } : w.position,
            size: rect ? { width: rect.width, height: rect.height } : w.size,
            preMaximizeRect: undefined,
          };
        }),
      };

    case 'MOVE':
      return {
        ...state,
        windows: state.windows.map(w =>
          w.instanceId === action.instanceId
            ? { ...w, position: { x: action.x, y: action.y } }
            : w
        ),
      };

    case 'RESIZE':
      return {
        ...state,
        windows: state.windows.map(w =>
          w.instanceId === action.instanceId
            ? { ...w, size: { width: action.width, height: action.height }, position: { x: action.x, y: action.y } }
            : w
        ),
      };

    default:
      return state;
  }
}

export function useWindowManager() {
  const [state, dispatch] = useReducer(reducer, { windows: [], nextZIndex: 1 });

  const openWindow = useCallback((app: AppDefinition) => dispatch({ type: 'OPEN', app }), []);
  const openDynamicWindow = useCallback((options: DynamicWindowOptions) => dispatch({ type: 'OPEN_DYNAMIC', options }), []);
  const closeWindow = useCallback((instanceId: string) => dispatch({ type: 'CLOSE', instanceId }), []);
  const focusWindow = useCallback((instanceId: string) => dispatch({ type: 'FOCUS', instanceId }), []);
  const minimizeWindow = useCallback((instanceId: string) => dispatch({ type: 'MINIMIZE', instanceId }), []);
  const maximizeWindow = useCallback((instanceId: string) => dispatch({ type: 'MAXIMIZE', instanceId }), []);
  const restoreWindow = useCallback((instanceId: string) => dispatch({ type: 'RESTORE', instanceId }), []);
  const moveWindow = useCallback((instanceId: string, x: number, y: number) => dispatch({ type: 'MOVE', instanceId, x, y }), []);
  const resizeWindow = useCallback((instanceId: string, width: number, height: number, x: number, y: number) => dispatch({ type: 'RESIZE', instanceId, width, height, x, y }), []);

  return {
    windows: state.windows,
    openWindow,
    openDynamicWindow,
    closeWindow,
    focusWindow,
    minimizeWindow,
    maximizeWindow,
    restoreWindow,
    moveWindow,
    resizeWindow,
  };
}
