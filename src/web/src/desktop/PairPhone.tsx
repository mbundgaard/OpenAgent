import { useEffect, useRef } from 'react';
import QRCode from 'qrcode';
import { getToken } from '../auth/token';
import styles from './PairPhone.module.css';

interface Props {
  onClose: () => void;
}

function buildPairUrl(): string {
  const base = window.location.origin;
  const token = getToken();
  return token ? `${base}/#token=${token}` : base;
}

export function PairPhone({ onClose }: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const backdropRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!canvasRef.current) return;
    QRCode.toCanvas(canvasRef.current, buildPairUrl(), {
      width: 256,
      margin: 2,
      color: { dark: '#000000', light: '#ffffff' },
    });
  }, []);

  function handleBackdropClick(e: React.MouseEvent) {
    if (e.target === backdropRef.current) onClose();
  }

  return (
    <div ref={backdropRef} className={styles.backdrop} onClick={handleBackdropClick}>
      <div className={styles.modal}>
        <div className={styles.header}>
          <span className={styles.title}>Pair Phone</span>
          <button className={styles.closeBtn} onClick={onClose}>x</button>
        </div>
        <div className={styles.body}>
          <canvas ref={canvasRef} className={styles.qr} />
          <p className={styles.hint}>Scan with your phone to open the app</p>
        </div>
      </div>
    </div>
  );
}
