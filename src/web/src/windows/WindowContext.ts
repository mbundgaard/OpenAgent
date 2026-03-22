import { createContext, useContext } from 'react';
import type { DynamicWindowOptions } from '../hooks/useWindowManager';

interface WindowContextValue {
  openDynamicWindow: (options: DynamicWindowOptions) => void;
}

export const WindowContext = createContext<WindowContextValue | null>(null);

/** Hook for apps to open dynamic windows. */
export function useWindowContext() {
  const ctx = useContext(WindowContext);
  if (!ctx) throw new Error('useWindowContext must be used within WindowContext.Provider');
  return ctx;
}
