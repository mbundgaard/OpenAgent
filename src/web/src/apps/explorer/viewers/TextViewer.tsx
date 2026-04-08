import styles from './TextViewer.module.css';

interface Props {
  content: string;
}

/** Plain text viewer with line numbers. Default viewer for unknown file types. */
export function TextViewer({ content }: Props) {
  const lines = content.split('\n');

  return (
    <div className={styles.codeView}>
      <pre className={styles.lineNumbers} aria-hidden="true">
        {lines.map((_, i) => <span key={i}>{i + 1}</span>)}
      </pre>
      <pre className={styles.content}>{content}</pre>
    </div>
  );
}
