import { useCallback, useEffect, useRef, useState } from 'react';
import QRCode from 'qrcode';
import { getWhatsAppQr } from './api';
import styles from './QrCodeDisplay.module.css';

interface Props {
  connectionId: string;
  onConnected: () => void;
}

/**
 * Displays WhatsApp pairing QR code.
 * Polls the QR endpoint every 15 seconds until connected.
 */
export function QrCodeDisplay({ connectionId, onConnected }: Props) {
  const [status, setStatus] = useState<string>('loading');
  const [qrData, setQrData] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const stableOnConnected = useCallback(onConnected, [onConnected]);

  useEffect(() => {
    let cancelled = false;

    const poll = async () => {
      try {
        const res = await getWhatsAppQr(connectionId);
        if (cancelled) return;

        setStatus(res.status);
        setQrData(res.qr);
        setError(res.error);

        if (res.status === 'connected') {
          if (intervalRef.current) clearInterval(intervalRef.current);
          stableOnConnected();
        }
      } catch {
        if (!cancelled) setError('Failed to fetch QR status');
      }
    };

    poll();
    intervalRef.current = setInterval(poll, 15000);

    return () => {
      cancelled = true;
      if (intervalRef.current) clearInterval(intervalRef.current);
    };
  }, [connectionId, stableOnConnected]);

  // Render QR code to canvas when data changes
  useEffect(() => {
    if (!qrData || !canvasRef.current) return;

    QRCode.toCanvas(canvasRef.current, qrData, {
      width: 256,
      margin: 2,
      color: {
        dark: '#000000',
        light: '#ffffff',
      },
    }).catch(() => {
      // QR rendering failed — data might be malformed
      setError('Failed to render QR code');
    });
  }, [qrData]);

  return (
    <div className={styles.container}>
      <div className={styles.statusRow}>
        <span className={`${styles.indicator} ${status === 'connected' ? styles.connected : styles.pairing}`} />
        <span className={styles.statusText}>
          {status === 'connected' && 'Connected!'}
          {status === 'pairing' && 'Scan with WhatsApp'}
          {status === 'loading' && 'Loading...'}
          {status !== 'connected' && status !== 'pairing' && status !== 'loading' && `Status: ${status}`}
        </span>
      </div>

      {error && <p className={styles.error}>{error}</p>}

      {qrData && status !== 'connected' && (
        <div className={styles.qrSection}>
          <canvas ref={canvasRef} className={styles.qrCanvas} />
        </div>
      )}

      {status === 'connected' && (
        <p className={styles.hint}>Device linked successfully.</p>
      )}
    </div>
  );
}
