import { useEffect, useRef, useState } from 'react';
import type { VoiceState } from '../hooks/useVoiceSession';
import styles from '../ChatApp.module.css';

interface Props {
  voiceState: VoiceState;
  textStreaming: boolean;
  voiceError: string | null;
  onSendText: (content: string) => void;
  onStartVoice: () => void;
  onStopVoice: () => void;
}

const STATUS_LABELS: Record<VoiceState, string> = {
  idle: '',
  listening: 'Listening...',
  userSpeaking: 'Listening...',
  thinking: 'Thinking...',
  assistantSpeaking: 'Speaking...'
};

export function Composer({ voiceState, textStreaming, voiceError, onSendText, onStartVoice, onStopVoice }: Props) {
  const [input, setInput] = useState('');
  const inputRef = useRef<HTMLTextAreaElement>(null);

  // Focus on mount (conversation selected) and when streaming ends or voice stops
  useEffect(() => {
    inputRef.current?.focus();
  }, []);

  useEffect(() => {
    if (!textStreaming && voiceState === 'idle') {
      inputRef.current?.focus();
    }
  }, [textStreaming, voiceState]);

  const voiceRunning = voiceState !== 'idle';
  const inputDisabled = voiceRunning || textStreaming;
  const hasText = input.trim().length > 0;

  // Action button mode
  type Mode = 'mic' | 'send' | 'stop';
  let mode: Mode;
  if (voiceRunning) mode = 'stop';
  else if (hasText) mode = 'send';
  else mode = 'mic';

  const handleAction = () => {
    if (mode === 'stop') {
      onStopVoice();
    } else if (mode === 'send') {
      const trimmed = input.trim();
      if (!trimmed) return;
      onSendText(trimmed);
      setInput('');
    } else {
      onStartVoice();
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey && mode === 'send') {
      e.preventDefault();
      handleAction();
    }
  };

  const actionDisabled =
    (mode === 'send' && (textStreaming || !hasText)) ||
    (mode === 'mic' && textStreaming);

  return (
    <>
      {voiceRunning && (
        <div className={styles.statusStrip}>
          <span className={styles.statusDot} />
          <span>{STATUS_LABELS[voiceState]}</span>
        </div>
      )}
      {voiceError && <div className={styles.composerError}>{voiceError}</div>}
      <div className={styles.composer}>
        <textarea
          ref={inputRef}
          className={styles.input}
          value={input}
          onChange={e => setInput(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder="Type a message..."
          rows={1}
          disabled={inputDisabled}
        />
        <button
          className={`${styles.actionButton} ${mode === 'stop' ? styles.stop : ''}`}
          onClick={handleAction}
          disabled={actionDisabled}
          title={mode === 'mic' ? 'Start voice' : mode === 'send' ? 'Send' : 'Stop voice'}
        >
          {mode === 'mic' && (
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M12 1a3 3 0 0 0-3 3v8a3 3 0 0 0 6 0V4a3 3 0 0 0-3-3z" />
              <path d="M19 10v2a7 7 0 0 1-14 0v-2" />
              <line x1="12" y1="19" x2="12" y2="23" />
              <line x1="8" y1="23" x2="16" y2="23" />
            </svg>
          )}
          {mode === 'send' && (
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <line x1="22" y1="2" x2="11" y2="13" />
              <polygon points="22 2 15 22 11 13 2 9 22 2" />
            </svg>
          )}
          {mode === 'stop' && (
            <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor">
              <rect x="6" y="6" width="12" height="12" rx="1" />
            </svg>
          )}
        </button>
      </div>
    </>
  );
}
