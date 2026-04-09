import { useEffect, useState } from 'react';
import type { ConversationDetail } from '../../conversations/api';
import { getConversation, deleteConversation } from '../../conversations/api';
import { useConversation } from '../hooks/useConversation';
import { useTextStream } from '../hooks/useTextStream';
import { useVoiceSession } from '../hooks/useVoiceSession';
import { MessageList } from './MessageList';
import { Composer } from './Composer';
import styles from '../ChatApp.module.css';

interface Props {
  conversationId: string;
  onDeleted: () => void;
  onActivity: () => void;
}

export function ConversationView({ conversationId, onDeleted, onActivity }: Props) {
  const { messages, appendMessage, updateLastMessageContent } = useConversation(conversationId);
  const [detail, setDetail] = useState<ConversationDetail | null>(null);

  // Load detail (for source label). 404 = new conversation, no detail.
  useEffect(() => {
    let cancelled = false;
    setDetail(null);
    getConversation(conversationId)
      .then(d => { if (!cancelled) setDetail(d); })
      .catch(() => { if (!cancelled) setDetail(null); });
    return () => { cancelled = true; };
  }, [conversationId]);

  const textStream = useTextStream(conversationId, {
    onUserMessage: appendMessage,
    onAssistantStart: appendMessage,
    onAssistantDelta: updateLastMessageContent,
    onDone: onActivity
  });

  const voice = useVoiceSession(conversationId, {
    onAppendMessage: appendMessage,
    onUpdateLastMessageContent: updateLastMessageContent,
    onSessionEnd: onActivity
  });

  const handleDelete = async () => {
    try {
      await deleteConversation(conversationId);
      onDeleted();
    } catch (error) {
      console.error(`Failed to delete conversation ${conversationId}:`, error);
    }
  };

  return (
    <div className={styles.main}>
      <div className={styles.header}>
        <span className={styles.headerSource}>{detail?.source ?? 'new'}</span>
        <button className={styles.headerDelete} onClick={handleDelete}>Delete</button>
      </div>
      <MessageList messages={messages} />
      <Composer
        voiceState={voice.state}
        textStreaming={textStream.streaming}
        voiceError={voice.error}
        onSendText={textStream.send}
        onStartVoice={voice.start}
        onStopVoice={voice.stop}
      />
    </div>
  );
}
