import type { AppDefinition } from './types';
import { SettingsApp } from './settings/SettingsApp';
import { TextApp } from './text/TextApp';
import { VoiceApp } from './voice/VoiceApp';
import { ConversationsApp } from './conversations/ConversationsApp';
import { ExplorerApp } from './explorer/ExplorerApp';
import { TerminalApp } from './terminal/TerminalApp';
import { ScheduledTasksApp } from './scheduled-tasks/ScheduledTasksApp';

const apps: AppDefinition[] = [
  {
    id: 'text',
    title: 'Text',
    icon: 'text-icon',
    component: TextApp,
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
    id: 'terminal',
    title: 'Terminal',
    icon: 'terminal-icon',
    component: TerminalApp,
    defaultSize: { width: 800, height: 500 },
  },
  {
    id: 'scheduled-tasks',
    title: 'Scheduled Tasks',
    icon: 'scheduled-tasks-icon',
    component: ScheduledTasksApp,
    defaultSize: { width: 900, height: 600 },
  },
  {
    id: 'admin',
    title: 'Settings',
    icon: 'admin-icon',
    component: SettingsApp,
    defaultSize: { width: 800, height: 500 },
  },
];

export default apps;
