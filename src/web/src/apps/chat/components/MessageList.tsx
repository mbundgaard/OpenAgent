import { useEffect, useRef } from 'react';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { ConversationMessage } from '../../conversations/api';
import type { ToolActivity } from '../hooks/useTextStream';
import styles from '../ChatApp.module.css';

interface Props {
  messages: ConversationMessage[];
  toolActivity?: ToolActivity[];
}

export function MessageList({ messages, toolActivity }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const endRef = useRef<HTMLDivElement>(null);
  const prevLengthRef = useRef(0);

  // Filter out system messages and tool/tool_call messages — chat app shows only user/assistant content
  const visible = messages.filter(m => (m.role === 'user' || m.role === 'assistant') && m.content);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    if (visible.length !== prevLengthRef.current) {
      // New message added — find the last user message and scroll it to the top
      prevLengthRef.current = visible.length;
      const userMessages = container.querySelectorAll(`.${styles.user}`);
      const lastUser = userMessages[userMessages.length - 1];
      if (lastUser) {
        lastUser.scrollIntoView({ behavior: 'smooth', block: 'start' });
      } else {
        endRef.current?.scrollIntoView({ behavior: 'smooth' });
      }
    } else {
      // Content updated (streaming delta) — scroll to bottom to follow the response
      endRef.current?.scrollIntoView({ behavior: 'smooth' });
    }
  }, [visible.length, messages]);

  return (
    <div className={styles.messages} ref={containerRef}>
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
                <Markdown remarkPlugins={[remarkGfm]}>{msg.content ?? ''}</Markdown>
              </div>
            </div>
          )}
        </div>
      ))}
      {toolActivity && toolActivity.length > 0 && (
        <div className={styles.tools}>
          {toolActivity.map((t, i) => (
            <div key={i} className={styles.toolItem}>
              <span className={styles.toolIcon}>
                {t.type === 'tool_call' ? '\u2699' : '\u2713'}
              </span>
              <span className={styles.toolName}>{t.name}</span>
            </div>
          ))}
        </div>
      )}
      <div ref={endRef} />
    </div>
  );
}
