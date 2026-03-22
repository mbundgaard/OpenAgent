import type { ComponentType } from 'react';

export interface AppDefinition {
  id: string;
  title: string;
  icon: string;
  component: ComponentType<Record<string, unknown>>;
  defaultSize: { width: number; height: number };
}
