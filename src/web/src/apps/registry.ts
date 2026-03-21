import type { AppDefinition } from './types';
import { AdminApp } from './admin/AdminApp';

const apps: AppDefinition[] = [
  {
    id: 'admin',
    title: 'Admin',
    icon: '\u2699',
    component: AdminApp,
    defaultSize: { width: 650, height: 500 },
  },
];

export default apps;
