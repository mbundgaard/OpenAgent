import { useCallback, useEffect, useRef, useState } from 'react';
import { getToken } from '../../auth/token';
import styles from './VoiceApp.module.css';

/** PCM16 mono at 24 kHz — matches the OpenAI Realtime API wire format. */
const SAMPLE_RATE = 24000;

type VoiceState = 'idle' | 'listening' | 'userSpeaking' | 'thinking' | 'assistantSpeaking';

const STATUS_LABELS: Record<VoiceState, string> = {
  idle: '',
  listening: 'Listening...',
  userSpeaking: 'Listening...',
  thinking: 'Thinking...',
  assistantSpeaking: 'Speaking...',
};

export function VoiceApp() {
  const [state, setState] = useState<VoiceState>('idle');
  const [error, setError] = useState<string | null>(null);
  const [userTranscript, setUserTranscript] = useState('');
  const [assistantTranscript, setAssistantTranscript] = useState('');

  const wsRef = useRef<WebSocket | null>(null);
  const audioCtxRef = useRef<AudioContext | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const workletRef = useRef<AudioWorkletNode | null>(null);
  const conversationIdRef = useRef(crypto.randomUUID());

  // Playback queue — incoming PCM chunks played sequentially
  const playQueueRef = useRef<ArrayBuffer[]>([]);
  const playingRef = useRef(false);

  // -- Audio playback ----------------------------------------------------------

  const playNextChunk = useCallback(() => {
    const ctx = audioCtxRef.current;
    if (!ctx || playQueueRef.current.length === 0) {
      playingRef.current = false;
      return;
    }
    playingRef.current = true;

    const chunk = playQueueRef.current.shift()!;
    const int16 = new Int16Array(chunk);
    const float32 = new Float32Array(int16.length);
    for (let i = 0; i < int16.length; i++) {
      float32[i] = int16[i] / 32768;
    }

    const buffer = ctx.createBuffer(1, float32.length, SAMPLE_RATE);
    buffer.copyToChannel(float32, 0);

    const source = ctx.createBufferSource();
    source.buffer = buffer;
    source.connect(ctx.destination);
    source.onended = playNextChunk;
    source.start();
  }, []);

  const enqueueAudio = useCallback((data: ArrayBuffer) => {
    playQueueRef.current.push(data);
    if (!playingRef.current) {
      playNextChunk();
    }
  }, [playNextChunk]);

  const flushPlayback = useCallback(() => {
    playQueueRef.current = [];
    playingRef.current = false;
  }, []);

  // -- WebSocket ---------------------------------------------------------------

  const connectWebSocket = useCallback(() => {
    const token = getToken();
    const conversationId = conversationIdRef.current;
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const host = window.location.hostname;
    const apiPort = '5264';
    const url = `${protocol}//${host}:${apiPort}/ws/conversations/${conversationId}/voice?api_key=${token}`;

    const ws = new WebSocket(url);
    ws.binaryType = 'arraybuffer';
    wsRef.current = ws;

    ws.onopen = () => {
      setState('listening');
      setError(null);
    };

    ws.onmessage = (event) => {
      // Binary frame = audio from assistant
      if (event.data instanceof ArrayBuffer) {
        setState('assistantSpeaking');
        enqueueAudio(event.data);
        return;
      }

      // Text frame = JSON control event
      const data = JSON.parse(event.data as string);

      switch (data.type) {
        case 'speech_started':
          flushPlayback();
          setState('userSpeaking');
          setAssistantTranscript('');
          break;

        case 'speech_stopped':
          setState('thinking');
          break;

        case 'audio_done':
          setState('listening');
          break;

        case 'transcript_delta':
          if (data.source === 'user') {
            setUserTranscript(prev => prev + data.text);
          } else {
            setAssistantTranscript(prev => prev + data.text);
          }
          break;

        case 'transcript_done':
          if (data.source === 'user') {
            setUserTranscript(data.text);
          } else {
            setAssistantTranscript(data.text);
          }
          break;

        case 'error':
          setError(data.message);
          break;
      }
    };

    ws.onclose = () => {
      setState('idle');
    };

    ws.onerror = () => {
      setError('Connection failed');
      setState('idle');
    };
  }, [enqueueAudio, flushPlayback]);

  // -- Microphone capture ------------------------------------------------------

  const startMicrophone = useCallback(async () => {
    const ctx = new AudioContext({ sampleRate: SAMPLE_RATE });
    audioCtxRef.current = ctx;

    // Load the PCM capture worklet inline via a blob URL
    const workletCode = `
      class PcmCaptureProcessor extends AudioWorkletProcessor {
        process(inputs) {
          const input = inputs[0];
          if (input.length > 0) {
            const float32 = input[0];
            const int16 = new Int16Array(float32.length);
            for (let i = 0; i < float32.length; i++) {
              const s = Math.max(-1, Math.min(1, float32[i]));
              int16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
            }
            this.port.postMessage(int16.buffer, [int16.buffer]);
          }
          return true;
        }
      }
      registerProcessor('pcm-capture', PcmCaptureProcessor);
    `;
    const blob = new Blob([workletCode], { type: 'application/javascript' });
    const workletUrl = URL.createObjectURL(blob);
    await ctx.audioWorklet.addModule(workletUrl);
    URL.revokeObjectURL(workletUrl);

    const stream = await navigator.mediaDevices.getUserMedia({
      audio: { sampleRate: SAMPLE_RATE, channelCount: 1, echoCancellation: true, noiseSuppression: true },
    });
    streamRef.current = stream;

    const source = ctx.createMediaStreamSource(stream);
    const worklet = new AudioWorkletNode(ctx, 'pcm-capture');
    workletRef.current = worklet;

    worklet.port.onmessage = (e: MessageEvent<ArrayBuffer>) => {
      const ws = wsRef.current;
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(e.data);
      }
    };

    source.connect(worklet);
    // Don't connect worklet to destination — we only capture, no local echo
  }, []);

  // -- Start / Stop ------------------------------------------------------------

  const handleStart = useCallback(async () => {
    setError(null);
    setUserTranscript('');
    setAssistantTranscript('');
    conversationIdRef.current = crypto.randomUUID();

    try {
      await startMicrophone();
      connectWebSocket();
    } catch {
      setError('Microphone access denied');
      setState('idle');
    }
  }, [startMicrophone, connectWebSocket]);

  const handleStop = useCallback(() => {
    // Close WebSocket
    const ws = wsRef.current;
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.close();
    }
    wsRef.current = null;

    // Stop mic
    streamRef.current?.getTracks().forEach(t => t.stop());
    streamRef.current = null;

    // Tear down audio context
    workletRef.current?.disconnect();
    workletRef.current = null;
    audioCtxRef.current?.close();
    audioCtxRef.current = null;

    flushPlayback();
    setState('idle');
  }, [flushPlayback]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      wsRef.current?.close();
      streamRef.current?.getTracks().forEach(t => t.stop());
      audioCtxRef.current?.close();
    };
  }, []);

  const stateClass = styles[state] ?? '';

  return (
    <div className={styles.voice}>
      <div className={`${styles.orbContainer} ${stateClass}`}>
        <div className={styles.ring} />
        <div className={styles.ring} />
        <div className={styles.ring} />
        <div className={styles.orb}>
          <div className={styles.thinkingDots}>
            <div className={styles.thinkingDot} />
            <div className={styles.thinkingDot} />
            <div className={styles.thinkingDot} />
          </div>
        </div>
      </div>

      <div className={styles.statusLabel}>{STATUS_LABELS[state]}</div>

      <div className={styles.transcript}>
        {userTranscript && (
          <div className={styles.transcriptUser}>{userTranscript}</div>
        )}
        {assistantTranscript && (
          <div className={styles.transcriptAssistant}>{assistantTranscript}</div>
        )}
      </div>

      {state === 'idle' ? (
        <button className={`${styles.controlButton} ${styles.startButton}`} onClick={handleStart}>
          Start
        </button>
      ) : (
        <button className={`${styles.controlButton} ${styles.stopButton}`} onClick={handleStop}>
          Stop
        </button>
      )}

      {error && <div className={styles.error}>{error}</div>}
    </div>
  );
}
