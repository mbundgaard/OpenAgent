import { useCallback, useEffect, useState } from 'react';
import Markdown from 'react-markdown';
import type { ConversationSummary, ConversationDetail, ConversationMessage } from './api';
import { listConversations, getConversation, getMessages, updateConversation, deleteConversation } from './api';
import { listProviders, getProviderModels } from '../settings/api';
import styles from './ConversationsApp.module.css';

function formatTokens(n: number): string {
  if (n < 1000) return String(n);
  if (n < 1_000_000) return `${(n / 1000).toFixed(1)}k`;
  return `${(n / 1_000_000).toFixed(2)}M`;
}

export function ConversationsApp() {
  const [conversations, setConversations] = useState<ConversationSummary[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<ConversationDetail | null>(null);
  const [messages, setMessages] = useState<ConversationMessage[]>([]);
  const [loading, setLoading] = useState(true);

  // Editing state
  const [editing, setEditing] = useState(false);
  const [editSource, setEditSource] = useState('');
  const [editProvider, setEditProvider] = useState('');
  const [editModel, setEditModel] = useState('');
  const [providers, setProviders] = useState<string[]>([]);
  const [modelsByProvider, setModelsByProvider] = useState<Record<string, string[]>>({});
  const [saving, setSaving] = useState(false);

  const refresh = useCallback(() => {
    setLoading(true);
    listConversations()
      .then(setConversations)
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  // Load provider models for editing
  useEffect(() => {
    listProviders().then(async (keys) => {
      setProviders(keys.filter(k => k !== 'agent'));
      const modelsMap: Record<string, string[]> = {};
      for (const pk of keys) {
        try {
          const models = await getProviderModels(pk);
          if (models.length > 0) modelsMap[pk] = models;
        } catch { /* no models */ }
      }
      setModelsByProvider(modelsMap);
    });
  }, []);

  const handleSelect = async (id: string) => {
    setSelected(id);
    setEditing(false);
    const [conv, msgs] = await Promise.all([getConversation(id), getMessages(id)]);
    setDetail(conv);
    setMessages(msgs);
  };

  const handleDelete = async (id: string) => {
    await deleteConversation(id);
    if (selected === id) {
      setSelected(null);
      setDetail(null);
      setMessages([]);
    }
    refresh();
  };

  const startEditing = () => {
    if (!detail) return;
    setEditSource(detail.source);
    setEditProvider(detail.provider);
    setEditModel(detail.model);
    setEditing(true);
  };

  const cancelEditing = () => setEditing(false);

  const saveEditing = async () => {
    if (!selected || !detail) return;
    setSaving(true);
    const updated = await updateConversation(selected, {
      source: editSource !== detail.source ? editSource : undefined,
      provider: editProvider !== detail.provider ? editProvider : undefined,
      model: editModel !== detail.model ? editModel : undefined,
    });
    setDetail(updated);
    setEditing(false);
    setSaving(false);
    refresh();
  };

  const availableModels = modelsByProvider[editProvider] ?? [];

  return (
    <div className={styles.container}>
      <div className={styles.sidebar}>
        <div className={styles.sidebarHeader}>
          <span className={styles.sidebarTitle}>
            Conversations{loading && <span className={styles.loadingDot}> ...</span>}
          </span>
          <button className={styles.refreshButton} onClick={refresh} title="Refresh">{'\u21BB'}</button>
        </div>
        <div className={styles.list}>
          {!loading && conversations.length === 0 && (
            <div className={styles.muted}>No conversations</div>
          )}
          {conversations.map(c => (
            <button
              key={c.id}
              className={`${styles.item} ${selected === c.id ? styles.selected : ''}`}
              onClick={() => handleSelect(c.id)}
            >
              <div className={styles.itemTop}>
                <span className={styles.source}>{c.source}</span>
                <span className={styles.type}>{c.type}</span>
              </div>
              <div className={styles.itemMeta}>
                <span>{c.model}</span>
                <span>{c.turn_count} turns</span>
              </div>
              <div className={styles.itemId}>{c.id.slice(0, 8)}...</div>
            </button>
          ))}
        </div>
      </div>
      <div className={styles.main}>
        {!selected || !detail ? (
          <div className={styles.empty}>Select a conversation</div>
        ) : (
          <>
            {/* Detail header */}
            <div className={styles.detailHeader}>
              <div className={styles.detailTop}>
                <div className={styles.detailTitle}>
                  {editing ? (
                    <input
                      className={styles.editInput}
                      value={editSource}
                      onChange={e => setEditSource(e.target.value)}
                      placeholder="Source name"
                    />
                  ) : (
                    <span className={styles.headerSource}>{detail.type}</span>
                  )}
                  <span className={styles.headerType}>{detail.source}</span>
                </div>
                <div className={styles.detailActions}>
                  {editing ? (
                    <>
                      <button className={styles.saveButton} onClick={saveEditing} disabled={saving}>
                        {saving ? '...' : 'Save'}
                      </button>
                      <button className={styles.cancelButton} onClick={cancelEditing}>Cancel</button>
                    </>
                  ) : (
                    <>
                      <button className={styles.editButton} onClick={startEditing}>Edit</button>
                      <button className={styles.deleteButton} onClick={() => handleDelete(selected)}>Delete</button>
                    </>
                  )}
                </div>
              </div>

              {/* Provider / Model */}
              <div className={styles.detailRow}>
                {editing ? (
                  <>
                    <select className={styles.editSelect} value={editProvider}
                      onChange={e => { setEditProvider(e.target.value); setEditModel(''); }}>
                      <option value="">-- provider --</option>
                      {providers.filter(p => modelsByProvider[p]?.length).map(p => (
                        <option key={p} value={p}>{p}</option>
                      ))}
                    </select>
                    <select className={styles.editSelect} value={editModel}
                      onChange={e => setEditModel(e.target.value)} disabled={!availableModels.length}>
                      <option value="">-- model --</option>
                      {availableModels.map(m => (
                        <option key={m} value={m}>{m}</option>
                      ))}
                    </select>
                  </>
                ) : (
                  <span className={styles.detailLabel}>{detail.provider} / {detail.model}</span>
                )}
              </div>

              {/* Stats */}
              <div className={styles.stats}>
                <div className={styles.stat}>
                  <span className={styles.statValue}>{detail.turn_count}</span>
                  <span className={styles.statLabel}>Turns</span>
                </div>
                <div className={styles.stat}>
                  <span className={styles.statValue}>{formatTokens(detail.total_prompt_tokens)}</span>
                  <span className={styles.statLabel}>Prompt</span>
                </div>
                <div className={styles.stat}>
                  <span className={styles.statValue}>{formatTokens(detail.total_completion_tokens)}</span>
                  <span className={styles.statLabel}>Completion</span>
                </div>
                <div className={styles.stat}>
                  <span className={styles.statValue}>{formatTokens(detail.total_prompt_tokens + detail.total_completion_tokens)}</span>
                  <span className={styles.statLabel}>Total</span>
                </div>
                {detail.last_activity && (
                  <div className={styles.stat}>
                    <span className={styles.statValue}>{new Date(detail.last_activity).toLocaleTimeString()}</span>
                    <span className={styles.statLabel}>Last Active</span>
                  </div>
                )}
              </div>
            </div>

            {/* Messages */}
            <div className={styles.messages}>
              {messages.filter(m => m.role !== 'system').map(msg => (
                <div key={msg.id} className={`${styles.message} ${styles[msg.role] ?? ''}`}>
                  <div className={styles.messageHeader}>
                    <span className={styles.role}>{msg.role}</span>
                    <span className={styles.time}>
                      {new Date(msg.created_at).toLocaleTimeString()}
                      {msg.elapsed_ms != null && <span className={styles.elapsed}> {msg.elapsed_ms}ms</span>}
                      {msg.prompt_tokens != null && (
                        <span className={styles.msgTokens}> {msg.prompt_tokens}+{msg.completion_tokens} tok</span>
                      )}
                    </span>
                  </div>
                  <div className={styles.messageContent}>
                    {msg.role === 'assistant' && msg.content ? (
                      <div className={styles.markdown}><Markdown>{msg.content}</Markdown></div>
                    ) : msg.tool_call_id ? (
                      <div className={styles.toolResult}>
                        <span className={styles.toolLabel}>Tool result for {msg.tool_call_id.slice(0, 12)}</span>
                        <pre className={styles.toolContent}>{msg.content?.slice(0, 500)}</pre>
                      </div>
                    ) : msg.tool_calls ? (
                      <div className={styles.toolCall}>
                        <span className={styles.toolLabel}>Tool calls</span>
                        <pre className={styles.toolContent}>{msg.tool_calls}</pre>
                      </div>
                    ) : (
                      <span>{msg.content}</span>
                    )}
                  </div>
                </div>
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  );
}
