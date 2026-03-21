import { apiFetch } from '../../auth/api';

export interface ConversationSummary {
  id: string;
  source: string;
  type: string;
  provider: string;
  model: string;
  created_at: string;
}

export interface ConversationMessage {
  id: string;
  conversation_id: string;
  role: string;
  content: string | null;
  created_at: string;
  tool_calls: string | null;
  tool_call_id: string | null;
}

export async function listConversations(): Promise<ConversationSummary[]> {
  const res = await apiFetch('/api/conversations');
  return res.json();
}

export async function getMessages(conversationId: string): Promise<ConversationMessage[]> {
  const res = await apiFetch(`/api/conversations/${conversationId}/messages`);
  return res.json();
}

export async function deleteConversation(conversationId: string): Promise<void> {
  await apiFetch(`/api/conversations/${conversationId}`, { method: 'DELETE' });
}
