import { useMemo, useState } from 'react';
import styles from './JsonlViewer.module.css';

interface Props {
  content: string;
}

interface LogEntry {
  line: number;
  raw: string;
  parsed: Record<string, unknown> | null;
}

/** Renders .jsonl files as structured log entries. Supports Serilog compact JSON format. */
export function JsonlViewer({ content }: Props) {
  const entries = useMemo(() => {
    return content
      .split('\n')
      .map((raw, i): LogEntry => {
        const trimmed = raw.trim();
        if (!trimmed) return { line: i + 1, raw, parsed: null };
        try {
          return { line: i + 1, raw: trimmed, parsed: JSON.parse(trimmed) };
        } catch {
          return { line: i + 1, raw: trimmed, parsed: null };
        }
      })
      .filter(e => e.raw.trim().length > 0);
  }, [content]);

  return (
    <div className={styles.container}>
      <div className={styles.header}>
        <span className={styles.count}>{entries.length} entries</span>
      </div>
      <div className={styles.entries}>
        {entries.map(entry => (
          <LogEntryRow key={entry.line} entry={entry} />
        ))}
      </div>
    </div>
  );
}

function LogEntryRow({ entry }: { entry: LogEntry }) {
  const [expanded, setExpanded] = useState(false);

  if (!entry.parsed) {
    return (
      <div className={styles.row}>
        <span className={styles.lineNum}>{entry.line}</span>
        <pre className={styles.rawLine}>{entry.raw}</pre>
      </div>
    );
  }

  const obj = entry.parsed;
  // Serilog compact JSON: @t = timestamp, @l = level, @mt = message template, @m = rendered message
  const timestamp = formatTimestamp(obj['@t'] as string | undefined);
  const level = (obj['@l'] as string | undefined) ?? 'Information';
  const message = (obj['@m'] as string | undefined) ?? (obj['@mt'] as string | undefined) ?? '';

  return (
    <div className={`${styles.row} ${expanded ? styles.expanded : ''}`}>
      <div className={styles.summary} onClick={() => setExpanded(!expanded)}>
        <span className={styles.lineNum}>{entry.line}</span>
        <span className={styles.chevron}>{expanded ? '\u25BC' : '\u25B6'}</span>
        <span className={styles.timestamp}>{timestamp}</span>
        <span className={`${styles.level} ${styles[`level${level}`] ?? ''}`}>{levelAbbr(level)}</span>
        <span className={styles.message}>{message}</span>
      </div>
      {expanded && (
        <pre className={styles.json}>{JSON.stringify(obj, null, 2)}</pre>
      )}
    </div>
  );
}

function formatTimestamp(ts: string | undefined): string {
  if (!ts) return '';
  try {
    const d = new Date(ts);
    return d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3 });
  } catch {
    return ts;
  }
}

function levelAbbr(level: string): string {
  switch (level) {
    case 'Verbose': return 'VRB';
    case 'Debug': return 'DBG';
    case 'Information': return 'INF';
    case 'Warning': return 'WRN';
    case 'Error': return 'ERR';
    case 'Fatal': return 'FTL';
    default: return level.substring(0, 3).toUpperCase();
  }
}
