import { useEffect, useState } from 'react';
import type { ConversationSummary } from '../../conversations/api';
import { listConversations } from '../../conversations/api';

/**
 * Loads the full conversation list. Re-fetches when refreshTrigger changes.
 * The backend already returns the list ordered by LastActivity (newest first),
 * so this hook does not re-sort.
 */
export function useConversations(refreshTrigger: number): {
  conversations: ConversationSummary[];
  loading: boolean;
} {
  const [conversations, setConversations] = useState<ConversationSummary[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    listConversations()
      .then(list => {
        if (!cancelled) setConversations(list);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => { cancelled = true; };
  }, [refreshTrigger]);

  return { conversations, loading };
}
