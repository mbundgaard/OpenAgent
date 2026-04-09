import { useMemo } from 'react';
import Markdown from 'react-markdown';
import styles from './MarkdownViewer.module.css';

interface Props {
  content: string;
}

interface Frontmatter {
  entries: [string, string][];
  body: string;
}

/** Parses YAML frontmatter delimited by --- lines. */
function parseFrontmatter(content: string): Frontmatter {
  if (!content.startsWith('---')) return { entries: [], body: content };

  const endIndex = content.indexOf('\n---', 3);
  if (endIndex === -1) return { entries: [], body: content };

  const yaml = content.slice(4, endIndex).trim();
  const body = content.slice(endIndex + 4).trim();

  const entries: [string, string][] = [];
  for (const line of yaml.split('\n')) {
    const colon = line.indexOf(':');
    if (colon > 0) {
      const key = line.slice(0, colon).trim();
      const value = line.slice(colon + 1).trim();
      entries.push([key, value]);
    }
  }

  return { entries, body };
}

/** Renders markdown files with formatted headings, code blocks, tables, etc. */
export function MarkdownViewer({ content }: Props) {
  const { entries, body } = useMemo(() => parseFrontmatter(content), [content]);

  return (
    <div className={styles.markdown}>
      {entries.length > 0 && (
        <div className={styles.frontmatter}>
          {entries.map(([key, value]) => (
            <div key={key} className={styles.field}>
              <span className={styles.fieldKey}>{key}</span>
              <span className={styles.fieldValue}>{value}</span>
            </div>
          ))}
        </div>
      )}
      <Markdown>{body}</Markdown>
    </div>
  );
}
