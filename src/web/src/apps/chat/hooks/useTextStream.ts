import { useCallback, useEffect, useRef, useState } from 'react';
import { getToken } from '../../../auth/token';
import type { ConversationMessage } from '../../conversations/api';

interface Callbacks {
  onUserMessage: (msg: ConversationMessage) => void;
  onAssistantStart: (msg: ConversationMessage) => void;
  onAssistantDelta: (accumulatedContent: string) => void;
  onDone: () => void;
}

/**
 * Opens a long-lived text WebSocket for one conversation. Sends text messages
 * via send(), streams responses through the callbacks. WS is closed and reopened
 * when conversationId changes.
 */
export interface ToolActivity {
  type: 'tool_call' | 'tool_result';
  name: string;
}

export function useTextStream(conversationId: string, callbacks: Callbacks): {
  send: (content: string) => void;
  streaming: boolean;
  connected: boolean;
  toolActivity: ToolActivity[];
} {
  const [streaming, setStreaming] = useState(false);
  const [connected, setConnected] = useState(false);
  const [toolActivity, setToolActivity] = useState<ToolActivity[]>([]);
  const wsRef = useRef<WebSocket | null>(null);
  const streamContentRef = useRef('');
  const callbacksRef = useRef(callbacks);
  // Queue a message to send once the WebSocket opens
  const pendingSendRef = useRef<string | null>(null);

  // Keep latest callbacks accessible without re-opening the WS
  callbacksRef.current = callbacks;

  useEffect(() => {
    const token = getToken();
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${protocol}//${window.location.host}/ws/conversations/${conversationId}/text?api_key=${token}`;

    const ws = new WebSocket(url);
    wsRef.current = ws;

    ws.onopen = () => {
      setConnected(true);
      // Flush any message queued while the WS was still connecting
      const pending = pendingSendRef.current;
      if (pending) {
        pendingSendRef.current = null;
        ws.send(JSON.stringify({ content: pending }));
      }
    };

    ws.onmessage = (event) => {
      const data = JSON.parse(event.data);
      switch (data.type) {
        case 'delta':
          // Server-pushed message (e.g. scheduled task) — no preceding send(),
          // so create a new assistant message slot before streaming into it.
          if (!streamContentRef.current && !streaming) {
            callbacksRef.current.onAssistantStart({
              id: crypto.randomUUID(),
              conversation_id: conversationId,
              role: 'assistant',
              content: '',
              created_at: new Date().toISOString(),
              tool_calls: null,
              tool_call_id: null,
              channel_message_id: null,
              prompt_tokens: null,
              completion_tokens: null,
              elapsed_ms: null
            });
          }
          streamContentRef.current += data.content;
          callbacksRef.current.onAssistantDelta(streamContentRef.current);
          break;
        case 'tool_call':
          setToolActivity(prev => [...prev, { type: 'tool_call', name: data.name }]);
          break;
        case 'tool_result':
          setToolActivity(prev => [...prev, { type: 'tool_result', name: data.name }]);
          break;
        case 'done':
          setStreaming(false);
          setToolActivity([]);
          streamContentRef.current = '';
          callbacksRef.current.onDone();
          break;
      }
    };

    ws.onclose = () => {
      setStreaming(false);
      setConnected(false);
    };

    return () => { ws.close(); wsRef.current = null; setConnected(false); };
  }, [conversationId]);

  const send = useCallback((content: string) => {
    const trimmed = content.trim();
    if (!trimmed) return;

    const ws = wsRef.current;
    if (!ws) return;

    // Optimistic user message
    callbacksRef.current.onUserMessage({
      id: crypto.randomUUID(),
      conversation_id: conversationId,
      role: 'user',
      content: trimmed,
      created_at: new Date().toISOString(),
      tool_calls: null,
      tool_call_id: null,
      channel_message_id: null,
      prompt_tokens: null,
      completion_tokens: null,
      elapsed_ms: null
    });

    // Open the assistant message that deltas will stream into
    callbacksRef.current.onAssistantStart({
      id: crypto.randomUUID(),
      conversation_id: conversationId,
      role: 'assistant',
      content: '',
      created_at: new Date().toISOString(),
      tool_calls: null,
      tool_call_id: null,
      channel_message_id: null,
      prompt_tokens: null,
      completion_tokens: null,
      elapsed_ms: null
    });

    setStreaming(true);
    setToolActivity([]);
    streamContentRef.current = '';

    // If the WebSocket is still connecting, queue the message for onopen
    if (ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ content: trimmed }));
    } else if (ws.readyState === WebSocket.CONNECTING) {
      pendingSendRef.current = trimmed;
    }
  }, [conversationId]);

  return { send, streaming, connected, toolActivity };
}
