import type { ComponentType } from 'react';

export interface AppDefinition {
  id: string;
  title: string;
  icon: string;
  component: ComponentType;
  defaultSize: { width: number; height: number };
}
