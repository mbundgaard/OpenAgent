import { useCallback, useEffect, useState } from 'react';
import Markdown from 'react-markdown';
import type { ConversationSummary, ConversationMessage } from './api';
import { listConversations, getMessages, deleteConversation } from './api';
import styles from './ConversationsApp.module.css';

export function ConversationsApp() {
  const [conversations, setConversations] = useState<ConversationSummary[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [messages, setMessages] = useState<ConversationMessage[]>([]);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(() => {
    setLoading(true);
    listConversations()
      .then(setConversations)
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  const handleSelect = async (id: string) => {
    setSelected(id);
    const msgs = await getMessages(id);
    setMessages(msgs);
  };

  const handleDelete = async (id: string) => {
    await deleteConversation(id);
    if (selected === id) {
      setSelected(null);
      setMessages([]);
    }
    refresh();
  };

  const selectedConv = conversations.find(c => c.id === selected);

  return (
    <div className={styles.container}>
      <div className={styles.sidebar}>
        <div className={styles.sidebarHeader}>
          <span className={styles.sidebarTitle}>Conversations</span>
          <button className={styles.refreshButton} onClick={refresh} title="Refresh">{'\u21BB'}</button>
        </div>
        <div className={styles.list}>
          {loading && <div className={styles.muted}>Loading...</div>}
          {!loading && conversations.length === 0 && (
            <div className={styles.muted}>No conversations</div>
          )}
          {conversations.map(c => (
            <button
              key={c.id}
              className={`${styles.item} ${selected === c.id ? styles.selected : ''}`}
              onClick={() => handleSelect(c.id)}
            >
              <div className={styles.itemTop}>
                <span className={styles.source}>{c.source}</span>
                <span className={styles.type}>{c.type}</span>
              </div>
              <div className={styles.itemMeta}>
                <span>{c.model}</span>
                <span>{new Date(c.created_at).toLocaleDateString()}</span>
              </div>
              <div className={styles.itemId}>{c.id.slice(0, 8)}...</div>
            </button>
          ))}
        </div>
      </div>
      <div className={styles.main}>
        {!selected ? (
          <div className={styles.empty}>Select a conversation</div>
        ) : (
          <>
            <div className={styles.header}>
              <div className={styles.headerInfo}>
                <span className={styles.headerSource}>{selectedConv?.source}</span>
                <span className={styles.headerModel}>{selectedConv?.provider} / {selectedConv?.model}</span>
              </div>
              <button className={styles.deleteButton} onClick={() => handleDelete(selected)}>Delete</button>
            </div>
            <div className={styles.messages}>
              {messages.filter(m => m.role !== 'system').map(msg => (
                <div key={msg.id} className={`${styles.message} ${styles[msg.role] ?? ''}`}>
                  <div className={styles.messageHeader}>
                    <span className={styles.role}>{msg.role}</span>
                    <span className={styles.time}>
                      {new Date(msg.created_at).toLocaleTimeString()}
                    </span>
                  </div>
                  <div className={styles.messageContent}>
                    {msg.role === 'assistant' && msg.content ? (
                      <div className={styles.markdown}><Markdown>{msg.content}</Markdown></div>
                    ) : msg.tool_call_id ? (
                      <div className={styles.toolResult}>
                        <span className={styles.toolLabel}>Tool result for {msg.tool_call_id.slice(0, 12)}</span>
                        <pre className={styles.toolContent}>{msg.content?.slice(0, 500)}</pre>
                      </div>
                    ) : msg.tool_calls ? (
                      <div className={styles.toolCall}>
                        <span className={styles.toolLabel}>Tool calls</span>
                        <pre className={styles.toolContent}>{msg.tool_calls}</pre>
                      </div>
                    ) : (
                      <span>{msg.content}</span>
                    )}
                  </div>
                </div>
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  );
}
