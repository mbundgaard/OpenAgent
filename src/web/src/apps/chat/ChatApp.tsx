import { useCallback, useState } from 'react';
import { ConversationSidebar } from './components/ConversationSidebar';
import { ConversationView } from './components/ConversationView';
import { useConversations } from './hooks/useConversations';
import styles from './ChatApp.module.css';

export function ChatApp() {
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [refreshTrigger, setRefreshTrigger] = useState(0);

  const { conversations, loading } = useConversations(refreshTrigger);

  const refresh = useCallback(() => setRefreshTrigger(t => t + 1), []);

  const handleNew = useCallback(() => {
    setSelectedId(crypto.randomUUID());
  }, []);

  const handleDeleted = useCallback((id: string) => {
    if (selectedId === id) setSelectedId(null);
    refresh();
  }, [selectedId, refresh]);

  const handleViewDeleted = useCallback(() => {
    setSelectedId(null);
    refresh();
  }, [refresh]);

  return (
    <div className={styles.chat}>
      <ConversationSidebar
        conversations={conversations}
        loading={loading}
        selectedId={selectedId}
        onSelect={setSelectedId}
        onNew={handleNew}
        onDeleted={handleDeleted}
      />
      {selectedId === null ? (
        <div className={styles.mainEmpty}>Select a conversation or start a new one</div>
      ) : (
        <ConversationView
          key={selectedId}
          conversationId={selectedId}
          onDeleted={handleViewDeleted}
          onActivity={refresh}
        />
      )}
    </div>
  );
}
