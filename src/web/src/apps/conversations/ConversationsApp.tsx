import { useCallback, useEffect, useRef, useState } from 'react';
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
  const [showToolCalls, setShowToolCalls] = useState(false);
  const messagesEndRef = useRef<HTMLDivElement>(null);

  // Scroll to bottom when messages load
  useEffect(() => {
    const el = messagesEndRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages, showToolCalls]);

  // Editing state
  const [editing, setEditing] = useState(false);
  const [editSource, setEditSource] = useState('');
  const [editTextProvider, setEditTextProvider] = useState('');
  const [editTextModel, setEditTextModel] = useState('');
  const [editVoiceProvider, setEditVoiceProvider] = useState('');
  const [editVoiceModel, setEditVoiceModel] = useState('');
  const [editIntention, setEditIntention] = useState('');
  const [editMentionFilter, setEditMentionFilter] = useState('');
  const [textProviders, setTextProviders] = useState<string[]>([]);
  const [voiceProviders, setVoiceProviders] = useState<string[]>([]);
  const [modelsByProvider, setModelsByProvider] = useState<Record<string, string[]>>({});
  const [saving, setSaving] = useState(false);

  const refresh = useCallback(() => {
    setLoading(true);
    listConversations()
      .then(convs => setConversations(convs.sort((a, b) =>
        (b.last_activity ?? b.created_at).localeCompare(a.last_activity ?? a.created_at))))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  // Load provider models for editing — filter by capability so text providers
  // only appear in the text dropdown and voice providers only in the voice dropdown.
  useEffect(() => {
    listProviders().then(async (infos) => {
      setTextProviders(infos.filter(p => p.capabilities.includes('text')).map(p => p.key));
      setVoiceProviders(infos.filter(p => p.capabilities.includes('voice')).map(p => p.key));
      const modelsMap: Record<string, string[]> = {};
      for (const info of infos) {
        try {
          const models = await getProviderModels(info.key);
          if (models.length > 0) modelsMap[info.key] = models;
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
    setEditTextProvider(detail.text_provider);
    setEditTextModel(detail.text_model);
    setEditVoiceProvider(detail.voice_provider);
    setEditVoiceModel(detail.voice_model);
    setEditIntention(detail.intention ?? '');
    setEditMentionFilter((detail.mention_filter ?? []).join(', '));
    setEditing(true);
  };

  const cancelEditing = () => setEditing(false);

  const saveEditing = async () => {
    if (!selected || !detail) return;
    setSaving(true);
    const currentIntention = detail.intention ?? '';
    const nextIntention = editIntention.trim();
    const currentMentionFilter = detail.mention_filter ?? [];
    const nextMentionFilter = editMentionFilter
      .split(',')
      .map(s => s.trim())
      .filter(s => s.length > 0);
    const mentionFilterChanged =
      nextMentionFilter.length !== currentMentionFilter.length ||
      nextMentionFilter.some((v, i) => v !== currentMentionFilter[i]);
    const updated = await updateConversation(selected, {
      source: editSource !== detail.source ? editSource : undefined,
      text_provider: editTextProvider !== detail.text_provider ? editTextProvider : undefined,
      text_model: editTextModel !== detail.text_model ? editTextModel : undefined,
      voice_provider: editVoiceProvider !== detail.voice_provider ? editVoiceProvider : undefined,
      voice_model: editVoiceModel !== detail.voice_model ? editVoiceModel : undefined,
      // Empty string clears the intention; undefined leaves it unchanged.
      intention: nextIntention !== currentIntention ? nextIntention : undefined,
      // Empty array clears the mention filter; undefined leaves it unchanged.
      mention_filter: mentionFilterChanged ? nextMentionFilter : undefined,
    });
    setDetail(updated);
    setEditing(false);
    setSaving(false);
    refresh();
  };

  const availableTextModels = modelsByProvider[editTextProvider] ?? [];
  const availableVoiceModels = modelsByProvider[editVoiceProvider] ?? [];
  const lastPromptTokens = [...messages].reverse().find(m => m.prompt_tokens != null)?.prompt_tokens ?? 0;

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
                <span className={styles.source}>{c.display_name ?? c.source}</span>
              </div>
              <div className={styles.itemMeta}>
                <span>{c.text_model}</span>
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
              {/* Text + Voice provider/model + Name + Actions */}
              <div className={styles.detailRow}>
                {editing ? (
                  <>
                    <label className={styles.fieldGroup}>
                      <span className={styles.fieldLabel}>Text Provider</span>
                      <select className={styles.editSelect} value={editTextProvider}
                        onChange={e => { setEditTextProvider(e.target.value); setEditTextModel(''); }}>
                        <option value="">-- text provider --</option>
                        {textProviders.filter(p => modelsByProvider[p]?.length).map(p => (
                          <option key={p} value={p}>{p}</option>
                        ))}
                      </select>
                    </label>
                    <label className={styles.fieldGroup}>
                      <span className={styles.fieldLabel}>Text Model</span>
                      <select className={styles.editSelect} value={editTextModel}
                        onChange={e => setEditTextModel(e.target.value)} disabled={!availableTextModels.length}>
                        <option value="">-- text model --</option>
                        {availableTextModels.map(m => (
                          <option key={m} value={m}>{m}</option>
                        ))}
                      </select>
                    </label>
                    <label className={styles.fieldGroup}>
                      <span className={styles.fieldLabel}>Voice Provider</span>
                      <select className={styles.editSelect} value={editVoiceProvider}
                        onChange={e => { setEditVoiceProvider(e.target.value); setEditVoiceModel(''); }}>
                        <option value="">-- voice provider --</option>
                        {voiceProviders.filter(p => modelsByProvider[p]?.length).map(p => (
                          <option key={p} value={p}>{p}</option>
                        ))}
                      </select>
                    </label>
                    <label className={styles.fieldGroup}>
                      <span className={styles.fieldLabel}>Voice Model</span>
                      <select className={styles.editSelect} value={editVoiceModel}
                        onChange={e => setEditVoiceModel(e.target.value)} disabled={!availableVoiceModels.length}>
                        <option value="">-- voice model --</option>
                        {availableVoiceModels.map(m => (
                          <option key={m} value={m}>{m}</option>
                        ))}
                      </select>
                    </label>
                    <label className={styles.fieldGroup}>
                      <span className={styles.fieldLabel}>Name</span>
                      <input
                        className={styles.editInput}
                        value={editSource}
                        onChange={e => setEditSource(e.target.value)}
                        placeholder="Name"
                      />
                    </label>
                    <div className={styles.detailActions}>
                      <button className={styles.saveButton} onClick={saveEditing} disabled={saving}>
                        {saving ? '...' : 'Save'}
                      </button>
                      <button className={styles.cancelButton} onClick={cancelEditing}>Cancel</button>
                    </div>
                  </>
                ) : (
                  <>
                    <div>
                      <span className={styles.detailLabel}>
                        {detail.text_provider} / {detail.text_model}
                        {' · '}
                        {detail.voice_provider} / {detail.voice_model}
                        {' · '}
                        {detail.source}
                        {detail.display_name ? ` · ${detail.display_name}` : ''}
                      </span>
                      <div className={styles.detailId}>{detail.id}</div>
                    </div>
                    <div className={styles.detailActions}>
                      <button className={styles.editButton} onClick={startEditing}>Edit</button>
                      <button className={styles.deleteButton} onClick={() => handleDelete(selected)}>Delete</button>
                    </div>
                  </>
                )}
              </div>

              {/* Mention filter — names that gate which messages wake the agent */}
              {editing ? (
                <div className={styles.intentionRow}>
                  <span className={styles.intentionLabel}>Mentions</span>
                  <input
                    className={styles.editInput}
                    style={{ flex: 1 }}
                    value={editMentionFilter}
                    onChange={e => setEditMentionFilter(e.target.value)}
                    placeholder="Comma-separated names (empty = no filter, agent replies to everything)"
                  />
                </div>
              ) : detail.mention_filter && detail.mention_filter.length > 0 ? (
                <div className={styles.intentionRow}>
                  <span className={styles.intentionLabel}>Mentions</span>
                  <span className={styles.intentionText}>{detail.mention_filter.join(', ')}</span>
                </div>
              ) : null}

              {/* Intention — topic/scope for this conversation */}
              {editing ? (
                <div className={styles.intentionRow}>
                  <span className={styles.intentionLabel}>Intention</span>
                  <textarea
                    className={styles.intentionInput}
                    value={editIntention}
                    onChange={e => setEditIntention(e.target.value)}
                    placeholder="What is this conversation about? (leave empty for none)"
                    rows={2}
                  />
                </div>
              ) : detail.intention ? (
                <div className={styles.intentionRow}>
                  <span className={styles.intentionLabel}>Intention</span>
                  <span className={styles.intentionText}>{detail.intention}</span>
                </div>
              ) : null}

              {/* Stats */}
              <div className={styles.stats}>
                <div className={styles.stat}>
                  <span className={styles.statValue}>{formatTokens(lastPromptTokens)}</span>
                  <span className={styles.statLabel}>Last Prompt</span>
                </div>
                {detail.last_activity && (
                  <div className={styles.stat}>
                    <span className={styles.statValue}>{new Date(detail.last_activity).toLocaleTimeString()}</span>
                    <span className={styles.statLabel}>Last Active</span>
                  </div>
                )}
                <div className={styles.stat}>
                  <span className={styles.statValue}>
                    <input type="checkbox" checked={showToolCalls}
                      onChange={e => setShowToolCalls(e.target.checked)} />
                  </span>
                  <span className={styles.statLabel}>Tool Calls</span>
                </div>
                {detail.active_skills && detail.active_skills.length > 0 && (
                  <div className={styles.stat}>
                    <span className={styles.statValue}>
                      {detail.active_skills.map(skill => (
                        <span key={skill} className={styles.skillTag}>{skill}</span>
                      ))}
                    </span>
                    <span className={styles.statLabel}>Skills</span>
                  </div>
                )}
              </div>
            </div>
            <div className={styles.messages} ref={messagesEndRef}>
              {messages
                .filter(m => m.role !== 'system')
                .filter(m => showToolCalls || (m.role !== 'tool' && !m.tool_calls))
                .slice(-50)
                .map(msg => (
                <div key={msg.id} className={`${styles.message} ${styles[msg.role] ?? ''}`}>
                  <div className={styles.messageHeader}>
                    <span className={styles.headerLeft}>
                      <span className={styles.role}>{msg.role}</span>
                      {msg.sender && <span className={styles.sender}>{msg.sender}</span>}
                    </span>
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
                      <span>{msg.content}</span>
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
