import type { AppDefinition } from './types';
import { SettingsApp } from './settings/SettingsApp';
import { ChatApp } from './chat/ChatApp';
import { VoiceApp } from './voice/VoiceApp';
import { ConversationsApp } from './conversations/ConversationsApp';
import { ExplorerApp } from './explorer/ExplorerApp';

const apps: AppDefinition[] = [
  {
    id: 'chat',
    title: 'Chat',
    icon: 'chat-icon',
    component: ChatApp,
    defaultSize: { width: 500, height: 600 },
  },
  {
    id: 'voice',
    title: 'Voice',
    icon: 'voice-icon',
    component: VoiceApp,
    defaultSize: { width: 380, height: 480 },
  },
  {
    id: 'conversations',
    title: 'Conversations',
    icon: 'conversations-icon',
    component: ConversationsApp,
    defaultSize: { width: 800, height: 500 },
  },
  {
    id: 'explorer',
    title: 'Explorer',
    icon: 'explorer-icon',
    component: ExplorerApp,
    defaultSize: { width: 700, height: 500 },
  },
  {
    id: 'admin',
    title: 'Settings',
    icon: 'admin-icon',
    component: SettingsApp,
    defaultSize: { width: 650, height: 500 },
  },
];

export default apps;
