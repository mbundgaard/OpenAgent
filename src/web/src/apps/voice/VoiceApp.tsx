import { useCallback, useEffect, useRef, useState } from 'react';
import { getToken } from '../../auth/token';
import styles from './VoiceApp.module.css';

/** Fallback sample rate if the server never sends session_ready. */
const FALLBACK_SAMPLE_RATE = 24000;

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
  const outputRateRef = useRef<number>(FALLBACK_SAMPLE_RATE);

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

    const buffer = ctx.createBuffer(1, float32.length, outputRateRef.current);
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
    const url = `${protocol}//${window.location.host}/ws/conversations/${conversationId}/voice?api_key=${token}`;
    console.log('[Voice] connecting WebSocket:', url);

    const ws = new WebSocket(url);
    ws.binaryType = 'arraybuffer';
    wsRef.current = ws;

    let micStarted = false;

    ws.onopen = () => {
      console.log('[Voice] WebSocket open — waiting for session_ready');
      setError(null);
    };

    ws.onmessage = async (event) => {
      // Binary frame = audio from assistant
      if (event.data instanceof ArrayBuffer) {
        setState('assistantSpeaking');
        enqueueAudio(event.data);
        return;
      }

      // Text frame = JSON control event
      const data = JSON.parse(event.data as string);
      console.log('[Voice] received event:', data.type, data);

      switch (data.type) {
        case 'session_ready':
          if (micStarted) break;
          if (data.input_codec !== 'pcm16' || data.output_codec !== 'pcm16') {
            setError(`Unsupported codec: ${data.input_codec}/${data.output_codec}. Only pcm16 is supported by this client.`);
            ws.close();
            return;
          }
          try {
            console.log('[Voice] starting microphone at', data.input_sample_rate, '→', data.output_sample_rate, 'Hz');
            await startMicrophone(data.input_sample_rate ?? FALLBACK_SAMPLE_RATE, data.output_sample_rate ?? FALLBACK_SAMPLE_RATE);
            micStarted = true;
            setState('listening');
          } catch (err) {
            console.error('[Voice] mic init failed:', err);
            setError('Microphone access denied');
            ws.close();
          }
          break;

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

    ws.onclose = (event) => {
      console.log('[Voice] WebSocket closed:', event.code, event.reason);
      setState('idle');
    };

    ws.onerror = (event) => {
      console.error('[Voice] WebSocket error:', event);
      setError('Connection failed');
      setState('idle');
    };
    // startMicrophone intentionally omitted — it's a stable useCallback([]) reference and
    // adding it to deps causes a TDZ error because it's declared below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enqueueAudio, flushPlayback]);

  // -- Microphone capture ------------------------------------------------------

  const startMicrophone = useCallback(async (inputRate: number, outputRate: number) => {
    const ctx = new AudioContext({ sampleRate: outputRate });
    audioCtxRef.current = ctx;
    outputRateRef.current = outputRate;

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
      audio: { sampleRate: inputRate, channelCount: 1, echoCancellation: true, noiseSuppression: true },
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

  const handleStart = useCallback(() => {
    console.log('[Voice] handleStart called — opening WebSocket, mic starts on session_ready');
    setError(null);
    setUserTranscript('');
    setAssistantTranscript('');
    conversationIdRef.current = crypto.randomUUID();
    connectWebSocket();
  }, [connectWebSocket]);

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
