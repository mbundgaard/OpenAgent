import { useCallback, useEffect, useState } from 'react';
import type { ConversationMessage } from '../../conversations/api';
import { apiFetch } from '../../../auth/api';

/**
 * Loads messages for one conversation. Returns mutators for streaming updates.
 * 404 is treated as "empty conversation" — the new-conversation flow generates
 * a GUID locally before any backend row exists.
 */
export function useConversation(conversationId: string): {
  messages: ConversationMessage[];
  appendMessage: (msg: ConversationMessage) => void;
  updateLastMessageContent: (content: string) => void;
  error: string | null;
} {
  const [messages, setMessages] = useState<ConversationMessage[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setError(null);
    setMessages([]);

    apiFetch(`/api/conversations/${conversationId}/messages?tail=20`)
      .then(async res => {
        if (cancelled) return;
        if (res.status === 404) {
          // New conversation — no backend row yet. Empty list, no error.
          setMessages([]);
          return;
        }
        if (!res.ok) {
          setError(`Failed to load conversation (${res.status})`);
          return;
        }
        const data: ConversationMessage[] = await res.json();
        setMessages(data);
      })
      .catch(err => {
        if (!cancelled) setError(err.message ?? 'Network error');
      });

    return () => { cancelled = true; };
  }, [conversationId]);

  const appendMessage = useCallback((msg: ConversationMessage) => {
    setMessages(prev => [...prev, msg]);
  }, []);

  const updateLastMessageContent = useCallback((content: string) => {
    setMessages(prev => {
      if (prev.length === 0) return prev;
      const last = prev[prev.length - 1];
      const updated = [...prev];
      updated[updated.length - 1] = { ...last, content };
      return updated;
    });
  }, []);

  return { messages, appendMessage, updateLastMessageContent, error };
}
