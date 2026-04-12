import { apiFetch } from '../../auth/api';

export interface ConversationSummary {
  id: string;
  source: string;
  type: string;
  provider: string;
  model: string;
  created_at: string;
  total_prompt_tokens: number;
  total_completion_tokens: number;
  turn_count: number;
  last_activity: string | null;
  active_skills: string[] | null;
  channel_type?: string | null;
  connection_id?: string | null;
  channel_chat_id?: string | null;
  display_name?: string | null;
}

export interface ConversationDetail extends ConversationSummary {
  voice_session_id: string | null;
  voice_session_open: boolean;
  compaction_running: boolean;
}

export interface ConversationMessage {
  id: string;
  conversation_id: string;
  role: string;
  content: string | null;
  created_at: string;
  tool_calls: string | null;
  tool_call_id: string | null;
  channel_message_id: string | null;
  prompt_tokens: number | null;
  completion_tokens: number | null;
  elapsed_ms: number | null;
}

export async function createConversation(): Promise<string> {
  const res = await apiFetch('/api/conversations', { method: 'POST' });
  const data: { id: string } = await res.json();
  return data.id;
}

export async function listConversations(): Promise<ConversationSummary[]> {
  const res = await apiFetch('/api/conversations');
  return res.json();
}

export async function getConversation(conversationId: string): Promise<ConversationDetail> {
  const res = await apiFetch(`/api/conversations/${conversationId}`);
  return res.json();
}

export async function getMessages(conversationId: string): Promise<ConversationMessage[]> {
  const res = await apiFetch(`/api/conversations/${conversationId}/messages`);
  return res.json();
}

export async function updateConversation(conversationId: string, data: {
  source?: string;
  provider?: string;
  model?: string;
}): Promise<ConversationDetail> {
  const res = await apiFetch(`/api/conversations/${conversationId}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
  });
  return res.json();
}

export async function deleteConversation(conversationId: string): Promise<void> {
  await apiFetch(`/api/conversations/${conversationId}`, { method: 'DELETE' });
}
