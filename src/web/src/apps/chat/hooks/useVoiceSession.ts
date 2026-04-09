import { useCallback, useEffect, useRef, useState } from 'react';
import { getToken } from '../../../auth/token';
import type { ConversationMessage } from '../../conversations/api';

/** PCM16 mono at 24 kHz — matches the OpenAI Realtime API wire format. */
const SAMPLE_RATE = 24000;

export type VoiceState = 'idle' | 'listening' | 'userSpeaking' | 'thinking' | 'assistantSpeaking';

interface Callbacks {
  onAppendMessage: (msg: ConversationMessage) => void;
  onUpdateLastMessageContent: (content: string) => void;
}

/**
 * Manages a voice session for one conversation. Streams transcripts into the
 * conversation message list via callbacks using the source-flip rule: when
 * transcript_delta source flips (or after transcript_done), append a new
 * message; otherwise grow the last message's content.
 */
export function useVoiceSession(conversationId: string, callbacks: Callbacks): {
  state: VoiceState;
  start: () => Promise<void>;
  stop: () => void;
  error: string | null;
} {
  const [state, setState] = useState<VoiceState>('idle');
  const [error, setError] = useState<string | null>(null);

  const wsRef = useRef<WebSocket | null>(null);
  const audioCtxRef = useRef<AudioContext | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const workletRef = useRef<AudioWorkletNode | null>(null);

  // Source-flip routing state
  const lastSourceRef = useRef<'user' | 'assistant' | null>(null);
  const accumulatedRef = useRef<string>('');

  // Playback queue
  const playQueueRef = useRef<ArrayBuffer[]>([]);
  const playingRef = useRef(false);

  const callbacksRef = useRef(callbacks);
  callbacksRef.current = callbacks;

  // -- Audio playback --

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
    if (!playingRef.current) playNextChunk();
  }, [playNextChunk]);

  const flushPlayback = useCallback(() => {
    playQueueRef.current = [];
    playingRef.current = false;
  }, []);

  // -- Transcript routing --

  const handleTranscriptDelta = useCallback((source: 'user' | 'assistant', text: string) => {
    if (lastSourceRef.current !== source) {
      // Source flipped — open a new message bubble
      lastSourceRef.current = source;
      accumulatedRef.current = text;
      callbacksRef.current.onAppendMessage({
        id: crypto.randomUUID(),
        conversation_id: conversationId,
        role: source,
        content: text,
        created_at: new Date().toISOString(),
        tool_calls: null,
        tool_call_id: null,
        channel_message_id: null,
        prompt_tokens: null,
        completion_tokens: null,
        elapsed_ms: null,
      });
    } else {
      // Same source — grow the last bubble
      accumulatedRef.current += text;
      callbacksRef.current.onUpdateLastMessageContent(accumulatedRef.current);
    }
  }, [conversationId]);

  const handleTranscriptDone = useCallback(() => {
    // Reset for next turn — next delta will open a fresh bubble
    lastSourceRef.current = null;
    accumulatedRef.current = '';
  }, []);

  // -- Microphone capture --

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
      if (ws && ws.readyState === WebSocket.OPEN) ws.send(e.data);
    };

    source.connect(worklet);
    // Don't connect worklet to destination — we only capture, no local echo
  }, []);

  // -- Start / Stop --

  const start = useCallback(async () => {
    setError(null);
    lastSourceRef.current = null;
    accumulatedRef.current = '';

    try {
      await startMicrophone();
    } catch {
      setError('Microphone access denied');
      setState('idle');
      return;
    }

    const token = getToken();
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${protocol}//${window.location.host}/ws/conversations/${conversationId}/voice?api_key=${token}`;
    const ws = new WebSocket(url);
    ws.binaryType = 'arraybuffer';
    wsRef.current = ws;

    ws.onopen = () => { setState('listening'); setError(null); };

    ws.onmessage = (event) => {
      // Binary frame = audio from assistant
      if (event.data instanceof ArrayBuffer) {
        setState('assistantSpeaking');
        enqueueAudio(event.data);
        return;
      }

      // Text frame = JSON control event
      const data = JSON.parse(event.data as string) as { type: string; source?: 'user' | 'assistant'; text?: string; message?: string };
      switch (data.type) {
        case 'speech_started':
          flushPlayback();
          setState('userSpeaking');
          break;
        case 'speech_stopped':
          setState('thinking');
          break;
        case 'audio_done':
          setState('listening');
          break;
        case 'transcript_delta':
          handleTranscriptDelta(data.source!, data.text!);
          break;
        case 'transcript_done':
          handleTranscriptDone();
          break;
        case 'error':
          setError(data.message ?? 'Unknown error');
          break;
      }
    };

    ws.onclose = () => { setState('idle'); };
    ws.onerror = () => { setError('Connection failed'); setState('idle'); };
  }, [conversationId, startMicrophone, enqueueAudio, flushPlayback, handleTranscriptDelta, handleTranscriptDone]);

  const stop = useCallback(() => {
    const ws = wsRef.current;
    if (ws && ws.readyState === WebSocket.OPEN) ws.close();
    wsRef.current = null;

    streamRef.current?.getTracks().forEach(t => t.stop());
    streamRef.current = null;

    workletRef.current?.disconnect();
    workletRef.current = null;
    audioCtxRef.current?.close();
    audioCtxRef.current = null;

    flushPlayback();
    setState('idle');
  }, [flushPlayback]);

  // Cleanup on unmount or conversation change
  useEffect(() => {
    return () => {
      wsRef.current?.close();
      streamRef.current?.getTracks().forEach(t => t.stop());
      audioCtxRef.current?.close();
    };
  }, [conversationId]);

  return { state, start, stop, error };
}
