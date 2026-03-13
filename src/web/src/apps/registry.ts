import type { AppDefinition } from './types';
import { AdminApp } from './admin/AdminApp';

const apps: AppDefinition[] = [
  {
    id: 'admin',
    title: 'Admin',
    icon: '\u2699',
    component: AdminApp,
    defaultSize: { width: 600, height: 400 },
  },
];

export default apps;
