import { useMemo, useState } from 'react';
import styles from './JsonlViewer.module.css';

const PAGE_SIZE = 200;

interface Props {
  content: string;
}

interface LogEntry {
  line: number;
  raw: string;
  parsed: Record<string, unknown> | null;
}

/** Renders .jsonl files as structured log entries. Shows last 200 entries by default. */
export function JsonlViewer({ content }: Props) {
  const [visibleCount, setVisibleCount] = useState(PAGE_SIZE);

  // Split lines once, keep as raw strings — don't parse until needed
  const lines = useMemo(() => {
    const result: { line: number; raw: string }[] = [];
    const parts = content.split('\n');
    for (let i = 0; i < parts.length; i++) {
      const trimmed = parts[i].trim();
      if (trimmed) result.push({ line: i + 1, raw: trimmed });
    }
    return result;
  }, [content]);

  const totalCount = lines.length;
  const startIndex = Math.max(0, totalCount - visibleCount);
  const visibleLines = lines.slice(startIndex).reverse();
  const hasMore = startIndex > 0;

  return (
    <div className={styles.container}>
      <div className={styles.header}>
        <span className={styles.count}>
          {totalCount} entries{hasMore && ` (showing newest ${visibleLines.length})`}
        </span>
      </div>
      <div className={styles.entries}>
        {visibleLines.map(({ line, raw }) => (
          <LogEntryRow key={line} line={line} raw={raw} />
        ))}
        {hasMore && (
          <button
            className={styles.loadMore}
            onClick={() => setVisibleCount(v => v + PAGE_SIZE)}
          >
            Load {Math.min(PAGE_SIZE, startIndex)} older entries
          </button>
        )}
      </div>
    </div>
  );
}

function LogEntryRow({ line, raw }: { line: number; raw: string }) {
  const [expanded, setExpanded] = useState(false);

  // Parse lazily on first render
  const parsed = useMemo<Record<string, unknown> | null>(() => {
    try { return JSON.parse(raw); }
    catch { return null; }
  }, [raw]);

  if (!parsed) {
    return (
      <div className={styles.row}>
        <span className={styles.lineNum}>{line}</span>
        <pre className={styles.rawLine}>{raw}</pre>
      </div>
    );
  }

  const timestamp = formatTimestamp(parsed['@t'] as string | undefined);
  const level = (parsed['@l'] as string | undefined) ?? 'Information';
  const message = (parsed['@m'] as string | undefined) ?? (parsed['@mt'] as string | undefined) ?? '';

  return (
    <div className={`${styles.row} ${expanded ? styles.expanded : ''}`}>
      <div className={styles.summary} onClick={() => setExpanded(!expanded)}>
        <span className={styles.lineNum}>{line}</span>
        <span className={styles.chevron}>{expanded ? '\u25BC' : '\u25B6'}</span>
        <span className={styles.timestamp}>{timestamp}</span>
        <span className={`${styles.level} ${styles[`level${level}`] ?? ''}`}>{levelAbbr(level)}</span>
        <span className={styles.message}>{message}</span>
      </div>
      {expanded && (
        <pre className={styles.json}>{JSON.stringify(parsed, null, 2)}</pre>
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
