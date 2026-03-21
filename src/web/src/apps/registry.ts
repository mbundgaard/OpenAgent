import type { AppDefinition } from './types';
import { AdminApp } from './admin/AdminApp';
import { ChatApp } from './chat/ChatApp';
import { ConversationsApp } from './conversations/ConversationsApp';

const apps: AppDefinition[] = [
  {
    id: 'chat',
    title: 'Chat',
    icon: 'chat-icon',
    component: ChatApp,
    defaultSize: { width: 500, height: 600 },
  },
  {
    id: 'conversations',
    title: 'Conversations',
    icon: 'conversations-icon',
    component: ConversationsApp,
    defaultSize: { width: 800, height: 500 },
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
