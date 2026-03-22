import type { ComponentType } from 'react';

export interface WindowState {
  instanceId: string;
  appId: string;
  title: string;
  position: { x: number; y: number };
  size: { width: number; height: number };
  isMinimized: boolean;
  isMaximized: boolean;
  preMaximizeRect?: { x: number; y: number; width: number; height: number };
  zIndex: number;
  /** Component override for dynamic (non-registry) windows. */
  component?: ComponentType<Record<string, unknown>>;
  /** Props passed to the window component. */
  componentProps?: Record<string, unknown>;
}
