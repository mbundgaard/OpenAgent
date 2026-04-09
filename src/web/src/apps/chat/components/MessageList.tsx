import { useEffect, useRef } from 'react';
import Markdown from 'react-markdown';
import type { ConversationMessage } from '../../conversations/api';
import styles from '../ChatApp.module.css';

interface Props {
  messages: ConversationMessage[];
}

export function MessageList({ messages }: Props) {
  const endRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  // Filter out system messages and tool/tool_call messages — chat app shows only user/assistant content
  const visible = messages.filter(m => (m.role === 'user' || m.role === 'assistant') && m.content);

  return (
    <div className={styles.messages}>
      {visible.length === 0 && (
        <div className={styles.messagesEmpty}>Start a conversation...</div>
      )}
      {visible.map(msg => (
        <div key={msg.id} className={`${styles.message} ${styles[msg.role] ?? ''}`}>
          {msg.role === 'user' ? (
            <div className={styles.userBubble}>{msg.content}</div>
          ) : (
            <div className={styles.assistantContent}>
              <div className={styles.markdown}>
                <Markdown>{msg.content ?? ''}</Markdown>
              </div>
            </div>
          )}
        </div>
      ))}
      <div ref={endRef} />
    </div>
  );
}
