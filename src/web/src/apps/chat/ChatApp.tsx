import { useCallback, useEffect, useRef, useState } from 'react';
import { getToken } from '../../auth/token';
import styles from './ChatApp.module.css';

interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
}

interface ToolActivity {
  type: 'tool_call' | 'tool_result';
  name: string;
  detail: string;
}

export function ChatApp() {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState('');
  const [streaming, setStreaming] = useState(false);
  const [toolActivity, setToolActivity] = useState<ToolActivity[]>([]);
  const [conversationId] = useState(() => crypto.randomUUID());
  const wsRef = useRef<WebSocket | null>(null);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  // Ref to track accumulated assistant content for the current streaming response
  const streamContentRef = useRef('');

  const scrollToBottom = useCallback(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, []);

  useEffect(() => {
    scrollToBottom();
  }, [messages, toolActivity, scrollToBottom]);

  // Connect WebSocket
  useEffect(() => {
    const token = getToken();
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    // In dev, the API runs on a different port — use the Vite proxy or direct URL
    const host = window.location.hostname;
    const apiPort = '5264';
    const url = `${protocol}//${host}:${apiPort}/ws/conversations/${conversationId}/text?api_key=${token}`;

    const ws = new WebSocket(url);
    wsRef.current = ws;

    ws.onmessage = (event) => {
      const data = JSON.parse(event.data);

      switch (data.type) {
        case 'delta':
          streamContentRef.current += data.content;
          setMessages(prev => {
            const updated = [...prev];
            const last = updated[updated.length - 1];
            if (last?.role === 'assistant') {
              updated[updated.length - 1] = { ...last, content: streamContentRef.current };
            }
            return updated;
          });
          break;

        case 'tool_call':
          setToolActivity(prev => [...prev, {
            type: 'tool_call',
            name: data.name,
            detail: data.arguments,
          }]);
          break;

        case 'tool_result':
          setToolActivity(prev => [...prev, {
            type: 'tool_result',
            name: data.name,
            detail: data.result?.slice(0, 200) ?? '',
          }]);
          break;

        case 'done':
          setStreaming(false);
          setToolActivity([]);
          streamContentRef.current = '';
          setTimeout(() => inputRef.current?.focus(), 0);
          break;
      }
    };

    ws.onclose = () => {
      setStreaming(false);
    };

    return () => {
      ws.close();
    };
  }, [conversationId]);

  const sendMessage = useCallback(() => {
    const trimmed = input.trim();
    if (!trimmed || !wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;

    // Add user message
    setMessages(prev => [...prev, { role: 'user', content: trimmed }]);
    // Add empty assistant message for streaming
    setMessages(prev => [...prev, { role: 'assistant', content: '' }]);
    setInput('');
    setStreaming(true);
    setToolActivity([]);
    streamContentRef.current = '';

    wsRef.current.send(JSON.stringify({ content: trimmed }));
    setTimeout(() => inputRef.current?.focus(), 0);
  }, [input]);

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      sendMessage();
    }
  };

  return (
    <div className={styles.chat}>
      <div className={styles.messages}>
        {messages.length === 0 && (
          <div className={styles.empty}>Start a conversation...</div>
        )}
        {messages.map((msg, i) => (
          <div key={i} className={`${styles.message} ${styles[msg.role]}`}>
            <div className={styles.bubble}>
              {msg.content || (streaming && i === messages.length - 1 ? (
                <span className={styles.cursor}>|</span>
              ) : null)}
            </div>
          </div>
        ))}
        {toolActivity.length > 0 && (
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
        <div ref={messagesEndRef} />
      </div>
      <div className={styles.inputArea}>
        <textarea
          ref={inputRef}
          className={styles.input}
          value={input}
          onChange={e => setInput(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder="Type a message..."
          rows={1}
          disabled={streaming}
        />
        <button
          className={styles.sendButton}
          onClick={sendMessage}
          disabled={streaming || !input.trim()}
        >
          Send
        </button>
      </div>
    </div>
  );
}
