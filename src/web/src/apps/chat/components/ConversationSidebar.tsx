import type { ConversationSummary } from '../../conversations/api';
import { deleteConversation } from '../../conversations/api';
import styles from '../ChatApp.module.css';

interface Props {
  conversations: ConversationSummary[];
  loading: boolean;
  selectedId: string | null;
  onSelect: (id: string) => void;
  onNew: () => void;
  onDeleted: (id: string) => void;
}

export function ConversationSidebar({ conversations, loading, selectedId, onSelect, onNew, onDeleted }: Props) {
  const handleDelete = async (e: React.MouseEvent, id: string) => {
    e.stopPropagation();
    await deleteConversation(id);
    onDeleted(id);
  };

  return (
    <div className={styles.sidebar}>
      <div className={styles.sidebarHeader}>
        <span className={styles.sidebarTitle}>Conversations</span>
        <button className={styles.newButton} onClick={onNew} title="New conversation">+</button>
      </div>
      <div className={styles.list}>
        {loading && conversations.length === 0 && (
          <div className={styles.empty}>Loading...</div>
        )}
        {!loading && conversations.length === 0 && (
          <div className={styles.empty}>No conversations</div>
        )}
        {conversations.map(c => (
          <button
            key={c.id}
            className={`${styles.row} ${selectedId === c.id ? styles.selected : ''}`}
            onClick={() => onSelect(c.id)}
          >
            <div className={styles.rowSource}>{c.source}</div>
            <div className={styles.rowId}>{c.id.slice(0, 8)}...</div>
            <span className={styles.rowDelete} onClick={e => handleDelete(e, c.id)} title="Delete">×</span>
          </button>
        ))}
      </div>
    </div>
  );
}
