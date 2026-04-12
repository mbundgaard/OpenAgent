import { useCallback, useEffect, useState } from 'react';
import type { ScheduledTask, ScheduleConfig } from './api';
import { listTasks, getTask, createTask, updateTask, deleteTask, runTaskNow } from './api';
import { listConversations, type ConversationSummary } from '../conversations/api';
import styles from './ScheduledTasksApp.module.css';

type ScheduleKind = 'cron' | 'intervalMs' | 'at';

interface FormState {
  name: string;
  description: string;
  prompt: string;
  enabled: boolean;
  deleteAfterRun: boolean;
  conversationId: string;
  scheduleKind: ScheduleKind;
  cron: string;
  timezone: string;
  intervalMs: string;
  at: string;
}

function emptyForm(): FormState {
  return {
    name: '',
    description: '',
    prompt: '',
    enabled: true,
    deleteAfterRun: false,
    conversationId: '',
    scheduleKind: 'cron',
    cron: '0 9 * * *',
    timezone: '',
    intervalMs: '',
    at: '',
  };
}

function formFromTask(task: ScheduledTask): FormState {
  const kind: ScheduleKind = task.schedule.cron != null ? 'cron'
    : task.schedule.intervalMs != null ? 'intervalMs'
    : 'at';
  return {
    name: task.name,
    description: task.description ?? '',
    prompt: task.prompt,
    enabled: task.enabled,
    deleteAfterRun: task.deleteAfterRun,
    conversationId: task.conversationId ?? '',
    scheduleKind: kind,
    cron: task.schedule.cron ?? '',
    timezone: task.schedule.timezone ?? '',
    intervalMs: task.schedule.intervalMs?.toString() ?? '',
    at: task.schedule.at ?? '',
  };
}

function scheduleFromForm(form: FormState): ScheduleConfig {
  switch (form.scheduleKind) {
    case 'cron':
      return { cron: form.cron, timezone: form.timezone || null };
    case 'intervalMs':
      return { intervalMs: Number(form.intervalMs) };
    case 'at':
      return { at: form.at };
  }
}

function formatSchedule(schedule: ScheduleConfig): string {
  if (schedule.cron) return `cron: ${schedule.cron}${schedule.timezone ? ` (${schedule.timezone})` : ''}`;
  if (schedule.intervalMs) return `every ${schedule.intervalMs}ms`;
  if (schedule.at) return `once at ${new Date(schedule.at).toLocaleString()}`;
  return 'unknown';
}

function formatTimestamp(iso: string | null | undefined): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString();
}

function formatConversationLabel(conv: ConversationSummary): string {
  // Prefer the human-readable display name if the channel has populated one.
  if (conv.display_name) return conv.display_name;
  // Channel-bound fallback: "telegram: chat 12345"
  if (conv.channel_type && conv.channel_chat_id) {
    return `${conv.channel_type}: ${conv.channel_chat_id}`;
  }
  // App/other: show source + short id
  return `${conv.source} (${conv.id.slice(0, 8)})`;
}

function labelForConversationId(id: string | null | undefined, conversations: ConversationSummary[]): string {
  if (!id) return '(auto-created on first run, silent)';
  const conv = conversations.find(c => c.id === id);
  if (!conv) return `${id} (not found)`;
  return formatConversationLabel(conv);
}

