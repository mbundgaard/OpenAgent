import { useEffect, useRef, useState } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { WebglAddon } from '@xterm/addon-webgl';
import '@xterm/xterm/css/xterm.css';
import { getToken } from '../../auth/token';
import styles from './TerminalApp.module.css';

export function TerminalApp() {
  const containerRef = useRef<HTMLDivElement>(null);
  const terminalRef = useRef<Terminal | null>(null);
  const fitAddonRef = useRef<FitAddon | null>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const [sessionId] = useState(() => crypto.randomUUID());

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    // Create terminal with theme matching the portal dark palette
    const terminal = new Terminal({
      cursorBlink: true,
      fontSize: 14,
      fontFamily: "'Cascadia Code', 'Fira Code', 'Consolas', monospace",
      theme: {
        background: '#0f0f1a',
        foreground: '#e8e8e8',
        cursor: '#e94560',
        selectionBackground: 'rgba(233, 69, 96, 0.3)',
        black: '#0f0f1a',
        red: '#e94560',
        green: '#50fa7b',
        yellow: '#f1fa8c',
        blue: '#6272a4',
        magenta: '#bd93f9',
        cyan: '#8be9fd',
        white: '#e8e8e8',
        brightBlack: '#555',
        brightRed: '#ff6e6e',
        brightGreen: '#69ff94',
        brightYellow: '#ffffa5',
        brightBlue: '#d6acff',
        brightMagenta: '#ff92df',
        brightCyan: '#a4ffff',
        brightWhite: '#ffffff',
      },
    });
    terminalRef.current = terminal;

    // Fit addon — auto-sizes terminal to container
    const fitAddon = new FitAddon();
    fitAddonRef.current = fitAddon;
    terminal.loadAddon(fitAddon);

    // Open terminal in the DOM
    terminal.open(container);

    // Try WebGL for performance, fall back silently
    try {
      terminal.loadAddon(new WebglAddon());
    } catch {
      // Canvas renderer is fine
    }

    // Initial fit
    fitAddon.fit();

    // Connect WebSocket
    const token = getToken();
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${protocol}//${window.location.host}/ws/terminal/${sessionId}?api_key=${token}`;
    const ws = new WebSocket(url);
    ws.binaryType = 'arraybuffer';
    wsRef.current = ws;

    // PTY output -> terminal display
    ws.onmessage = (event) => {
      if (event.data instanceof ArrayBuffer) {
        terminal.write(new Uint8Array(event.data));
      } else {
        // Text frame — could be an error message
        try {
          const msg = JSON.parse(event.data);
          if (msg.type === 'error') {
            terminal.write(`\r\n\x1b[31mError: ${msg.message}\x1b[0m\r\n`);
          }
        } catch {
          // Ignore unparseable text frames
        }
      }
    };

    ws.onclose = () => {
      terminal.write('\r\n\x1b[90m[Connection closed]\x1b[0m\r\n');
    };

    // Keystrokes -> WebSocket as binary
    const dataDisposable = terminal.onData((data) => {
      if (ws.readyState === WebSocket.OPEN) {
        ws.send(new TextEncoder().encode(data));
      }
    });

    // Send binary data (e.g., from paste) -> WebSocket
    const binaryDisposable = terminal.onBinary((data) => {
      if (ws.readyState === WebSocket.OPEN) {
        const bytes = new Uint8Array(data.length);
        for (let i = 0; i < data.length; i++) {
          bytes[i] = data.charCodeAt(i);
        }
        ws.send(bytes);
      }
    });

    // Resize handling — fit terminal to container, then notify server
    const sendResize = () => {
      fitAddon.fit();
      if (ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({
          type: 'resize',
          cols: terminal.cols,
          rows: terminal.rows,
        }));
      }
    };

    const resizeObserver = new ResizeObserver(() => {
      sendResize();
    });
    resizeObserver.observe(container);

    // Also send initial size once connected
    ws.onopen = () => {
      sendResize();
    };

    // Cleanup
    return () => {
      resizeObserver.disconnect();
      dataDisposable.dispose();
      binaryDisposable.dispose();
      ws.close();
      terminal.dispose();
    };
  }, [sessionId]);

  return <div ref={containerRef} className={styles.terminal} />;
}
