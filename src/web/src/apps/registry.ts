import type { AppDefinition } from './types';
import { AdminApp } from './admin/AdminApp';
import { ChatApp } from './chat/ChatApp';

const apps: AppDefinition[] = [
  {
    id: 'chat',
    title: 'Chat',
    icon: 'chat-icon',
    component: ChatApp,
    defaultSize: { width: 500, height: 600 },
  },
  {
    id: 'admin',
    title: 'Admin',
    icon: 'admin-icon',
    component: AdminApp,
    defaultSize: { width: 650, height: 500 },
  },
];

export default apps;
