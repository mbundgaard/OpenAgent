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
  const lastUserRef = useRef<HTMLDivElement>(null);
  const lastMsgRef = useRef<HTMLDivElement>(null);
  const prevLengthRef = useRef(0);

  // Filter out system/tool messages and scheduled task injected prompts — chat shows only real user/assistant content
  const visible = messages.filter(m =>
    (m.role === 'user' || m.role === 'assistant') &&
    m.content &&
    !m.content.startsWith('[Scheduled task: '));

  // Find the index of the last user message for the scroll ref
  let lastUserIndex = -1;
  for (let i = visible.length - 1; i >= 0; i--) {
    if (visible[i].role === 'user') { lastUserIndex = i; break; }
  }

  useEffect(() => {
    if (visible.length !== prevLengthRef.current) {
      const wasEmpty = prevLengthRef.current === 0;
      prevLengthRef.current = visible.length;

      if (wasEmpty && lastMsgRef.current && containerRef.current) {
        // Initial load — scroll last message to bottom of container
        const containerRect = containerRef.current.getBoundingClientRect();
        const elRect = lastMsgRef.current.getBoundingClientRect();
        containerRef.current.scrollTop += elRect.bottom - containerRect.bottom;
      } else if (!wasEmpty) {
        const lastMsg = visible[visible.length - 1];
        if (lastMsg?.role === 'user' && lastUserRef.current && containerRef.current) {
          // User sent a message — scroll it to the top
          const containerRect = containerRef.current.getBoundingClientRect();
          const elRect = lastUserRef.current.getBoundingClientRect();
          containerRef.current.scrollTop += elRect.top - containerRect.top;
        }
      }
    }
  }, [visible.length]);

  return (
    <div className={styles.messages} ref={containerRef}>
      {visible.length === 0 && (
        <div className={styles.messagesEmpty}>Start a conversation...</div>
      )}
      {visible.map((msg, i) => (
        <div key={msg.id} ref={el => {
          if (i === lastUserIndex) lastUserRef.current = el;
          if (i === visible.length - 1) lastMsgRef.current = el;
        }} className={`${styles.message} ${styles[msg.role] ?? ''}`}>
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
      <div className={styles.messagesSpacer} />
    </div>
  );
}
