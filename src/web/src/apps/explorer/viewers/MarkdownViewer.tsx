import Markdown from 'react-markdown';
import styles from './MarkdownViewer.module.css';

interface Props {
  content: string;
}

/** Renders markdown files with formatted headings, code blocks, tables, etc. */
export function MarkdownViewer({ content }: Props) {
  return (
    <div className={styles.markdown}>
      <Markdown>{content}</Markdown>
    </div>
  );
}