export function ScheduledTasksApp() {
  const [tasks, setTasks] = useState<ScheduledTask[]>([]);
  const [conversations, setConversations] = useState<ConversationSummary[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<ScheduledTask | null>(null);
  const [loading, setLoading] = useState(true);
  const [mode, setMode] = useState<'view' | 'edit' | 'create'>('view');
  const [form, setForm] = useState<FormState>(emptyForm);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(() => {
    setLoading(true);
    Promise.all([listTasks(), listConversations()])
      .then(([ts, convs]) => {
        setTasks(ts.sort((a, b) => a.name.localeCompare(b.name)));
        setConversations(convs.sort((a, b) =>
          (b.last_activity ?? b.created_at).localeCompare(a.last_activity ?? a.created_at)));
      })
      .catch(err => setError(err.message))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  const handleSelect = async (id: string) => {
    setSelected(id);
    setMode('view');
    setError(null);
    const task = await getTask(id);
    setDetail(task);
  };

  const handleCreate = () => {
    setSelected(null);
    setDetail(null);
    setMode('create');
    setForm(emptyForm());
    setError(null);
  };

  const handleEdit = () => {
    if (!detail) return;
    setForm(formFromTask(detail));
    setMode('edit');
    setError(null);
  };

  const handleCancel = () => {
    setMode('view');
    setError(null);
  };

  const handleSave = async () => {
    setSaving(true);
    setError(null);
    try {
      if (mode === 'create') {
        const created = await createTask({
          id: crypto.randomUUID(),
          name: form.name,
          description: form.description || null,
          prompt: form.prompt,
          enabled: form.enabled,
          deleteAfterRun: form.deleteAfterRun,
          schedule: scheduleFromForm(form),
          conversationId: form.conversationId || null,
        });
        refresh();
        setSelected(created.id);
        setDetail(created);
        setMode('view');
      } else if (mode === 'edit' && selected) {
        const updated = await updateTask(selected, {
          name: form.name,
          description: form.description || null,
          prompt: form.prompt,
          enabled: form.enabled,
          schedule: scheduleFromForm(form),
          conversationId: form.conversationId || null,
        });
        refresh();
        setDetail(updated);
        setMode('view');
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async () => {
    if (!selected) return;
    if (!confirm(`Delete task "${detail?.name}"?`)) return;
    await deleteTask(selected);
    setSelected(null);
    setDetail(null);
    setMode('view');
    refresh();
  };

  const handleRunNow = async () => {
    if (!selected) return;
    try {
      await runTaskNow(selected);
      // Re-fetch to show updated last run state
      const task = await getTask(selected);
      setDetail(task);
      refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  const showForm = mode === 'create' || mode === 'edit';

  return (
    <div className={styles.container}>
      <div className={styles.sidebar}>
        <div className={styles.sidebarHeader}>
          <span className={styles.sidebarTitle}>
            Tasks{loading && <span className={styles.loadingDot}> ...</span>}
          </span>
          <div className={styles.sidebarActions}>
            <button className={styles.iconButton} onClick={handleCreate} title="New task">+</button>
            <button className={styles.iconButton} onClick={refresh} title="Refresh">{'\u21BB'}</button>
          </div>
        </div>
        <div className={styles.list}>
          {!loading && tasks.length === 0 && (
            <div className={styles.muted}>No scheduled tasks</div>
          )}
          {tasks.map(task => (
            <button
              key={task.id}
              className={`${styles.item} ${selected === task.id ? styles.selected : ''}`}
              onClick={() => handleSelect(task.id)}
            >
              <div className={styles.itemTop}>
                <span className={styles.itemName}>{task.name}</span>
                {!task.enabled && <span className={styles.disabledBadge}>disabled</span>}
              </div>
              <div className={styles.itemMeta}>{formatSchedule(task.schedule)}</div>
              {task.state.nextRunAt && (
                <div className={styles.itemNext}>next: {new Date(task.state.nextRunAt).toLocaleString()}</div>
              )}
              {task.state.lastStatus === 'Error' && (
                <div className={styles.itemError}>last run failed</div>
              )}
            </button>
          ))}
        </div>
      </div>
      <div className={styles.main}>
        {showForm ? (
          <TaskForm
            form={form}
            setForm={setForm}
            conversations={conversations}
            mode={mode}
            saving={saving}
            error={error}
            onSave={handleSave}
            onCancel={handleCancel}
          />
        ) : !detail ? (
          <div className={styles.empty}>Select a task or click + to create one</div>
        ) : (
          <TaskDetail
            task={detail}
            conversations={conversations}
            error={error}
            onEdit={handleEdit}
            onDelete={handleDelete}
            onRunNow={handleRunNow}
          />
        )}
      </div>
    </div>
  );
}

function TaskDetail({
  task, conversations, error, onEdit, onDelete, onRunNow,
}: {
  task: ScheduledTask;
  conversations: ConversationSummary[];
  error: string | null;
  onEdit: () => void;
  onDelete: () => void;
  onRunNow: () => void;
}) {
  return (
    <>
      <div className={styles.detailHeader}>
        <div className={styles.detailRow}>
          <div>
            <div className={styles.detailName}>{task.name}</div>
            <div className={styles.detailId}>{task.id}</div>
          </div>
          <div className={styles.detailActions}>
            <button className={styles.button} onClick={onRunNow}>Run now</button>
            <button className={styles.button} onClick={onEdit}>Edit</button>
            <button className={styles.dangerButton} onClick={onDelete}>Delete</button>
          </div>
        </div>
        {error && <div className={styles.error}>{error}</div>}
      </div>
      <div className={styles.detailBody}>
        {task.description && (
          <Field label="Description" value={task.description} />
        )}
        <Field label="Schedule" value={formatSchedule(task.schedule)} />
        <Field label="Next run" value={formatTimestamp(task.state.nextRunAt)} />
        <Field label="Last run" value={formatTimestamp(task.state.lastRunAt)} />
        <Field label="Last status" value={task.state.lastStatus ?? '—'} />
        {task.state.lastError && (
          <Field label="Last error" value={task.state.lastError} className={styles.errorText} />
        )}
        <Field label="Enabled" value={task.enabled ? 'yes' : 'no'} />
        <Field label="Delete after run" value={task.deleteAfterRun ? 'yes' : 'no'} />
        <Field label="Conversation" value={labelForConversationId(task.conversationId, conversations)} />
        <div className={styles.promptBlock}>
          <div className={styles.fieldLabel}>Prompt</div>
          <pre className={styles.promptContent}>{task.prompt}</pre>
        </div>
      </div>
    </>
  );
}

function Field({ label, value, className }: { label: string; value: string; className?: string }) {
  return (
    <div className={styles.field}>
      <span className={styles.fieldLabel}>{label}</span>
      <span className={`${styles.fieldValue} ${className ?? ''}`}>{value}</span>
    </div>
  );
}

function TaskForm({
  form, setForm, conversations, mode, saving, error, onSave, onCancel,
}: {
  form: FormState;
  setForm: (f: FormState) => void;
  conversations: ConversationSummary[];
  mode: 'create' | 'edit';
  saving: boolean;
  error: string | null;
  onSave: () => void;
  onCancel: () => void;
}) {
  const update = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    setForm({ ...form, [key]: value });

  return (
    <>
      <div className={styles.detailHeader}>
        <div className={styles.detailRow}>
          <div className={styles.detailName}>{mode === 'create' ? 'New task' : 'Edit task'}</div>
          <div className={styles.detailActions}>
            <button className={styles.button} onClick={onSave} disabled={saving || !form.name || !form.prompt}>
              {saving ? 'Saving...' : 'Save'}
            </button>
            <button className={styles.button} onClick={onCancel}>Cancel</button>
          </div>
        </div>
        {error && <div className={styles.error}>{error}</div>}
      </div>
      <div className={styles.formBody}>
        <div className={styles.formRow}>
          <label className={styles.formLabel}>Name</label>
          <input
            className={styles.formInput}
            value={form.name}
            onChange={e => update('name', e.target.value)}
            placeholder="e.g. Morning summary"
          />
        </div>
        <div className={styles.formRow}>
          <label className={styles.formLabel}>Description</label>
          <input
            className={styles.formInput}
            value={form.description}
            onChange={e => update('description', e.target.value)}
            placeholder="optional"
          />
        </div>
        <div className={styles.formRow}>
          <label className={styles.formLabel}>Prompt</label>
          <textarea
            className={styles.formTextarea}
            value={form.prompt}
            onChange={e => update('prompt', e.target.value)}
            rows={4}
            placeholder="What should the agent do when this task runs?"
          />
        </div>
        <div className={styles.formRow}>
          <label className={styles.formLabel}>Schedule</label>
          <select
            className={styles.formSelect}
            value={form.scheduleKind}
            onChange={e => update('scheduleKind', e.target.value as ScheduleKind)}
          >
            <option value="cron">Cron expression</option>
            <option value="intervalMs">Fixed interval</option>
            <option value="at">One-shot (at timestamp)</option>
          </select>
        </div>
        {form.scheduleKind === 'cron' && (
          <>
            <div className={styles.formRow}>
              <label className={styles.formLabel}>Cron</label>
              <input
                className={styles.formInput}
                value={form.cron}
                onChange={e => update('cron', e.target.value)}
                placeholder="0 9 * * *  (daily at 9am)"
              />
            </div>
            <div className={styles.formRow}>
              <label className={styles.formLabel}>Timezone</label>
              <input
                className={styles.formInput}
                value={form.timezone}
                onChange={e => update('timezone', e.target.value)}
                placeholder="Europe/Copenhagen (optional, default UTC)"
              />
            </div>
          </>
        )}
        {form.scheduleKind === 'intervalMs' && (
          <div className={styles.formRow}>
            <label className={styles.formLabel}>Interval (ms)</label>
            <input
              className={styles.formInput}
              type="number"
              value={form.intervalMs}
              onChange={e => update('intervalMs', e.target.value)}
              placeholder="60000"
            />
          </div>
        )}
        {form.scheduleKind === 'at' && (
          <div className={styles.formRow}>
            <label className={styles.formLabel}>At (ISO 8601)</label>
            <input
              className={styles.formInput}
              value={form.at}
              onChange={e => update('at', e.target.value)}
              placeholder="2026-04-10T09:00:00Z"
            />
          </div>
        )}
        <div className={styles.formRow}>
          <label className={styles.formLabel}>Conversation</label>
          <select
            className={styles.formSelect}
            value={form.conversationId}
            onChange={e => update('conversationId', e.target.value)}
          >
            <option value="">(auto-create new silent conversation)</option>
            {conversations.map(c => (
              <option key={c.id} value={c.id}>{formatConversationLabel(c)}</option>
            ))}
          </select>
        </div>
        <div className={styles.formRow}>
          <label className={styles.formLabel}>
            <input
              type="checkbox"
              checked={form.enabled}
              onChange={e => update('enabled', e.target.checked)}
            />{' '}
            Enabled
          </label>
        </div>
        {mode === 'create' && (
          <div className={styles.formRow}>
            <label className={styles.formLabel}>
              <input
                type="checkbox"
                checked={form.deleteAfterRun}
                onChange={e => update('deleteAfterRun', e.target.checked)}
              />{' '}
              Delete after first successful run
            </label>
          </div>
        )}
      </div>
    </>
  );
}
